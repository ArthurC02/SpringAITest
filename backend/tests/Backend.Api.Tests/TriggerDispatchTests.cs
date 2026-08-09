using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Auth;
using Backend.Api.Data.InMemory;
using Backend.Api.Orchestrators;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Skills;
using Backend.Api.Triggers;
using Backend.Api.Workflows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

/// <summary>
/// O5 fire path (04-operations-trigger-plan.md §6.1/§6.3): the claim/lease exclusivity, at-most-once
/// root creation, the misfire window, cancel stopping future claims, and every fail-closed reason
/// code. HTTP-level gating lives in <see cref="TriggerApiTests"/>.
/// </summary>
public sealed class TriggerDispatchTests
{
    private static readonly DateTime Due = new(2030, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Target = Guid.Parse("b0000000-0000-4000-8000-000000000001");

    /// <summary>
    /// The store's clock. Every case below sets it far from the host wall clock, which is exactly
    /// what proves the dispatcher no longer consults <c>DateTime.UtcNow</c>: nothing would be due at
    /// 2030 under the real clock, and nothing would misfire at a 2030 boundary either.
    /// </summary>
    private sealed class TestClock(DateTime start)
    {
        public DateTime Now { get; set; } = start;

        public InMemoryTriggerRepository Repository() => new() { Clock = () => Now };
    }

    [Fact]
    public async Task RunOnce_FiresThroughTheExistingD5RootCommand_UsingTheImmutableGrantSnapshot()
    {
        var clock = new TestClock(Due);
        var triggers = clock.Repository();
        var trigger = await ScheduleAsync(triggers);
        var runs = new RecordingRuns();
        var dispatcher = Dispatcher(triggers, runs);

        Assert.Equal(1, await dispatcher.RunOnceAsync(default));

        var call = Assert.Single(runs.Calls);
        Assert.Equal("t", call.Tenant);
        // Snapshot identity, not the current account: role/groups/capabilities come from creation.
        Assert.Equal("op-a", call.User);
        Assert.Equal("ADMIN", call.Role);
        Assert.Equal(new[] { "ops" }, call.Groups);
        Assert.Equal(new[] { "workflow.manage" }, call.Capabilities);
        Assert.Equal(Target, call.Orchestrator);
        Assert.Equal("產生報表", call.Message);

        var occurrence = Assert.Single((await triggers.OccurrencesAsync("t", trigger.Id, null, 10, default))!);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, occurrence.Status);
        Assert.Equal(runs.Created.Single(), occurrence.RootRunId);
        // Server-owned conversation/idempotency identity, derived from the occurrence -- never from
        // the caller-supplied input mapping.
        Assert.Equal(TriggerDispatcher.ConversationId(occurrence.Id), call.Conversation);
        Assert.Equal(TriggerDispatcher.IdempotencyKey(occurrence.Id), call.Key);
        Assert.Equal(TriggerStatuses.Fired, (await triggers.GetAsync("t", trigger.Id, default))!.Status);
    }

    [Fact]
    public async Task RunOnce_BeforeDue_ClaimsNothing()
    {
        var triggers = new TestClock(Due.AddSeconds(-1)).Repository();
        await ScheduleAsync(triggers);
        var runs = new RecordingRuns();

        Assert.Equal(0, await Dispatcher(triggers, runs).RunOnceAsync(default));
        Assert.Empty(runs.Calls);
    }

    // On-point/off-point for the documented window: resolved exactly at the boundary still fires,
    // one second later is a misfire that is never backfilled. The verdict follows the injected store
    // clock alone -- the host wall clock is years away from both of these instants.
    [Theory]
    [InlineData(20, TriggerOccurrenceStatuses.Fired, TriggerStatuses.Fired)]
    [InlineData(21, TriggerOccurrenceStatuses.SkippedMisfired, TriggerStatuses.Misfired)]
    public async Task RunOnce_MisfireWindowBoundary(int lateSeconds, string occurrenceStatus, string triggerStatus)
    {
        var triggers = new TestClock(Due.AddSeconds(lateSeconds)).Repository();
        var trigger = await ScheduleAsync(triggers, window: 20);
        var runs = new RecordingRuns();

        await Dispatcher(triggers, runs).RunOnceAsync(default);

        Assert.Equal(occurrenceStatus, (await OccurrenceAsync(triggers, trigger)).Status);
        Assert.Equal(triggerStatus, (await triggers.GetAsync("t", trigger.Id, default))!.Status);
        Assert.Equal(occurrenceStatus == TriggerOccurrenceStatuses.Fired ? 1 : 0, runs.Calls.Count);
    }

