using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.CheckpointRetention;
using Backend.Api.Data.InMemory;
using Backend.Api.Orchestrators;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Skills;
using Backend.Api.Workflows;

namespace Backend.Api.Tests;

/// <summary>
/// Lite 模式 <see cref="InMemoryCheckpointRetentionRepository"/> 的 parity 覆蓋:直接實例化生產類別
/// (不用 CheckpointRetentionApiTests 的 RetentionFake),逐條對照 Dapper 版 SQL 的候選條件。
/// 併發案同時釘住「取候選 → 只鎖自己讀 _acks」這個單向鎖序不會死鎖。
/// </summary>
public sealed class CheckpointRetentionInMemoryTests
{
    private static readonly DateTime FarFuture = DateTime.UtcNow.AddHours(1);

    [Fact]
    public async Task CompletedAgentRunPastRetention_IsACandidateWithD3Provenance()
    {
        var fixture = await FixtureAsync();
        var runId = await CompletedRunAsync(fixture, "candidate");

        var row = Assert.Single(await ListAsync(fixture));

        Assert.Equal(runId, row.RunId);
        Assert.Equal("agent_thread", row.Kind);
        Assert.Equal("d3", row.Source);
        Assert.Equal(1, row.KindOrder);
        Assert.Equal("demo-a", row.TenantId);
        Assert.Equal("admin-a", row.UserId);
        Assert.NotEqual(0, row.MaxGeneration);
    }

    [Fact]
    public async Task PendingApproval_ExcludesTheTerminalRun()
    {
        var fixture = await FixtureAsync();
        await CompletedRunAsync(fixture, "clean");
        var withApproval = await CancelledRunWithPendingApprovalAsync(fixture, "approval");

        var rows = await ListAsync(fixture);

        Assert.DoesNotContain(rows, row => row.RunId == withApproval);
        Assert.Single(rows);
    }

    [Fact]
    public async Task CommandStillAwaitingDispatchCompletion_ExcludesTheRun()
    {
        var fixture = await FixtureAsync();
        var undispatched = await CompletedRunAsync(fixture, "undispatched", completeDispatch: false);
        var dispatched = await CompletedRunAsync(fixture, "dispatched");

        var rows = await ListAsync(fixture);

        Assert.Equal(dispatched, Assert.Single(rows).RunId);
        Assert.DoesNotContain(rows, row => row.RunId == undispatched);
    }

    [Fact]
    public async Task RecoveryWindow_IsInclusiveOnTheLastWriteAndExclusiveJustBeforeIt()
    {
        // Dapper 的 recovery 條件是 `updated_at <= @recoveryBefore`(以及 lease 的同一個界)。
        // on-point = 最後一次寫入的時刻本身;off-point = 早它一個 tick。
        // lease 分支在 lite 這一側沒有公開路徑可觸發(每條 terminal 轉換都會清掉 lease),
        // 述詞仍照 Dapper 保留;這裡以同一個 recoveryBefore 界的 updated_at 分支取樣。
        var fixture = await FixtureAsync();
        await CompletedRunAsync(fixture, "window");
        var lastWrite = fixture.Clock.GetUtcNow().UtcDateTime;

        Assert.Single(await ListAsync(fixture, recoveryBefore: lastWrite));
        Assert.Empty(await ListAsync(fixture, recoveryBefore: lastWrite.AddTicks(-1)));
    }

    [Fact]
    public async Task RetentionWindow_IsInclusiveOnCompletionAndExclusiveJustBeforeIt()
    {
        var fixture = await FixtureAsync();
        await CompletedRunAsync(fixture, "retention");
        var completedAt = fixture.Clock.GetUtcNow().UtcDateTime;

        Assert.Single(await ListAsync(fixture, retentionBefore: completedAt));
        Assert.Empty(await ListAsync(fixture, retentionBefore: completedAt.AddTicks(-1)));
    }

    [Fact]
    public async Task AckedCandidate_DisappearsFromLaterPages()
    {
        var fixture = await FixtureAsync();
        var acked = await CompletedRunAsync(fixture, "acked");
        var kept = await CompletedRunAsync(fixture, "kept");
        Assert.Equal(2, (await ListAsync(fixture)).Count);

        await fixture.Retention.AckAsync("agent_thread", acked, 1, 0, "evidence:test", default);

        Assert.Equal(kept, Assert.Single(await ListAsync(fixture)).RunId);
    }

