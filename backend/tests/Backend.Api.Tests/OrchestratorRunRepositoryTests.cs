using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Orchestrators;
using Backend.Api.RuntimeDiscovery;
using Backend.Api.Workflows;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Tests;

public sealed class OrchestratorRunRepositoryTests
{
    [Fact]
    public async Task RootSnapshot_IsIndependentFromD3AgentRun_AndPinsPublishedGraphAndBudgets()
    {
        var workflows = new Data.InMemory.InMemoryWorkflowRepository();
        var workflow = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", workflow.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", workflow.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await CreateWorkflow(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await CreateWorkflow(workflows, "verifier", "agent-runtime");
        var workerId = Guid.NewGuid(); var verifierId = Guid.NewGuid();
        var definition = Definition(workflow.Workflow.Id, workerId, verifierId);
        var orchestrator = new StubOrchestrators(Guid.NewGuid(), definition);
        var agents = new StubAgents(workerId, verifierId, workerWorkflow, verifierWorkflow);
        var runs = new InMemoryOrchestratorRunRepository(orchestrator, workflows, agents);

        var created = await runs.CreateAsync("t", "u", "ADMIN", ["ops"], ["workflow.manage"], orchestrator.Id, "console-1", "plan it", "start-1", default);

        Assert.Equal(OrchestratorRunWriteStatus.Success, created.Status);
        Assert.NotNull(created.Run);
        Assert.Equal(1, created.Run!.OrchestratorRevision);
        Assert.Equal(1, created.Run.WorkflowRevision);
        var artifact = await runs.ExecutionArtifactAsync("t", "u", created.Run.Id, default);
        using var envelope = JsonDocument.Parse(artifact!);
        var snapshot = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.RootElement.GetProperty("snapshot_canonical_base64").GetString()!));
        using var doc = JsonDocument.Parse(snapshot);
        Assert.False(doc.RootElement.TryGetProperty("agent", out _));
        Assert.Equal(created.Run.SnapshotHash, doc.RootElement.GetProperty("snapshot_hash").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("limits").GetProperty("max_child_runs").GetInt32());
        Assert.Equal(workflow.Workflow.Id.ToString("D"), doc.RootElement.GetProperty("workflow_id").GetString());
        Assert.Single(doc.RootElement.GetProperty("workers").EnumerateArray());
        Assert.Equal(AgentExecutionContract.DefaultTokenBudget,
            doc.RootElement.GetProperty("workers")[0].GetProperty("token_cap").GetInt32());
        Assert.Equal(AgentExecutionContract.DefaultTokenBudget,
            doc.RootElement.GetProperty("verifier").GetProperty("token_cap").GetInt32());
        Assert.Equal("root-runtime-adapter-1", doc.RootElement.GetProperty("graph").GetProperty("runtime_adapter_version").GetString());
        Assert.Equal("plan it", doc.RootElement.GetProperty("root_input").GetProperty("message").GetString());
        Assert.True(DateTimeOffset.TryParse(doc.RootElement.GetProperty("root_input").GetProperty("observed_at").GetString(), out _));
        var claim = await runs.ClaimCommandAsync("t", "u", created.Run.Id, created.Dispatch!.CommandId, "workflow-1", 30, default);
        Assert.NotNull(claim);
        Assert.Equal(created.Run.SnapshotHash, claim!.SnapshotHash);
        Assert.True(claim.LeaseGeneration > 0);
        var renewed = await runs.RenewCommandAsync("t", "u", created.Run.Id, claim.CommandId, claim.ClaimToken, claim.LeaseGeneration, 30, default);
        Assert.NotNull(renewed);
        Assert.Equal(claim.LeaseGeneration, renewed!.LeaseGeneration);
        Assert.Equal(claim.ClaimToken, renewed.ClaimToken);
        Assert.Null(await runs.RenewCommandAsync("t", "u", created.Run.Id, claim.CommandId, "stale", claim.LeaseGeneration, 30, default));
        Assert.Equal(OrchestratorRunDispatchCompleteStatus.Conflict,
            await runs.CompleteDispatchAsync("t", "u", created.Run.Id, claim.CommandId, "wrong", default));
        Assert.Equal(OrchestratorRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync("t", "u", created.Run.Id, claim.CommandId, claim.ClaimToken, default));
        var reclaimable = await runs.CreateAsync("t", "u", "ADMIN", [], [], orchestrator.Id, "console-2", "reclaim", "start-3", default);
        var oldClaim = await runs.ClaimCommandAsync("t", "u", reclaimable.Run!.Id, reclaimable.Dispatch!.CommandId, "worker-old", 1, default);
        Assert.NotNull(oldClaim);
        await Task.Delay(1100);
        Assert.Null(await runs.RenewCommandAsync("t", "u", reclaimable.Run.Id, oldClaim!.CommandId, oldClaim.ClaimToken, oldClaim.LeaseGeneration, 30, default));
        var recovered = await runs.ClaimRecoveryAsync("worker-new", 10, 30, default);
        var recoveredClaim = Assert.Single(recovered.Items).Claim;
        Assert.Equal(oldClaim!.CommandId, recoveredClaim.CommandId);
        Assert.True(recoveredClaim.LeaseGeneration > oldClaim.LeaseGeneration);
        Assert.Equal(OrchestratorRunDispatchCompleteStatus.Conflict,
            await runs.CompleteDispatchAsync("t", "u", reclaimable.Run.Id, oldClaim.CommandId, oldClaim.ClaimToken, default));
        Assert.Null(await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("forged-cap", 1, "worker", workerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget + 1), default));
        var child = await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("research-a", 1, "worker", workerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default);
        Assert.NotNull(child);
        Assert.Equal(created.Run.Id, child!.OrchestratorRootRunId);
        Assert.Null(await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("research-a", 1, "worker", workerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default));
        Assert.Null(await runs.CreateChildAsync("t", "u", created.Run.Id,
            new OrchestratorChildCreateRequest("write", 1, "worker", workerId, 1,
                WriteIntent: true, TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default));

        var duplicate = await runs.CreateAsync("t", "u", "ADMIN", [], [], orchestrator.Id, "console-1", "new work", "start-2", default);
        Assert.Equal(OrchestratorRunWriteStatus.Conflict, duplicate.Status);
        var cancelled = await runs.CancelAsync("t", "u", created.Run.Id, "stop", "cancel-1", default);
        Assert.Equal("cancelled", cancelled.Run!.Status);
        var events = await runs.EventsAsync("t", "u", created.Run.Id, 0, 10, default);
        Assert.Equal(["run_created", "child_created", "run_cancelled"], events!.Events.Select(x => x.EventType));
        var firstPage = await runs.EventsAsync("t", "u", created.Run.Id, 0, 1, default);
        var secondPage = await runs.EventsAsync("t", "u", created.Run.Id, firstPage!.NextSequence, 1, default);
        var thirdPage = await runs.EventsAsync("t", "u", created.Run.Id, secondPage!.NextSequence, 1, default);
        Assert.Equal(1, firstPage.NextSequence);
        Assert.Equal(2, secondPage.NextSequence);
        Assert.Equal(3, thirdPage!.NextSequence);
        Assert.Equal(["run_created", "child_created", "run_cancelled"], new[] { firstPage.Events[0].EventType, secondPage.Events[0].EventType, thirdPage.Events[0].EventType });
    }

    [Fact]
    public async Task ActiveLookup_AndRootResume_AreOwnerScopedAmbiguitySafeAndCheckpointPinned()
    {
        var workflows = new Data.InMemory.InMemoryWorkflowRepository();
        var root = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", root.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", root.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await CreateWorkflow(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await CreateWorkflow(workflows, "verifier", "agent-runtime");
        var worker = Guid.NewGuid(); var verifier = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var orchestrators = new StubOrchestrators(first, Definition(root.Workflow.Id, worker, verifier), second);
        var runs = new InMemoryOrchestratorRunRepository(orchestrators, workflows, new StubAgents(worker, verifier, workerWorkflow, verifierWorkflow));

        var one = await runs.CreateAsync("t", "u", "USER", [], [], first, "shared", "one", "start-one", default);
        Assert.NotNull((await runs.FindActiveAsync("t", "u", "shared", default)).Run);
        Assert.Null((await runs.FindActiveAsync("t", "other-user", "shared", default)).Run);
        Assert.Null((await runs.FindActiveAsync("other-tenant", "u", "shared", default)).Run);
        var two = await runs.CreateAsync("t", "u", "USER", [], [], second, "shared", "two", "start-two", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, two.Status);
        var ambiguous = await runs.FindActiveAsync("t", "u", "shared", default);
        Assert.True(ambiguous.IsAmbiguous);
        Assert.Null(ambiguous.Run);

        var waiting = await runs.CreateAsync("t", "u", "USER", [], [], first, "resume", "need context", "start-wait", default);
        var startClaim = await runs.ClaimCommandAsync("t", "u", waiting.Run!.Id, waiting.Dispatch!.CommandId, "workflow", 30, default);
        Assert.NotNull(startClaim);
        var running = await runs.GetAsync("t", "u", waiting.Run.Id, default);
        var stale = await runs.TransitionAsync("t", "u", waiting.Run.Id,
            new(running!.StateVersion - 1, startClaim!.ClaimToken, startClaim.LeaseGeneration, "waiting_input", "rctx1:test:hash:mac", 1), default);
        Assert.Equal(OrchestratorRunWriteStatus.Conflict, stale.Status);
        var parked = await runs.TransitionAsync("t", "u", waiting.Run.Id,
            new(running.StateVersion, startClaim.ClaimToken, startClaim.LeaseGeneration, "waiting_input", "rctx1:test:hash:mac", 1), default);
        Assert.Equal("waiting_input", parked.Run!.Status);
        Assert.Equal(OrchestratorRunWriteStatus.NotFound, (await runs.ResumeAsync("t", "other-user", waiting.Run.Id, "details", "resume:resume-1", default)).Status);

        var resumed = await runs.ResumeAsync("t", "u", waiting.Run.Id, "details", "resume:resume-1", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, resumed.Status);
        var replay = await runs.ResumeAsync("t", "u", waiting.Run.Id, "details", "resume:resume-1", default);
        Assert.Equal(OrchestratorRunWriteStatus.Replay, replay.Status);
        Assert.Equal(resumed.Dispatch!.CommandId, replay.Dispatch!.CommandId);
        var conflictingReplay = await runs.ResumeAsync("t", "u", waiting.Run.Id, "different details", "resume:resume-1", default);
        Assert.Equal(OrchestratorRunWriteStatus.Conflict, conflictingReplay.Status);
        var resumeClaim = await runs.ClaimCommandAsync("t", "u", waiting.Run.Id, resumed.Dispatch.CommandId, "workflow-after-restart", 30, default);
        Assert.NotNull(resumeClaim);
        Assert.Equal("resume", resumeClaim!.CommandType);
        Assert.Equal("details", resumeClaim.ResumeInput);
        Assert.Equal("rctx1:test:hash:mac", resumeClaim.CheckpointRef);
        Assert.Equal(1, resumeClaim.CheckpointVersion);
        var replayRequest = new OrchestratorRunReplayRequest("resume", first, "details");
        var replayLookup = await runs.FindByIdempotencyKeyAsync("t", "u", "resume:resume-1", replayRequest, default);
        Assert.Equal(waiting.Run.Id, replayLookup.Run!.Id);
        Assert.Equal(resumed.Dispatch.CommandId, replayLookup.CommandId);
        var mismatch = await runs.FindByIdempotencyKeyAsync("t", "u", "resume:resume-1", new OrchestratorRunReplayRequest("resume", first, "changed"), default);
        Assert.True(mismatch.IsMismatch);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "t";
        context.Request.Headers["X-User-Id"] = "u";
        context.Request.Headers["Idempotency-Key"] = "resume:resume-1";
        var controller = new RuntimeDiscoveryController(null!, runs) { ControllerContext = new ControllerContext { HttpContext = context } };
        var apiReplay = await controller.Replay(replayRequest, default);
        var ok = Assert.IsType<OkObjectResult>(apiReplay);
        var body = Assert.IsType<ChatRunResponse>(ok.Value);
        Assert.Equal(resumed.Dispatch.CommandId, body.CommandId);
        var events = await runs.EventsAsync("t", "u", waiting.Run.Id, 0, 20, default);
        Assert.DoesNotContain(events!.Events, x => x.EventType == "child_created");
    }

    [Fact]
    public async Task DeadlineRecovery_TimesOutRoot_ReleasesActiveKey_AndFencesConcurrentClaimsAndTransitions()
    {
        var workflows = new Data.InMemory.InMemoryWorkflowRepository();
        var root = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", root.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", root.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await CreateWorkflow(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await CreateWorkflow(workflows, "verifier", "agent-runtime");
        var worker = Guid.NewGuid(); var verifier = Guid.NewGuid(); var orchestratorId = Guid.NewGuid();
        var runs = new InMemoryOrchestratorRunRepository(new StubOrchestrators(orchestratorId, Definition(root.Workflow.Id, worker, verifier)), workflows, new StubAgents(worker, verifier, workerWorkflow, verifierWorkflow));
        IOrchestratorRunRepository durable = runs;

        var expired = await runs.CreateAsync("t", "u", "USER", [], [], orchestratorId, "deadline", "work", "deadline-start", default);
        Assert.NotNull(await runs.CreateChildAsync("t", "u", expired.Run!.Id,
            new("child", 1, "worker", worker, 1, TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default));
        SetDeadline(runs, expired.Run.Id, DateTime.UtcNow.AddSeconds(-1));

        var recovery = await durable.ClaimRecoveryAsync("scrubber", 10, 30, default);
        Assert.Empty(recovery.Items);
        var timedOut = await runs.GetAsync("t", "u", expired.Run.Id, default);
        Assert.Equal("timed_out", timedOut!.Status);
        Assert.Equal("deadline_exceeded", timedOut.ErrorCode);
        Assert.Null((await runs.FindActiveAsync("t", "u", "deadline", default)).Run);
        var timeoutEvents = await runs.EventsAsync("t", "u", expired.Run.Id, 0, 20, default);
        Assert.Equal(1, timeoutEvents!.Events.Count(x => x.EventType == "root_timed_out"));
        var lateCancel = await runs.CancelAsync("t", "u", expired.Run.Id, "too late", "late-cancel", default);
        Assert.Equal(OrchestratorRunWriteStatus.InvalidState, lateCancel.Status);
        Assert.Equal("timed_out", (await runs.GetAsync("t", "u", expired.Run.Id, default))!.Status);
        Assert.Equal(timeoutEvents.Events.Count, (await runs.EventsAsync("t", "u", expired.Run.Id, 0, 20, default))!.Events.Count);
        Assert.Null(await durable.ClaimCommandAsync("t", "u", expired.Run.Id, expired.Dispatch!.CommandId, "late", 30, default));
        var replacement = await runs.CreateAsync("t", "u", "USER", [], [], orchestratorId, "deadline", "replacement", "deadline-replacement", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, replacement.Status);

        var cas = await runs.CreateAsync("t", "u", "USER", [], [], orchestratorId, "cas", "work", "cas-start", default);
        var claims = await Task.WhenAll(
            durable.ClaimCommandAsync("t", "u", cas.Run!.Id, cas.Dispatch!.CommandId, "worker-a", 30, default),
            durable.ClaimCommandAsync("t", "u", cas.Run.Id, cas.Dispatch.CommandId, "worker-b", 30, default));
        var claim = Assert.Single(claims, x => x is not null)!;
        var state = await runs.GetAsync("t", "u", cas.Run.Id, default);
        var transition = new OrchestratorRootTransitionRequest(state!.StateVersion, claim.ClaimToken, claim.LeaseGeneration, "completed", Result: JsonDocument.Parse("{}").RootElement.Clone());
        var terminal = await Task.WhenAll(
            durable.TransitionAsync("t", "u", cas.Run.Id, transition, default),
            durable.TransitionAsync("t", "u", cas.Run.Id, transition, default));
        Assert.Single(terminal, x => x.Status == OrchestratorRunWriteStatus.Success);
        var terminalEvents = await runs.EventsAsync("t", "u", cas.Run.Id, 0, 20, default);
        Assert.Equal(1, terminalEvents!.Events.Count(x => x.EventType == "root_terminal"));
    }

    private static void SetDeadline(InMemoryOrchestratorRunRepository repository, Guid runId, DateTime deadline)
    {
        var runs = (System.Collections.IDictionary)typeof(InMemoryOrchestratorRunRepository)
            .GetField("_runs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(repository)!;
        var entry = runs[runId]!;
        entry.GetType().GetField("Deadline")!.SetValue(entry, deadline);
    }

    private static async Task<Guid> CreateWorkflow(Data.InMemory.InMemoryWorkflowRepository workflows, string name, string kind)
    { var item = await workflows.CreateAsync("t", name, kind, "{\"schemaVersion\":1}", "{}", "u", default); await workflows.MarkValidatedAsync("t", item.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default); await workflows.PublishAsync("t", item.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default); return item.Workflow.Id; }
    private static JsonElement TaskEnvelope() => JsonDocument.Parse("""{"objective":"research","required_capabilities":["research"],"context":{"query":"q"},"context_provenance":[{"context_key":"query","source_type":"caller","source_id":"user","observed_at":"2026-01-01T00:00:00Z","content_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"write_intent":false,"delegation_depth":0,"repair_of":null}""").RootElement.Clone();
    private static string Definition(Guid workflow, Guid worker, Guid verifier) => new JsonObject
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

    private sealed class StubOrchestrators(Guid id, string definition, params Guid[] additionalIds) : IOrchestratorRepository
    {
        public Guid Id => id;
        private bool Has(Guid requested) => requested == id || additionalIds.Contains(requested);
        public Task<Orchestrator?> GetAsync(string t, Guid requested, CancellationToken ct) => Task.FromResult(Has(requested) ? new Orchestrator(requested, "root", "", true, 1, null, 1, definition, DateTime.UtcNow, DateTime.UtcNow) : null);
        public Task<string?> RevisionAsync(string t, Guid requested, int revision, CancellationToken ct) => Task.FromResult<string?>(Has(requested) && revision == 1 ? definition : null);
        public Task<IReadOnlyList<OrchestratorInfo>> ListAsync(string t, CancellationToken ct) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> CreateAsync(string a, string b, string c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> UpdateAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<IReadOnlyList<string>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, CancellationToken e) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> PublishAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<IReadOnlyList<OrchestratorRevisionInfo>> RevisionsAsync(string a, Guid b, CancellationToken c) => throw new NotSupportedException(); public Task<OrchestratorWriteResult> RestoreAsync(string a, Guid b, int c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }
    private sealed class StubAgents(Guid worker, Guid verifier, Guid workerWorkflow, Guid verifierWorkflow) : Backend.Api.Agents.IAgentRepository
    {
        private static string Definition(Guid workflow, string role) => new JsonObject { ["system_prompt"] = "", ["execution_roles"] = new JsonArray(role), ["capabilities"] = new JsonArray("research"), ["output_contract"] = new JsonObject(), ["audience"] = new JsonArray("role:ADMIN"), ["allowed_tools"] = new JsonArray(), ["skill_bindings"] = new JsonArray(), ["knowledge_sources"] = new JsonArray(), ["business_rules"] = new JsonObject { { "version", 1 }, { "rules", new JsonArray() } }, ["runtime_limits"] = new JsonObject(), ["runtime_workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } } }.ToJsonString();
        private (Guid Workflow, string Role) Source(Guid id) => (id == worker ? (workerWorkflow, "worker") : (verifierWorkflow, "verifier"));
        public Task<Backend.Api.Agents.Agent?> GetAsync(string t, Guid id, CancellationToken ct) { if (id != worker && id != verifier) return Task.FromResult<Backend.Api.Agents.Agent?>(null); var x = Source(id); var d = Definition(x.Workflow, x.Role); return Task.FromResult<Backend.Api.Agents.Agent?>(new(id, x.Role, x.Role, "", true, 1, null, 1, d, Skills.SkillHash.Sha256(d), DateTime.UtcNow, DateTime.UtcNow)); }
        public Task<string?> GetRevisionDefinitionAsync(string t, Guid id, int r, CancellationToken ct) { var x = Source(id); return Task.FromResult<string?>(r == 1 && (id == worker || id == verifier) ? Definition(x.Workflow, x.Role) : null); }
        public async Task<IReadOnlyList<Backend.Api.Agents.AgentRevisionInfo>> ListRevisionsAsync(string t, Guid id, CancellationToken ct) { var d = await GetRevisionDefinitionAsync(t, id, 1, ct); if (d is null) return []; var x = Source(id); return [new(1, "published", Skills.SkillHash.Sha256(d), x.Workflow, 1, [], "u", DateTime.UtcNow)]; }
        public Task<IReadOnlyList<Backend.Api.Agents.AgentInfo>> ListAsync(string a, CancellationToken b) => throw new NotSupportedException(); public Task<Backend.Api.Agents.Agent?> CreateAsync(string a, string b, string c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException(); public Task<Backend.Api.Agents.AgentDraftResult> UpdateDraftAsync(string a, Guid b, long c, string d, string e, string f, string g, CancellationToken h) => throw new NotSupportedException(); public Task<IReadOnlyList<Backend.Api.Agents.AgentValidationError>> ValidateReferencesAsync(string a, string b, CancellationToken c) => throw new NotSupportedException(); public Task<bool> MarkValidatedAsync(string a, Guid b, long c, string d, string e, CancellationToken f) => throw new NotSupportedException(); public Task<Backend.Api.Agents.AgentPublishResult> PublishAsync(string a, Guid b, long c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<Backend.Api.Agents.AgentPublishResult> RestoreAsync(string a, Guid b, int c, string d, string e, string f, CancellationToken g) => throw new NotSupportedException(); public Task<bool> SetEnabledAsync(string a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
    }
}

[Collection("Postgres")]
public sealed class OrchestratorRunRepositoryPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Tenant = "orchestratorrunrepo-d6";

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        await CleanupAsync();
    }

    public Task DisposeAsync() => CleanupAsync();

    [SkippableFact]
    public async Task ActiveLookupAndResumeReplay_AreAmbiguitySafeAndInputBound()
    {
        fixture.SkipIfUnavailable();
        var repo = new OrchestratorRunRepository(fixture.DataSource!);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var resume = Guid.NewGuid();
        await InsertAsync(first, "shared", Guid.NewGuid(), "queued");
        await InsertAsync(second, "shared", Guid.NewGuid(), "running");
        var active = await repo.FindActiveAsync(Tenant, "user", "shared", default);
        Assert.True(active.IsAmbiguous);
        Assert.Null(active.Run);

        await InsertAsync(resume, "resume", Guid.NewGuid(), "waiting_input");
        var accepted = await repo.ResumeAsync(Tenant, "user", resume, "first", "same-key", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, accepted.Status);
        var replay = await repo.ResumeAsync(Tenant, "user", resume, "first", "same-key", default);
        Assert.Equal(OrchestratorRunWriteStatus.Replay, replay.Status);
        Assert.Equal(accepted.Dispatch!.CommandId, replay.Dispatch!.CommandId);
        var conflicting = await repo.ResumeAsync(Tenant, "user", resume, "different", "same-key", default);
        Assert.Equal(OrchestratorRunWriteStatus.Conflict, conflicting.Status);
    }

    [SkippableFact]
    public async Task LogicalAttemptLookup_ReplaysTerminalResumeWithMatchedCommand_AndIsOwnerScoped()
    {
        fixture.SkipIfUnavailable();
        var repo = new OrchestratorRunRepository(fixture.DataSource!);
        var runId = Guid.NewGuid();
        const string chatAttempt = "chat:logical-attempt-terminal";
        const string resumeAttempt = "resume:logical-attempt-terminal";
        await InsertAsync(runId, "terminal-retry", Guid.NewGuid(), "waiting_input", idempotencyKey: chatAttempt);

        var resumed = await repo.ResumeAsync(Tenant, "user", runId, "details", resumeAttempt, default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, resumed.Status);
        var claim = await repo.ClaimCommandAsync(Tenant, "user", runId, resumed.Dispatch!.CommandId, "worker", 30, default);
        Assert.NotNull(claim);
        var state = await repo.GetAsync(Tenant, "user", runId, default);
        var terminal = await repo.TransitionAsync(Tenant, "user", runId,
            new(state!.StateVersion, claim!.ClaimToken, claim.LeaseGeneration, "completed",
                Result: JsonDocument.Parse("""{"aggregate":{"answer":"once"}}""").RootElement.Clone()), default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, terminal.Status);

        var replay = await repo.FindByIdempotencyKeyAsync(Tenant, "user", resumeAttempt, ReplayRequest("terminal-retry", terminal.Run!.OrchestratorId, "details"), default);
        Assert.False(replay.IsAmbiguous);
        Assert.Equal(runId, replay.Run!.Id);
        Assert.Equal("completed", replay.Run.Status);
        Assert.Equal(resumed.Dispatch.CommandId, replay.CommandId);
        Assert.True((await repo.FindByIdempotencyKeyAsync(Tenant, "user", resumeAttempt, ReplayRequest("terminal-retry", terminal.Run.OrchestratorId, "different"), default)).IsMismatch);
        Assert.True((await repo.FindByIdempotencyKeyAsync(Tenant, "user", resumeAttempt, ReplayRequest("other-conversation", terminal.Run.OrchestratorId, "details"), default)).IsMismatch);
        Assert.True((await repo.FindByIdempotencyKeyAsync(Tenant, "user", resumeAttempt, ReplayRequest("terminal-retry", Guid.NewGuid(), "details"), default)).IsMismatch);
        Assert.Null((await repo.FindByIdempotencyKeyAsync(Tenant, "other-user", resumeAttempt, ReplayRequest("terminal-retry", terminal.Run.OrchestratorId, "details"), default)).Run);
        Assert.Null((await repo.FindByIdempotencyKeyAsync("other-tenant", "user", resumeAttempt, ReplayRequest("terminal-retry", terminal.Run.OrchestratorId, "details"), default)).Run);

        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run WHERE id=@runId", new { runId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_command WHERE run_id=@runId AND command_type='resume'", new { runId }));
    }

    [SkippableFact]
    public async Task ResumeAfterAbsoluteDeadline_TimesOutInsteadOfRequeueing()
    {
        fixture.SkipIfUnavailable();
        var repo = new OrchestratorRunRepository(fixture.DataSource!);
        var runId = Guid.NewGuid();
        await InsertAsync(runId, "expired-resume", Guid.NewGuid(), "waiting_input", expired: true);

        var result = await repo.ResumeAsync(Tenant, "user", runId, "late clarification", "resume-after-deadline", default);

        Assert.Equal(OrchestratorRunWriteStatus.Conflict, result.Status);
        var run = await repo.GetAsync(Tenant, "user", runId, default);
        Assert.Equal("timed_out", run!.Status);
        Assert.Equal("deadline_exceeded", run.ErrorCode);
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_command WHERE run_id=@runId AND command_type='resume'", new { runId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@runId AND event_type='root_resumed'", new { runId }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@runId AND event_type='root_timed_out'", new { runId }));
    }

    [SkippableFact]
    public async Task DeadlineRecovery_UsesDatabaseClock_TimesOutCascadeLedger_AndFencesCas()
    {
        fixture.SkipIfUnavailable();
        var repo = new OrchestratorRunRepository(fixture.DataSource!);
        var expired = Guid.NewGuid();
        await InsertAsync(expired, "deadline", Guid.NewGuid(), "queued", expired: true, withStartCommand: true);
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            var agentRun = Guid.NewGuid(); var agentCommand = Guid.NewGuid(); var child = Guid.NewGuid();
            const string agentSnapshot = """{"caller":{"tenant_id":"orchestratorrunrepo-d6","user_id":"user","role":"USER"}}""";
            var hash = new string('b', 64);
            // This test exercises a root's cascade only. A fresh bootstrap deliberately has no
            // tenant Agent fixture, so use synthetic immutable child references while locally
            // bypassing only their FK triggers; caller/status constraints remain enforced.
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("SET LOCAL session_replication_role = replica; INSERT INTO agent_run(id,tenant_id,root_run_id,run_kind,user_id,caller_role,agent_id,agent_revision,workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,status,deadline_at,orchestrator_root_run_id) VALUES(@agentRun,@tenant,@root,'worker','user','USER',@agent,1,@workflow,1,@snapshot::jsonb,@bytes,@hash,'running',clock_timestamp()+interval '1 hour',@root); INSERT INTO agent_run_command(id,tenant_id,user_id,run_id,command_type,idempotency_key_sha256,request_sha256,command_input_sha256,dispatch_claim_owner,dispatch_claim_token_sha256,dispatch_claim_expires_at) VALUES(@agentCommand,@tenant,'user',@agentRun,'start',@commandHash,@hash,@hash,'worker',@hash,clock_timestamp()+interval '1 hour'); INSERT INTO orchestrator_run_child(id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,workflow_id,workflow_revision,agent_snapshot_sha256,agent_run_id,command_id,dispatch_artifact,status) VALUES(@child,@root,'child',1,'worker',@agent,1,@workflow,1,@hash,@agentRun,@agentCommand,'{}'::jsonb,'running')", new { agentRun, agentCommand, child, root = expired, tenant = Tenant, agent = Guid.NewGuid(), workflow = Guid.NewGuid(), snapshot = agentSnapshot, bytes = Encoding.UTF8.GetBytes(agentSnapshot), hash, commandHash = Guid.NewGuid().ToString("N") }, transaction);
            await transaction.CommitAsync();
        }
        Assert.Empty((await repo.ClaimRecoveryAsync("scrubber", 10, 30, default)).Items);
        var timedOut = await repo.GetAsync(Tenant, "user", expired, default);
        Assert.Equal("timed_out", timedOut!.Status);
        Assert.Equal("deadline_exceeded", timedOut.ErrorCode);
        Assert.Null((await repo.FindActiveAsync(Tenant, "user", "deadline", default)).Run);
        var lateCancel = await repo.CancelAsync(Tenant, "user", expired, "too late", "late-cancel", default);
        Assert.Equal(OrchestratorRunWriteStatus.InvalidState, lateCancel.Status);
        Assert.Equal("timed_out", (await repo.GetAsync(Tenant, "user", expired, default))!.Status);
        Assert.Null(await repo.ClaimCommandAsync(Tenant, "user", expired, timedOut.CommandId!.Value, "late", 30, default));
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal("cancelled", await connection.ExecuteScalarAsync<string>("SELECT status FROM orchestrator_run_child WHERE orchestrator_root_run_id=@id", new { id = expired }));
            Assert.Equal("cancelled", await connection.ExecuteScalarAsync<string>("SELECT status FROM agent_run WHERE orchestrator_root_run_id=@id", new { id = expired }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM agent_run_command c JOIN agent_run a ON a.id=c.run_id WHERE a.orchestrator_root_run_id=@id AND c.dispatch_completed_at IS NOT NULL AND c.dispatch_claim_owner IS NULL", new { id = expired }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@id AND event_type='root_timed_out'", new { id = expired }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@id AND event_type='run_cancelled'", new { id = expired }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_command WHERE run_id=@id AND command_type='cancel'", new { id = expired }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_command WHERE run_id=@id AND completed_at IS NOT NULL", new { id = expired }));
        }

        var cas = Guid.NewGuid();
        await InsertAsync(cas, "cas", Guid.NewGuid(), "queued", withStartCommand: true);
        var command = (await repo.GetAsync(Tenant, "user", cas, default))!.CommandId!.Value;
        var claims = await Task.WhenAll(
            repo.ClaimCommandAsync(Tenant, "user", cas, command, "worker-a", 30, default),
            repo.ClaimCommandAsync(Tenant, "user", cas, command, "worker-b", 30, default));
        var claim = Assert.Single(claims, x => x is not null)!;
        var state = await repo.GetAsync(Tenant, "user", cas, default);
        var request = new OrchestratorRootTransitionRequest(state!.StateVersion, claim.ClaimToken, claim.LeaseGeneration, "completed", Result: JsonDocument.Parse("{}").RootElement.Clone());
        var terminal = await Task.WhenAll(repo.TransitionAsync(Tenant, "user", cas, request, default), repo.TransitionAsync(Tenant, "user", cas, request, default));
        Assert.Single(terminal, x => x.Status == OrchestratorRunWriteStatus.Success);
        await using var eventsConnection = await fixture.DataSource!.OpenConnectionAsync();
        Assert.Equal(1, await eventsConnection.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@id AND event_type='root_terminal'", new { id = cas }));
    }

    private async Task InsertAsync(Guid id, string conversation, Guid orchestrator, string status, bool expired = false, bool withStartCommand = false, string? idempotencyKey = null)
    {
        const string snapshot = """{"limits":{"max_context_rounds":1,"max_tasks":1,"max_child_runs":1,"max_concurrency":1,"max_repair_rounds":0,"timeout_seconds":30},"token_budget":1}""";
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO orchestrator_run
            (id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,request_sha256,idempotency_key_sha256,status,checkpoint_ref,checkpoint_version,deadline_at)
            VALUES (@id,@tenant,'user','USER',@orchestrator,1,@conversation,@workflow,1,@snapshot::jsonb,@bytes,@hash,@hash,@keyHash,@status,'rctx1:test:hash:mac',1,clock_timestamp()+CASE WHEN @expired THEN interval '-1 second' ELSE interval '1 hour' END);
            """, new { id, tenant = Tenant, orchestrator, conversation, workflow = Guid.NewGuid(), snapshot, bytes = Encoding.UTF8.GetBytes(snapshot), hash = new string('a', 64), keyHash = Skills.SkillHash.Sha256(idempotencyKey ?? Guid.NewGuid().ToString("N")), status, expired });
        if (withStartCommand)
            await connection.ExecuteAsync("INSERT INTO orchestrator_run_command(id,run_id,command_type) VALUES(@command,@id,'start')", new { command = Guid.NewGuid(), id });
    }

    private static OrchestratorRunReplayRequest ReplayRequest(string conversation, Guid orchestrator, string message) => new(conversation, orchestrator, message);

    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM agent_run_command WHERE run_id IN (SELECT id FROM agent_run WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant)); DELETE FROM agent_run_event WHERE run_id IN (SELECT id FROM agent_run WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant)); DELETE FROM agent_run WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run_event WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run_command WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run_child WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run WHERE tenant_id=@tenant;", new { tenant = Tenant });
    }
}