    [Fact]
    public async Task Cancel_StopsFutureClaims_AndCreatesNoRun()
    {
        var triggers = new TestClock(Due).Repository();
        var trigger = await ScheduleAsync(triggers);
        Assert.Equal(
            TriggerWriteStatus.Success,
            (await triggers.CancelAsync("t", trigger.Id, trigger.Version, default)).Status);
        var runs = new RecordingRuns();

        Assert.Equal(0, await Dispatcher(triggers, runs).RunOnceAsync(default));

        Assert.Empty(runs.Calls);
        Assert.Equal(TriggerOccurrenceStatuses.SkippedCancelled, (await OccurrenceAsync(triggers, trigger)).Status);
    }

    // §6.3 fail-closed matrix. Each row is one equivalence class; every one must create no run and
    // record its own bounded reason code (never a raw downstream message).
    public static TheoryData<string, string> FailClosedCases => new()
    {
        { "principal-deleted", TriggerOccurrenceStatuses.FailedPrincipalUnavailable },
        { "principal-moved-tenant", TriggerOccurrenceStatuses.FailedPrincipalUnavailable },
        { "target-missing", TriggerOccurrenceStatuses.FailedTargetMissing },
        { "target-disabled", TriggerOccurrenceStatuses.FailedTargetUnpublished },
        { "target-unpublished", TriggerOccurrenceStatuses.FailedTargetUnpublished },
        { "target-revision-changed", TriggerOccurrenceStatuses.FailedTargetRevisionChanged },
        { "dispatch-disabled", TriggerOccurrenceStatuses.FailedDispatchDisabled },
        { "triggers-disabled", TriggerOccurrenceStatuses.FailedDispatchDisabled },
        { "root-rejected", TriggerOccurrenceStatuses.FailedRootRejected },
    };

    [Theory]
    [MemberData(nameof(FailClosedCases))]
    public async Task RunOnce_FailsClosed_WithBoundedReasonCode(string scenario, string expected)
    {
        var triggers = new TestClock(Due).Repository();
        var trigger = await ScheduleAsync(triggers);
        var runs = new RecordingRuns
        {
            Status = scenario == "root-rejected"
                ? OrchestratorRunWriteStatus.Conflict
                : OrchestratorRunWriteStatus.Success,
        };
        var orchestrators = scenario switch
        {
            "target-missing" => new FakeOrchestrators(exists: false),
            "target-disabled" => new FakeOrchestrators(enabled: false),
            "target-unpublished" => new FakeOrchestrators(published: null),
            "target-revision-changed" => new FakeOrchestrators(published: 2),
            _ => new FakeOrchestrators(),
        };
        var users = scenario switch
        {
            "principal-deleted" => new FakeUsers(username: null),
            "principal-moved-tenant" => new FakeUsers(tenant: "other-tenant"),
            _ => new FakeUsers(),
        };
        var state = scenario switch
        {
            "dispatch-disabled" => new AgentTriggersState(true, false),
            "triggers-disabled" => new AgentTriggersState(false, true),
            _ => new AgentTriggersState(true, true),
        };

        await new TriggerDispatcher(
                triggers, orchestrators, runs, users, state, NullLogger<TriggerDispatcher>.Instance)
            .RunOnceAsync(default);

        var occurrence = await OccurrenceAsync(triggers, trigger);
        Assert.Equal(expected, occurrence.Status);
        Assert.Null(occurrence.RootRunId);
        Assert.Equal(TriggerStatuses.Failed, (await triggers.GetAsync("t", trigger.Id, default))!.Status);
        Assert.Empty(runs.Created);
    }