    [Fact]
    public async Task CursorPaging_WalksStrictlyForwardOverCompletedAtKindOrderAndRunId()
    {
        var fixture = await FixtureAsync();
        for (var i = 0; i < 3; i++)
        {
            await CompletedRunAsync(fixture, $"page-{i}");
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        await CompletedRootAsync(fixture);

        var all = await ListAsync(fixture, take: 100);
        Assert.Equal(4, all.Count);
        Assert.Equal("root_context", all[^1].Kind);
        Assert.Equal("d5-root", all[^1].Source);
        Assert.Equal(2, all[^1].KindOrder);
        Assert.Equal(0, all[^1].MaxGeneration);

        var page = await ListAsync(fixture, take: 2);
        Assert.Equal(all.Take(2), page);

        var cursor = new CheckpointRetentionPosition(page[^1].CompletedAt, page[^1].RunId, (byte)page[^1].KindOrder);
        Assert.Equal(all.Skip(2), await ListAsync(fixture, cursor: cursor, take: 100));

        // 嚴格大於:同一個 cursor 位置的那一列不得再出現。
        Assert.DoesNotContain(
            await ListAsync(fixture, cursor: cursor, take: 100),
            row => row.RunId == page[^1].RunId);
    }

    [Fact]
    public async Task ConcurrentListAckAndWrites_DoNotDeadlock()
    {
        var fixture = await FixtureAsync();
        var runId = await CompletedRunAsync(fixture, "concurrent");

        var work = Task.WhenAll(
            Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    await ListAsync(fixture);
                }
            }),
            Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    await fixture.Retention.AckAsync("root_context", Guid.NewGuid(), 0, 0, null, default);
                }
            }),
            Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    await fixture.Runs.GetAsync("demo-a", "admin-a", runId, default);
                    await fixture.Runs.CreateDirectAsync(
                        "demo-a", "admin-a", "ADMIN", fixture.AgentId, "m", $"writer-{i}", default);
                }
            }),
            Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    await fixture.Roots.GetAsync("demo-a", "admin-a", fixture.RootRunId, default);
                }
            }));

        Assert.True(
            await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5))) == work,
            "checkpoint retention 的候選收集發生死鎖或飢餓");
        await work;
    }

    private static async Task<IReadOnlyList<CheckpointRetentionRow>> ListAsync(
        Fixture fixture,
        DateTime? retentionBefore = null,
        DateTime? recoveryBefore = null,
        CheckpointRetentionPosition? cursor = null,
        int take = 100)
        => await fixture.Retention.ListAsync(
            retentionBefore ?? FarFuture, recoveryBefore ?? FarFuture, cursor, take, default);

    /// <summary>已完成、已 dispatch ACK、無 pending approval 的直接 Agent run(合格候選的基準形狀)。</summary>
    private static async Task<Guid> CompletedRunAsync(
        Fixture fixture, string key, bool completeDispatch = true)
    {
        var lease = await RunningRunAsync(fixture, key, completeDispatch);
        var completed = await fixture.Runs.TransitionAsync("demo-a", "admin-a", lease.RunId,
            new AgentRunTransitionRequest(lease.StateVersion, AgentRunStatuses.Completed,
                lease.Token, lease.Generation, lease.EventAckCursor,
                Checkpoint(lease.Generation), 1), default);
        Assert.Equal(AgentRunWriteStatus.Success, completed.Status);
        return lease.RunId;
    }

    /// <summary>terminal 但仍留著 pending approval 的 run:D7 的取消競態,Dapper 明確排除。</summary>
    private static async Task<Guid> CancelledRunWithPendingApprovalAsync(Fixture fixture, string key)
    {
        var running = await RunningRunAsync(fixture, key, completeDispatch: true);
        var created = await fixture.Approvals.CreateAsync("demo-a", "admin-a", running.RunId,
            new AgentRunApprovalCreateRequest(running.StateVersion, running.Token, running.Generation,
                Checkpoint(running.Generation), 1, "USER",
                new string('b', 64), fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, created.Status);

        var waiting = await fixture.Runs.GetAsync("demo-a", "admin-a", running.RunId, default);
        var release = await fixture.Runs.ClaimLeaseAsync("demo-a", "admin-a", running.RunId,
            new AgentRunLeaseRequest(waiting!.StateVersion, "workflow", 120), default);
        Assert.Equal(AgentRunWriteStatus.Success, release.Status);
        var cancelled = await fixture.Runs.TransitionAsync("demo-a", "admin-a", running.RunId,
            new AgentRunTransitionRequest(release.Lease!.Run.StateVersion, AgentRunStatuses.Cancelled,
                release.Lease.LeaseToken, release.Lease.LeaseGeneration, release.Lease.EventAckCursor,
                Checkpoint(release.Lease.LeaseGeneration), 2), default);
        Assert.Equal(AgentRunWriteStatus.Success, cancelled.Status);
        Assert.Contains(running.RunId, fixture.Approvals.PendingApprovalRunIds());
        return running.RunId;
    }

    private static async Task<RunningRun> RunningRunAsync(Fixture fixture, string key, bool completeDispatch)
    {
        var started = await fixture.Runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", fixture.AgentId, "message", key, default);
        Assert.Equal(AgentRunWriteStatus.Success, started.Status);
        var runId = started.Run!.Id;
        var commandId = started.Dispatch!.CommandId;
        var claim = await fixture.Runs.ClaimCommandAsync("demo-a", "admin-a", runId, commandId,
            new AgentRunCommandClaimRequest("workflow", 30), default);
        Assert.Equal(AgentRunWriteStatus.Success, claim.Status);
        if (completeDispatch)
        {
            Assert.Equal(AgentRunDispatchCompleteStatus.Success,
                await fixture.Runs.CompleteDispatchAsync(
                    "demo-a", "admin-a", runId, commandId, claim.Item!.ClaimToken, default));
        }

        var running = await fixture.Runs.TransitionAsync("demo-a", "admin-a", runId,
            new AgentRunTransitionRequest(claim.Item!.StateVersion, AgentRunStatuses.Running,
                claim.Item.LeaseToken, claim.Item.LeaseGeneration, claim.Item.EventAckCursor), default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        return new RunningRun(runId, running.Run!.StateVersion, claim.Item.LeaseToken,
            claim.Item.LeaseGeneration, claim.Item.EventAckCursor);
    }

    private static async Task CompletedRootAsync(Fixture fixture)
    {
        var claim = await fixture.Roots.ClaimCommandAsync(
            "demo-a", "admin-a", fixture.RootRunId, fixture.RootCommandId, "workflow", 60, default);
        Assert.NotNull(claim);
        var run = await fixture.Roots.GetAsync("demo-a", "admin-a", fixture.RootRunId, default);
        var transition = await fixture.Roots.TransitionAsync("demo-a", "admin-a", fixture.RootRunId,
            new OrchestratorRootTransitionRequest(
                run!.StateVersion, claim!.ClaimToken, claim.LeaseGeneration, "completed"), default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, transition.Status);
    }

    private static string Checkpoint(long generation)
        => $"v2:{generation}:{new string('d', 64)}:{Guid.NewGuid():D}";

    private sealed record RunningRun(
        Guid RunId, long StateVersion, string Token, long Generation, long EventAckCursor);

    private sealed record Fixture(
        InMemoryAgentRunRepository Runs,
        InMemoryAgentRunApprovalRepository Approvals,
        InMemoryOrchestratorRunRepository Roots,
        InMemoryCheckpointRetentionRepository Retention,
        ManualClock Clock,
        Guid AgentId,
        Guid RootRunId,
        Guid RootCommandId);

    private static async Task<Fixture> FixtureAsync()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(-30));
        var skills = new InMemorySkillRepository();
        var agents = new InMemoryAgentRepository(skills);
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var approvals = new InMemoryAgentRunApprovalRepository(runs, clock);
        var agent = await PublishedAgentAsync(agents);

        var workflows = new InMemoryWorkflowRepository();
        var rootWorkflow = await PublishedWorkflowAsync(workflows, "root", "orchestrator");
        var workerWorkflow = await PublishedWorkflowAsync(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await PublishedWorkflowAsync(workflows, "verifier", "agent-runtime");
        var workerId = Guid.NewGuid();
        var verifierId = Guid.NewGuid();
        var orchestrators = new StubOrchestrators(
            Guid.NewGuid(), OrchestratorDefinition(rootWorkflow, workerId, verifierId));
        var roots = new InMemoryOrchestratorRunRepository(
            orchestrators, workflows, new StubAgents(workerId, verifierId, workerWorkflow, verifierWorkflow));
        var root = await roots.CreateAsync("demo-a", "admin-a", "ADMIN", [], [],
            orchestrators.Id, "conversation", "root message", "root-key", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, root.Status);

        return new Fixture(
            runs, approvals, roots,
            new InMemoryCheckpointRetentionRepository(runs, roots, approvals),
            clock, agent.Id, root.Run!.Id, root.Dispatch!.CommandId);
    }

    private static async Task<Agent> PublishedAgentAsync(InMemoryAgentRepository agents)
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null, Name: null, Description: null, SystemPrompt: "test",
            ExecutionRoles: ["worker"], Capabilities: null, OutputContract: null,
            Audience: ["ADMIN"], AllowedTools: [], SkillBindings: null, KnowledgeSources: [],
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(TimeoutSeconds: 3600, StepBudget: 4),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync(
            "demo-a", "retention-agent", "保留", "test", definition, hash, "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync(
            "demo-a", agent!.Id, agent.DraftVersion, definition, hash, default));
        Assert.Equal(AgentWriteStatus.Success, (await agents.PublishAsync(
            "demo-a", agent.Id, agent.DraftVersion, definition, hash, "admin-a", default)).Status);
        return agent;
    }

    private static async Task<Guid> PublishedWorkflowAsync(
        InMemoryWorkflowRepository workflows, string name, string kind)
    {
        var item = await workflows.CreateAsync(
            "demo-a", name, kind, "{\"schemaVersion\":1}", "{}", "admin-a", default);
        Assert.True(await workflows.MarkValidatedAsync(
            "demo-a", item.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("demo-a", item.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}",
            WorkflowCompilerContracts.Current, "admin-a", default);
        return item.Workflow.Id;
    }

    private static string OrchestratorDefinition(Guid workflow, Guid worker, Guid verifier) => new JsonObject
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
        ["budgets"] = new JsonObject { { "maxContextRounds", 1 }, { "maxTasks", 2 }, { "maxChildRuns", 3 }, { "maxConcurrency", 1 }, { "maxRepairRounds", 1 }, { "tokenBudget", 10 }, { "timeoutSeconds", 600 } },
    }.ToJsonString();

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class StubOrchestrators(Guid id, string definition) : IOrchestratorRepository
    {
        public Guid Id => id;
        public Task<Orchestrator?> GetAsync(string t, Guid requested, CancellationToken ct) => Task.FromResult(requested == id ? new Orchestrator(requested, "root", "", true, 1, null, 1, definition, DateTime.UtcNow, DateTime.UtcNow) : null);
        public Task<string?> RevisionAsync(string t, Guid requested, int revision, CancellationToken ct) => Task.FromResult<string?>(requested == id && revision == 1 ? definition : null);
        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> CreateAsync(string a, string b, string c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> UpdateAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, CancellationToken e) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> PublishAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a, Guid b, CancellationToken c) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> RestoreAsync(string a, Guid b, int c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }

    private sealed class StubAgents(Guid worker, Guid verifier, Guid workerWorkflow, Guid verifierWorkflow) : IAgentRepository
    {
        private static string Definition(Guid workflow, string role) => new JsonObject { ["system_prompt"] = "", ["execution_roles"] = new JsonArray(role), ["capabilities"] = new JsonArray("research"), ["output_contract"] = new JsonObject(), ["audience"] = new JsonArray("role:ADMIN"), ["allowed_tools"] = new JsonArray(), ["skill_bindings"] = new JsonArray(), ["knowledge_sources"] = new JsonArray(), ["business_rules"] = new JsonObject { { "version", 1 }, { "rules", new JsonArray() } }, ["runtime_limits"] = new JsonObject(), ["runtime_workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } } }.ToJsonString();
        private (Guid Workflow, string Role) Source(Guid id) => (id == worker ? (workerWorkflow, "worker") : (verifierWorkflow, "verifier"));
        public Task<Agent?> GetAsync(string t, Guid id, CancellationToken ct) { if (id != worker && id != verifier) return Task.FromResult<Agent?>(null); var x = Source(id); var d = Definition(x.Workflow, x.Role); return Task.FromResult<Agent?>(new(id, x.Role, x.Role, "", true, 1, null, 1, d, SkillHash.Sha256(d), DateTime.UtcNow, DateTime.UtcNow)); }
        public Task<string?> GetRevisionDefinitionAsync(string t, Guid id, int r, CancellationToken ct) { var x = Source(id); return Task.FromResult<string?>(r == 1 && (id == worker || id == verifier) ? Definition(x.Workflow, x.Role) : null); }
        public async Task<IReadOnlyList<AgentRevisionInfo>> ListRevisionsAsync(string t, Guid id, CancellationToken ct) { var d = await GetRevisionDefinitionAsync(t, id, 1, ct); if (d is null) return []; var x = Source(id); return [new(1, "published", SkillHash.Sha256(d), x.Workflow, 1, [], "u", DateTime.UtcNow)]; }
        public Task<IReadOnlyList<AgentInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException(); public Task<Agent?> CreateAsync(string a, string b, string c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException(); public Task<AgentDraftResult> UpdateDraftAsync(string a, Guid b, long c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException(); public Task<IReadOnlyList<AgentValidationError>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<AgentPublishResult> PublishAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g, Backend.Api.PromptArtifacts.PromptManifestPin? h = null) => throw new NotSupportedException(); public Task<AgentPublishResult> RestoreAsync(string a, Guid b, int c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }
}