    [Fact]
    public async Task ConcurrentDueClaims_ProduceExactlyOneRootRun()
    {
        var triggers = new TestClock(Due).Repository();
        var trigger = await ScheduleAsync(triggers);
        var runs = new RecordingRuns();

        var claimed = await Task.WhenAll(
            Dispatcher(triggers, runs).RunOnceAsync(default),
            Dispatcher(triggers, runs).RunOnceAsync(default),
            Dispatcher(triggers, runs).RunOnceAsync(default));

        Assert.Equal(1, claimed.Sum());
        Assert.Single(runs.Created);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, (await OccurrenceAsync(triggers, trigger)).Status);
    }

    [Fact]
    public async Task RepeatedPasses_NeverRefireACompletedOccurrence()
    {
        var clock = new TestClock(Due);
        var triggers = clock.Repository();
        var trigger = await ScheduleAsync(triggers);
        var runs = new RecordingRuns();
        var dispatcher = Dispatcher(triggers, runs);

        Assert.Equal(1, await dispatcher.RunOnceAsync(default));
        clock.Now = Due.AddDays(1);
        Assert.Equal(0, await dispatcher.RunOnceAsync(default));

        var rootRunId = Assert.Single(runs.Created);
        Assert.Equal(rootRunId, (await OccurrenceAsync(triggers, trigger)).RootRunId);
    }

    // Restart with a half-finished pass: the run was durably created but the outcome was never
    // recorded. The expired lease is reclaimed, the same derived idempotency key replays at D5, and
    // the occurrence lands on the *same* root -- never a second one.
    [Fact]
    public async Task ExpiredLeaseAfterCrash_ReplaysTheSameRootRun()
    {
        var clock = new TestClock(Due);
        var inner = clock.Repository();
        var triggers = new FailingCompleteTriggers(inner, failures: 1);
        var trigger = await ScheduleAsync(inner);
        var runs = new RecordingRuns();
        var dispatcher = Dispatcher(triggers, runs);

        Assert.Equal(1, await dispatcher.RunOnceAsync(default));
        var midFlight = await OccurrenceAsync(inner, trigger);
        Assert.Equal(TriggerOccurrenceStatuses.Claimed, midFlight.Status);
        Assert.Null(midFlight.RootRunId);

        clock.Now = Due.AddSeconds(TriggerDispatcher.LeaseSeconds + 1);
        Assert.Equal(1, await dispatcher.RunOnceAsync(default));

        Assert.Equal(2, runs.Calls.Count);
        var rootRunId = Assert.Single(runs.Created);
        var final = await OccurrenceAsync(inner, trigger);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, final.Status);
        Assert.Equal(rootRunId, final.RootRunId);
    }

    // "No response at all" equivalence class: the durable authority throws instead of answering.
    // The occurrence must stay leased for a later retry -- never burned as a terminal failure --
    // and one bad claim must not abort the rest of the batch.
    [Fact]
    public async Task DownstreamTransportFailure_LeavesOccurrenceRetryable_AndDoesNotAbortTheBatch()
    {
        var clock = new TestClock(Due);
        var triggers = clock.Repository();
        var failing = await ScheduleAsync(triggers, name: "failing");
        var healthy = await ScheduleAsync(triggers, name: "healthy");
        // The throwing branch is selected by conversation id, which is occurrence-derived.
        var runs = new RecordingRuns
        {
            ThrowForConversation = TriggerDispatcher.ConversationId(
                TriggerOccurrenceId.For(failing.Id, failing.FireAt)),
        };

        Assert.Equal(2, await Dispatcher(triggers, runs).RunOnceAsync(default));

        Assert.Equal(TriggerOccurrenceStatuses.Claimed, (await OccurrenceAsync(triggers, failing)).Status);
        Assert.Null((await OccurrenceAsync(triggers, failing)).RootRunId);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, (await OccurrenceAsync(triggers, healthy)).Status);

        // Retryable, not burned: once the downstream recovers, the expired lease fires normally.
        runs.ThrowForConversation = null;
        clock.Now = Due.AddSeconds(TriggerDispatcher.LeaseSeconds + 1);
        await Dispatcher(triggers, runs).RunOnceAsync(default);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, (await OccurrenceAsync(triggers, failing)).Status);
    }

    [Fact]
    public void OccurrenceIdentity_IsStableAcrossRestarts_AndDistinctPerTriggerAndInstant()
    {
        var trigger = Guid.NewGuid();

        Assert.Equal(TriggerOccurrenceId.For(trigger, Due), TriggerOccurrenceId.For(trigger, Due));
        Assert.NotEqual(TriggerOccurrenceId.For(trigger, Due), TriggerOccurrenceId.For(trigger, Due.AddSeconds(1)));
        Assert.NotEqual(TriggerOccurrenceId.For(trigger, Due), TriggerOccurrenceId.For(Guid.NewGuid(), Due));
    }

    /// <summary>
    /// The one end-to-end check against the real D5 authority: a fired trigger produces an ordinary
    /// immutable root run — same pinned revisions and snapshot hash an operator-started root gets —
    /// because the dispatcher calls the existing command path rather than a second creation path.
    /// </summary>
    [Fact]
    public async Task FiredRoot_IsAnOrdinaryImmutableD5RootRun()
    {
        var workflows = new InMemoryWorkflowRepository();
        var root = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", root.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", root.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await PublishedWorkflowAsync(workflows, "worker");
        var verifierWorkflow = await PublishedWorkflowAsync(workflows, "verifier");
        var workerId = Guid.NewGuid();
        var verifierId = Guid.NewGuid();
        var orchestrators = new StubOrchestrators(Target, RootDefinition(root.Workflow.Id, workerId, verifierId));
        var d5 = new InMemoryOrchestratorRunRepository(
            orchestrators, workflows, new StubAgents(workerId, verifierId, workerWorkflow, verifierWorkflow));
        var triggers = new TestClock(Due).Repository();
        var trigger = await ScheduleAsync(triggers);

        var fired = await new TriggerDispatcher(
                triggers, orchestrators, d5, new FakeUsers(), new AgentTriggersState(true, true),
                NullLogger<TriggerDispatcher>.Instance)
            .RunOnceAsync(default);

        Assert.Equal(1, fired);
        var occurrence = await OccurrenceAsync(triggers, trigger);
        Assert.Equal(TriggerOccurrenceStatuses.Fired, occurrence.Status);
        var run = await d5.GetAsync("t", "op-a", occurrence.RootRunId!.Value, default);
        Assert.NotNull(run);
        Assert.Equal(Target, run!.OrchestratorId);
        Assert.Equal(1, run.OrchestratorRevision);
        Assert.Equal(root.Workflow.Id, run.WorkflowId);
        Assert.Equal("queued", run.Status);
        Assert.False(string.IsNullOrWhiteSpace(run.SnapshotHash));
        Assert.Equal(TriggerDispatcher.ConversationId(occurrence.Id), run.ConversationId);
    }

    private static TriggerDispatcher Dispatcher(ITriggerRepository triggers, IOrchestratorRunRepository runs)
        => new(triggers, new FakeOrchestrators(), runs, new FakeUsers(),
            new AgentTriggersState(true, true), NullLogger<TriggerDispatcher>.Instance);

    private static async Task<Trigger> ScheduleAsync(
        ITriggerRepository triggers, int window = 300, string name = "nightly")
    {
        var result = await triggers.CreateAsync(
            "t",
            new TriggerPrincipal("op-a", "ADMIN", ["ops"], ["workflow.manage"]),
            new TriggerCreateInput(name, "", Target, 1, "{\"message\":\"產生報表\"}", Due, window),
            default);
        Assert.Equal(TriggerWriteStatus.Success, result.Status);
        return result.Trigger!;
    }

    private static async Task<TriggerOccurrence> OccurrenceAsync(ITriggerRepository triggers, Trigger trigger)
        => Assert.Single((await triggers.OccurrencesAsync("t", trigger.Id, null, 10, default))!);

    private static async Task<Guid> PublishedWorkflowAsync(InMemoryWorkflowRepository workflows, string name)
    {
        var item = await workflows.CreateAsync("t", name, "agent-runtime", "{\"schemaVersion\":1}", "{}", "u", default);
        await workflows.MarkValidatedAsync("t", item.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default);
        await workflows.PublishAsync("t", item.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        return item.Workflow.Id;
    }

    private static string RootDefinition(Guid workflow, Guid worker, Guid verifier) => new JsonObject
    {
        ["instructions"] = "root",
        ["policy"] = new JsonObject { { "dispatchMode", "bounded-parallel" }, { "joinPolicy", "repair" }, { "repairPolicy", "redispatch" }, { "aggregationPolicy", "verified-only" }, { "denialPolicy", "fail-closed" } },
        ["workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } },
        ["verifier"] = new JsonObject { { "agentId", verifier.ToString("D") }, { "revision", 1 }, { "variant", "read-only" }, { "independent", true }, { "outputContract", new JsonObject { { "type", "verification-report" } } } },
        ["workerPool"] = new JsonArray(new JsonObject { { "agentId", worker.ToString("D") }, { "revision", 1 } }),
        ["workerPolicy"] = new JsonObject { { "requiredAudience", new JsonArray() }, { "requiredCapabilities", new JsonArray() }, { "selection", "pinned-only" } },
        ["context"] = new JsonObject { { "readOnly", true }, { "allowedTools", new JsonArray() }, { "knowledgeSources", new JsonArray() } },
        ["audience"] = new JsonArray(),
        ["capabilities"] = new JsonArray(),
        ["budgets"] = new JsonObject { { "maxContextRounds", 1 }, { "maxTasks", 2 }, { "maxChildRuns", 3 }, { "maxConcurrency", 1 }, { "maxRepairRounds", 1 }, { "tokenBudget", 10 }, { "timeoutSeconds", 10 } },
    }.ToJsonString();

    private sealed class FakeOrchestrators(bool exists = true, bool enabled = true, int? published = 1)
        : IOrchestratorRepository
    {
        public Task<Orchestrator?> GetAsync(string tenant, Guid id, CancellationToken ct)
            => Task.FromResult(exists
                ? new Orchestrator(id, "root", "", enabled, 1, null, published, "{}", DateTime.UtcNow, DateTime.UtcNow)
                : null);

        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> CreateAsync(string a, string b, string c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> UpdateAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException();
        public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> PublishAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a, Guid b, CancellationToken c) => throw new NotSupportedException();
        public Task<string?> RevisionAsync(string a, Guid b, int c, CancellationToken d) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> RestoreAsync(string a, Guid b, int c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }

    private sealed class StubOrchestrators(Guid id, string definition) : IOrchestratorRepository
    {
        public Task<Orchestrator?> GetAsync(string t, Guid requested, CancellationToken ct)
            => Task.FromResult(requested == id
                ? new Orchestrator(requested, "root", "", true, 1, null, 1, definition, DateTime.UtcNow, DateTime.UtcNow)
                : null);

        public Task<string?> RevisionAsync(string t, Guid requested, int revision, CancellationToken ct)
            => Task.FromResult<string?>(requested == id && revision == 1 ? definition : null);

        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> CreateAsync(string a, string b, string c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> UpdateAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException();
        public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> PublishAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a, Guid b, CancellationToken c) => throw new NotSupportedException();
        public Task<OrchestratorWriteResult> RestoreAsync(string a, Guid b, int c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }

    private sealed class StubAgents(Guid worker, Guid verifier, Guid workerWorkflow, Guid verifierWorkflow)
        : IAgentRepository
    {
        private static string Definition(Guid workflow, string role) => new JsonObject { ["system_prompt"] = "", ["execution_roles"] = new JsonArray(role), ["capabilities"] = new JsonArray("research"), ["output_contract"] = new JsonObject(), ["audience"] = new JsonArray("role:ADMIN"), ["allowed_tools"] = new JsonArray(), ["skill_bindings"] = new JsonArray(), ["knowledge_sources"] = new JsonArray(), ["business_rules"] = new JsonObject { { "version", 1 }, { "rules", new JsonArray() } }, ["runtime_limits"] = new JsonObject(), ["runtime_workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } } }.ToJsonString();
        private (Guid Workflow, string Role) Source(Guid id) => (id == worker ? (workerWorkflow, "worker") : (verifierWorkflow, "verifier"));
        public Task<Agent?> GetAsync(string t, Guid id, CancellationToken ct) { if (id != worker && id != verifier) return Task.FromResult<Agent?>(null); var x = Source(id); var d = Definition(x.Workflow, x.Role); return Task.FromResult<Agent?>(new(id, x.Role, x.Role, "", true, 1, null, 1, d, SkillHash.Sha256(d), DateTime.UtcNow, DateTime.UtcNow)); }
        public Task<string?> GetRevisionDefinitionAsync(string t, Guid id, int r, CancellationToken ct) { var x = Source(id); return Task.FromResult<string?>(r == 1 && (id == worker || id == verifier) ? Definition(x.Workflow, x.Role) : null); }
        public async Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(string t, Guid id, CancellationToken ct) { var d = await GetRevisionDefinitionAsync(t, id, 1, ct); if (d is null) return []; var x = Source(id); return [new(1, "published", SkillHash.Sha256(d), x.Workflow, 1, [], "u", DateTime.UtcNow)]; }
        public Task<IReadOnlyList<AgentInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException();
        public Task<Agent?> CreateAsync(string a, string b, string c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException();
        public Task<AgentDraftResult> UpdateDraftAsync(string a, Guid b, long c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException();
        public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentPublishResult> PublishAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g, Backend.Api.PromptArtifacts.PromptManifestPin? h = null) => throw new NotSupportedException();
        public Task<AgentPublishResult> RestoreAsync(string a, Guid b, int c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException();
        public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }

    private sealed class FakeUsers(string? username = "op-a", string tenant = "t") : IAuthRepository
    {
        public Task<UserRow?> FindUserByUsernameAsync(string requested, CancellationToken ct)
            => Task.FromResult(username is not null && string.Equals(username, requested, StringComparison.Ordinal)
                ? new UserRow(requested, "", "ADMIN", tenant)
                : null);

        public Task<TenantRow?> FindTenantByCodeAsync(string a, CancellationToken b) => throw new NotSupportedException();
        public Task<bool> UsernameExistsAsync(string a, CancellationToken b) => throw new NotSupportedException();
        public Task AddUserAsync(string a, string b, string c, long d, CancellationToken e) => throw new NotSupportedException();
    }

    /// <summary>Records what reached the D5 command path and replays a repeated idempotency key like the real authority.</summary>
    private sealed class RecordingRuns : IOrchestratorRunRepository
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, OrchestratorRunResponse> _byKey = new(StringComparer.Ordinal);

        public List<Call> Calls { get; } = [];
        public List<Guid> Created { get; } = [];
        public OrchestratorRunWriteStatus Status { get; init; } = OrchestratorRunWriteStatus.Success;
        public string? ThrowForConversation { get; set; }

        public Task<OrchestratorRunWriteResult> CreateAsync(
            string tenantId, string userId, string role, IReadOnlyCollection<string> groups,
            IReadOnlyCollection<string> capabilities, Guid orchestratorId, string conversationId,
            string message, string idempotencyKey, CancellationToken ct)
        {
            lock (_gate)
            {
                Calls.Add(new Call(
                    tenantId, userId, role, groups.ToArray(), capabilities.ToArray(), orchestratorId,
                    conversationId, message, idempotencyKey));
                if (ThrowForConversation is { } failing
                    && string.Equals(failing, conversationId, StringComparison.Ordinal))
                {
                    throw new HttpRequestException("downstream unavailable");
                }
                if (Status != OrchestratorRunWriteStatus.Success)
                {
                    return Task.FromResult(new OrchestratorRunWriteResult(Status, Message: "rejected"));
                }
                if (_byKey.TryGetValue(idempotencyKey, out var replayed))
                {
                    return Task.FromResult(new OrchestratorRunWriteResult(
                        OrchestratorRunWriteStatus.Replay, replayed, Replayed: true));
                }

                var run = Response(orchestratorId, conversationId);
                _byKey.Add(idempotencyKey, run);
                Created.Add(run.Id);
                return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Success, run));
            }
        }

        private static OrchestratorRunResponse Response(Guid orchestratorId, string conversationId)
            => new(
                Guid.NewGuid(), orchestratorId, 1, conversationId, Guid.NewGuid(), 1, new string('a', 64),
                "queued", false, 1, DateTime.UtcNow.AddMinutes(5),
                JsonDocument.Parse("{}").RootElement.Clone(), DateTime.UtcNow, DateTime.UtcNow);

        public sealed record Call(
            string Tenant, string User, string Role, string[] Groups, string[] Capabilities,
            Guid Orchestrator, string Conversation, string Message, string Key);

        public Task<OrchestratorRunResponse?> GetAsync(string a, string b, Guid c, CancellationToken d) => throw new NotSupportedException();
        public Task<OrchestratorRunActiveLookup> FindActiveAsync(string a, string b, string c, CancellationToken d) => throw new NotSupportedException();
        public Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string a, string b, string c, OrchestratorRunReplayRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorRunEventsResponse?> EventsAsync(string a, string b, Guid c, long d, int e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorRunWriteResult> CancelAsync(string a, string b, Guid c, string? d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorRunWriteResult> ResumeAsync(string a, string b, Guid c, string d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<string?> ExecutionArtifactAsync(string a, string b, Guid c, CancellationToken d) => throw new NotSupportedException();
        public Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string a, string b, Guid c, Guid d, string e, int f, CancellationToken g) => throw new NotSupportedException();
        public Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string a, string b, Guid c, Guid d, string e, long f, int g, CancellationToken h) => throw new NotSupportedException();
        public Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string a, string b, Guid c, Guid d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string a, int b, int c, CancellationToken d) => throw new NotSupportedException();
        public Task<OrchestratorChildResponse?> CreateChildAsync(string a, string b, Guid c, OrchestratorChildCreateRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorChildStatusResponse?> GetChildAsync(string a, string b, Guid c, Guid d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorRunWriteResult> TransitionAsync(string a, string b, Guid c, OrchestratorRootTransitionRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string a, string b, Guid c, OrchestratorContextAcquireRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorContextRequestResponse?> GetOrCreateContextRequestAsync(string a, string b, Guid c, Guid d, CancellationToken e) => throw new NotSupportedException();
        public Task<OrchestratorContextRequestResponse?> GetContextRequestAsync(string a, string b, Guid c, Guid d, Guid e, CancellationToken f) => throw new NotSupportedException();
        public Task<OrchestratorContextDeltaResult> AppendContextDeltaAsync(string a, string b, Guid c, Guid d, Guid e, long f, OrchestratorContextDeltaRequest g, CancellationToken h) => throw new NotSupportedException();
    }

    /// <summary>Simulates a worker that dies between creating the durable run and recording the outcome.</summary>
    private sealed class FailingCompleteTriggers(ITriggerRepository inner, int failures) : ITriggerRepository
    {
        private int _remaining = failures;

        public Task<bool> CompleteOccurrenceAsync(
            Guid occurrenceId, string claimToken, string status, Guid? rootRunId, CancellationToken ct)
        {
            if (_remaining-- > 0)
            {
                throw new InvalidOperationException("crash before recording the outcome");
            }
            return inner.CompleteOccurrenceAsync(occurrenceId, claimToken, status, rootRunId, ct);
        }

        public Task<TriggerWriteResult> CreateAsync(string a, TriggerPrincipal b, TriggerCreateInput c, CancellationToken d) => inner.CreateAsync(a, b, c, d);
        public Task<IReadOnlyList<Trigger>> ListAsync(string a, int b, CancellationToken c) => inner.ListAsync(a, b, c);
        public Task<Trigger?> GetAsync(string a, Guid b, CancellationToken c) => inner.GetAsync(a, b, c);
        public Task<TriggerWriteResult> CancelAsync(string a, Guid b, long c, CancellationToken d) => inner.CancelAsync(a, b, c, d);
        public Task<IReadOnlyList<TriggerOccurrence>?> OccurrencesAsync(string a, Guid b, TriggerOccurrencePosition? c, int d, CancellationToken e) => inner.OccurrencesAsync(a, b, c, d, e);
        public Task<TriggerClaimBatch> ClaimDueAsync(string a, int b, int c, CancellationToken d) => inner.ClaimDueAsync(a, b, c, d);
    }
}
