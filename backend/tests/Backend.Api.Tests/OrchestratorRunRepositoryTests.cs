using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Orchestrators;
using Backend.Api.RuntimeDiscovery;
using Backend.Api.Workflows;
using Backend.Api.Contexts;
using Backend.Api.Data.InMemory;
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
        // 畸形/非唯讀的 child 請求在生產倉儲是 ArgumentException(OrchestratorRunRepository.cs:244);
        // lite 若回 null 會被 controller 轉成 409,讓 Workflow 誤判成可重試的暫時性衝突。
        await Assert.ThrowsAsync<ArgumentException>(() => runs.CreateChildAsync("t", "u", created.Run.Id,
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
        // Dapper 的 root 終局事件帶 {status}(OrchestratorRunRepository.cs:299),前端的 trace
        // 就是靠它判定 root 收尾狀態(orchestratorTrace.ts:60-62);lite 回空物件會讓它靜靜變成 null。
        Assert.Equal("completed",
            Assert.Single(terminalEvents.Events, x => x.EventType == "root_terminal").Payload.GetProperty("status").GetString());
    }

    // 兩個獨立的預算判定各自用寬鬆的另一個把它孤立出來:上限剛好 2 的兩個 child 必須成功,第 3 個必須被拒。
    // 既有測試的兩個 Assert.Null 其實是重複 task 與 write_intent 造成的,預算這條分支從未被走到。
    [Theory]
    [InlineData(5, 2)]  // max_child_runs = 2 是唯一有效的上限
    [InlineData(2, 5)]  // max_concurrency = 2 是唯一有效的上限
    public async Task Child_BudgetCaps_AllowExactlyTheLimit_AndRejectTheNext(int maxConcurrency, int maxChildRuns)
    {
        var fixture = await FixtureAsync(maxChildRuns: maxChildRuns, maxConcurrency: maxConcurrency, maxTasks: 5);
        var created = await fixture.Runs.CreateAsync(
            "t", "u", "ADMIN", [], [], fixture.OrchestratorId, "budget", "plan", "budget-key", default);
        Assert.Equal(OrchestratorRunWriteStatus.Success, created.Status);

        Assert.NotNull(await ChildAsync(fixture, created.Run!.Id, "task-1"));
        Assert.NotNull(await ChildAsync(fixture, created.Run.Id, "task-2"));
        Assert.Null(await ChildAsync(fixture, created.Run.Id, "task-3"));
    }

    // AcquireContext 是 root AGENTS.md 明列的「絕不回 caller/model 自創 context」邊界:
    // 沒有伺服器擁有的唯讀 adapter 時必須回 ready:false 並列出缺什麼,而不是回一個空的成功。
    [Fact]
    public async Task AcquireContext_WithoutServerOwnedAdapter_ReturnsNotReadyWithMissingSources()
    {
        const string source = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var fixture = await FixtureAsync(contextTools: ["search_documents"], knowledgeSources: [source]);
        var created = await fixture.Runs.CreateAsync(
            "t", "u", "ADMIN", [], [], fixture.OrchestratorId, "ctx", "plan", "ctx-key", default);
        var runId = created.Run!.Id;
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var request = new OrchestratorContextAcquireRequest(1, empty, ["search_documents"], [source]);

        var result = await fixture.Runs.AcquireContextAsync("t", "u", runId, request, default);

        Assert.NotNull(result);
        Assert.False(result!.Ready);
        Assert.Equal(new[] { "context-tool:search_documents", "knowledge-source:" + source }, result.Missing);
        Assert.Equal("{}", result.Context.GetRawText());

        // 授權集合與 snapshot 權威不符 / context_round < 1 / current_context 非物件 / 非擁有者 → 一律 null(fail closed)。
        Assert.Null(await fixture.Runs.AcquireContextAsync("t", "u", runId, request with { AllowedTools = ["other_tool"] }, default));
        Assert.Null(await fixture.Runs.AcquireContextAsync("t", "u", runId, request with { AllowedKnowledgeSources = [] }, default));
        Assert.Null(await fixture.Runs.AcquireContextAsync("t", "u", runId, request with { ContextRound = 0 }, default));
        Assert.Null(await fixture.Runs.AcquireContextAsync("t", "u", runId, request with { CurrentContext = null }, default));
        Assert.Null(await fixture.Runs.AcquireContextAsync("t", "other-user", runId, request, default));
        Assert.Null(await fixture.Runs.AcquireContextAsync("other-tenant", "u", runId, request, default));
    }

    // H1:`current_context.user_input` 的澄清短路必須隨旗標分岔 —— 旗標關閉時位元同 E1 之前
    // (ready:true 原樣回傳 caller context),旗標開啟時改走新 revision(ready:false + 明確缺口碼)。
    // 這兩格原本全 repo 零覆蓋,是 E1 一度把關閉路徑一起改掉而沒被抓到的原因。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireContext_UserInputClarification_DependsOnTheEnrichmentFlag(bool enrichmentEnabled)
    {
        const string source = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var fixture = await FixtureAsync(contextTools: ["search_documents"], knowledgeSources: [source],
            contexts: enrichmentEnabled ? new InMemoryContextRepository(policyTenants: ["t"]) : null);
        var created = await fixture.Runs.CreateAsync("t", "u", "ADMIN", [], [], fixture.OrchestratorId, "clarify", "plan", "clarify-key", default);
        var current = JsonSerializer.SerializeToElement(new { user_input = "2026 Q1" });
        var request = new OrchestratorContextAcquireRequest(1, current, ["search_documents"], [source]);

        var result = await fixture.Runs.AcquireContextAsync("t", "u", created.Run!.Id, request, default);

        Assert.NotNull(result);
        if (enrichmentEnabled)
        {
            Assert.False(result!.Ready);
            Assert.Equal(["clarification-revision-required"], result.Missing);
            Assert.Equal("{}", result.Context.GetRawText());
        }
        else
        {
            Assert.True(result!.Ready);
            Assert.Empty(result.Missing);
            Assert.Equal("2026 Q1", result.Context.GetProperty("user_input").GetString());
        }
    }

    // A-CTX-14 + M7:child 拿到的 payload 只有自己的 view;投影後 envelope 超過 65 536 是永久性的
    // 400 材料(ArgumentException),不能逃逸成 Workflow 會無限重試的 500。
    [Fact]
    public async Task CreateChild_ProjectsOnlyItsOwnView_AndRejectsAnOversizedProjection()
    {
        var rag = new InMemoryRagRepository();
        var documentId = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(documentId.ToString("D"), "t", "evidence", default);
        await rag.CompleteDocumentAsync(documentId.ToString("D"), "t", ["source"], [new[] { 1f }], default);
        var chunk = Assert.Single(await rag.SearchAsync("t", [1f], 1, default));
        var contexts = new InMemoryContextRepository(rag, policyTenants: ["t"]);
        var childRuns = new ScriptedChildAgentRuns();
        var fixture = await FixtureAsync(maxConcurrency: 2, contextTools: ["backend.retrieval_search"],
            knowledgeSources: [documentId.ToString("D")], agentRuns: childRuns, contexts: contexts);
        var root = await fixture.Runs.CreateAsync("t", "u", "ADMIN", [], [], fixture.OrchestratorId, "projection", "plan", "projection-key", default);
        var rootId = root.Run!.Id;
        var evidence = new ContextEvidenceInput("document", documentId.ToString("D"), "s1", $"document://{documentId:D}#chunk/{chunk.ChunkId}", Backend.Api.Skills.SkillHash.Sha256("source"),
            Lineage: JsonSerializer.SerializeToElement(new { catalog_source_id = "backend_documents", adapter_id = "backend.retrieval_search" }));
        ContextViewInput[] views =
        [
            new("planner", JsonSerializer.SerializeToElement(new { plan = "root only" })),
            new("worker", JsonSerializer.SerializeToElement(new { task_fact = "worker only" })),
            new("verifier", JsonSerializer.SerializeToElement(new { claims = "verifier only" })),
        ];
        await contexts.CreateRevisionAsync("t", "u", Guid.NewGuid(), new ContextRevisionSubmitRequest(rootId,
            JsonSerializer.SerializeToElement(new { secret_envelope_field = "must not reach the child" }), [evidence], views,
            new ContextObjectiveMeasurements(1, 2)), default);

        var child = await ChildAsync(fixture, rootId, "worker-task");

        Assert.NotNull(child);
        var envelope = childRuns.DispatchedEnvelope;
        Assert.Equal("worker only", envelope.GetProperty("context").GetProperty("task_fact").GetString());
        var raw = envelope.GetRawText();
        Assert.DoesNotContain("secret_envelope_field", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("root only", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("verifier only", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("unmet_requirements", raw, StringComparison.Ordinal);

        var oversized = new ContextRevisionSubmitRequest(rootId,
            JsonSerializer.SerializeToElement(new { }), [evidence],
            [new("planner", JsonSerializer.SerializeToElement(new { plan = "root" })), new("worker", JsonSerializer.SerializeToElement(new { bulk = new string('w', 65_400) }))],
            new ContextObjectiveMeasurements(1, 2));
        var replacement = new InMemoryContextRepository(rag, policyTenants: ["t"]);
        var oversizedFixture = await FixtureAsync(contextTools: ["backend.retrieval_search"],
            knowledgeSources: [documentId.ToString("D")], contexts: replacement);
        var oversizedRoot = await oversizedFixture.Runs.CreateAsync("t", "u", "ADMIN", [], [], oversizedFixture.OrchestratorId, "oversize", "plan", "oversize-key", default);
        await replacement.CreateRevisionAsync("t", "u", Guid.NewGuid(), oversized with { RootRunId = oversizedRoot.Run!.Id }, default);

        await Assert.ThrowsAsync<ArgumentException>(() => ChildAsync(oversizedFixture, oversizedRoot.Run.Id, "worker-task"));
    }

    // child 狀態是從 D3 run 鏡射過來的:轉終局時必須恰好補一筆 child_terminal 事件(連呼兩次不得重複),
    // 並把 result 裡的 citations 抽出來。重複事件會讓 root 的聚合把同一個 child 算兩次。
    [Fact]
    public async Task ChildStatus_MirrorsTerminalOnce_AndExtractsCitations()
    {
        var childRuns = new ScriptedChildAgentRuns();
        var fixture = await FixtureAsync(agentRuns: childRuns);
        var created = await fixture.Runs.CreateAsync(
            "t", "u", "ADMIN", [], [], fixture.OrchestratorId, "mirror", "plan", "mirror-key", default);
        var runId = created.Run!.Id;
        var child = await ChildAsync(fixture, runId, "task-1");
        Assert.NotNull(child);

        var queued = await fixture.Runs.GetChildAsync("t", "u", runId, child!.Id, default);
        Assert.Equal("queued", queued!.Status);
        Assert.Equal("[]", queued.Citations.GetRawText());

        childRuns.Complete("""{"answer":"ok","citations":[{"document_id":"d1"}]}""");
        var terminal = await fixture.Runs.GetChildAsync("t", "u", runId, child.Id, default);
        Assert.Equal("completed", terminal!.Status);
        Assert.Equal("""[{"document_id":"d1"}]""", terminal.Citations.GetRawText());
        var afterFirstRead = (await fixture.Runs.EventsAsync("t", "u", runId, 0, 50, default))!
            .Events.Count(x => x.EventType == "child_terminal");
        Assert.Equal(1, afterFirstRead);

        // child_terminal 的 payload 也是共用產生器的產物(AgentRunRepository.cs:2509 的 12 欄),
        // 前端 trace 從中讀 task_id/attempt/run_kind/status;lite 只給 3 欄會讓它靜靜降級成 null。
        var payload = Assert.Single(
            (await fixture.Runs.EventsAsync("t", "u", runId, 0, 50, default))!.Events,
            x => x.EventType == "child_terminal").Payload;
        Assert.Equal(child.Id, payload.GetProperty("child_id").GetGuid());
        Assert.Equal(child.AgentRunId, payload.GetProperty("agent_run_id").GetGuid());
        Assert.Equal("task-1", payload.GetProperty("task_id").GetString());
        Assert.Equal(1, payload.GetProperty("attempt").GetInt32());
        Assert.Equal("worker", payload.GetProperty("run_kind").GetString());
        Assert.Equal(fixture.WorkerId, payload.GetProperty("agent_id").GetGuid());
        Assert.Equal(1, payload.GetProperty("agent_revision").GetInt32());
        Assert.Equal(child.AgentSnapshotHash, payload.GetProperty("agent_snapshot_hash").GetString());
        Assert.Equal("completed", payload.GetProperty("status").GetString());
        // 結果本體屬於 child authority:root 事件只留雜湊與 citation 數量。
        Assert.Equal(Backend.Api.Skills.SkillHash.Sha256("""{"answer":"ok","citations":[{"document_id":"d1"}]}"""),
            payload.GetProperty("result_sha256").GetString());
        Assert.Equal(1, payload.GetProperty("citations").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error_code").ValueKind);
        Assert.False(payload.TryGetProperty("output", out _));

        // 再讀一次不得重複追加。
        Assert.Equal("completed", (await fixture.Runs.GetChildAsync("t", "u", runId, child.Id, default))!.Status);
        Assert.Equal(1, (await fixture.Runs.EventsAsync("t", "u", runId, 0, 50, default))!
            .Events.Count(x => x.EventType == "child_terminal"));
        // 跨租戶/非擁有者/不存在的 child 都不得洩漏存在性。
        Assert.Null(await fixture.Runs.GetChildAsync("t", "other-user", runId, child.Id, default));
        Assert.Null(await fixture.Runs.GetChildAsync("t", "u", runId, Guid.NewGuid(), default));
    }

    // 公開事件是 server-redacted 的:child_created 的 payload 只能帶對外可見的譜系欄位,
    // 耐久命令 id 是內部執行憑據(平台的 Redact 只剝頂層鍵、且 events 不經 Redact,漏在這裡就直接外洩)。
    // 兩份倉儲必須產出同一份 payload 形狀,否則 in-memory 的空物件會讓斷言恆真。
    [Fact]
    public async Task ChildCreatedEvent_CarriesLineageWithoutTheDurableCommandId()
    {
        var fixture = await FixtureAsync();
        var created = await fixture.Runs.CreateAsync(
            "t", "u", "ADMIN", [], [], fixture.OrchestratorId, "events", "plan", "events-key", default);
        var child = await ChildAsync(fixture, created.Run!.Id, "task-1");
        Assert.NotNull(child);

        var events = await fixture.Runs.EventsAsync("t", "u", created.Run.Id, 0, 20, default);
        var payload = Assert.Single(events!.Events, x => x.EventType == "child_created").Payload;

        Assert.Equal(child!.Id, payload.GetProperty("child_id").GetGuid());
        Assert.Equal(child.AgentRunId, payload.GetProperty("agent_run_id").GetGuid());
        Assert.Equal("task-1", payload.GetProperty("task_id").GetString());
        Assert.Equal(1, payload.GetProperty("attempt").GetInt32());
        Assert.Equal("worker", payload.GetProperty("run_kind").GetString());
        Assert.False(payload.TryGetProperty("command_id", out _));
    }

    // 共用產生器是兩份倉儲唯一的事實來源,真正要鎖的是「欄位集合 + 命名 + 序列」。
    // Dapper 端的呼叫點需要整套租戶 Agent/Workflow/Skill revision fixture 才能走到,代價不對稱;
    // 直接對純函式斷言輸出字串,一次鎖住兩邊(in-memory 的形狀測試只間接鎖了一邊)。
    [Fact]
    public void RootEventPayloads_AreExactAndRedacted()
    {
        var childId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var agentRunId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var agentId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var result = JsonDocument.Parse("""{"citations":[{"document_id":"d1"},{"document_id":"d2"}]}""").RootElement;

        Assert.Equal(
            """{"child_id":"11111111-1111-4111-8111-111111111111","agent_run_id":"22222222-2222-4222-8222-222222222222","task_id":"task-1","attempt":2,"run_kind":"worker"}""",
            OrchestratorRunEvents.ChildCreated(childId, agentRunId, "task-1", 2, "worker"));

        Assert.Equal(
            $$"""{"child_id":"11111111-1111-4111-8111-111111111111","agent_run_id":"22222222-2222-4222-8222-222222222222","task_id":"task-1","attempt":2,"run_kind":"verifier","agent_id":"33333333-3333-4333-8333-333333333333","agent_revision":7,"agent_snapshot_hash":"{{new string('a', 64)}}","status":"failed","result_sha256":"{{Backend.Api.Skills.SkillHash.Sha256(result.GetRawText())}}","citations":{"count":2},"error_code":"boom"}""",
            OrchestratorRunEvents.ChildTerminal(childId, agentRunId, "task-1", 2, "verifier", agentId, 7,
                new string('a', 64), "failed", result, "  boom  "));

        // 沒有結果時雜湊是 null(不得變成空字串的雜湊),error_code 過長要截斷到 100。
        Assert.Equal(
            $$"""{"child_id":"11111111-1111-4111-8111-111111111111","agent_run_id":"22222222-2222-4222-8222-222222222222","task_id":"t","attempt":1,"run_kind":"worker","agent_id":"33333333-3333-4333-8333-333333333333","agent_revision":1,"agent_snapshot_hash":"h","status":"cancelled","result_sha256":null,"citations":{"count":0},"error_code":"{{new string('e', 100)}}"}""",
            OrchestratorRunEvents.ChildTerminal(childId, agentRunId, "t", 1, "worker", agentId, 1, "h",
                "cancelled", null, new string('e', 101)));

        Assert.Equal("""{"status":"completed"}""", OrchestratorRunEvents.RootTerminal("completed"));
    }

    [Fact]
    public async Task ContextRequests_AreTaskLocal_CasProtected_RoleScoped_AndCursorVisible()
    {
        var rag = new InMemoryRagRepository();
        var documentId = Guid.NewGuid();
        await rag.InsertProcessingDocumentAsync(documentId.ToString("D"), "t", "evidence", default);
        await rag.CompleteDocumentAsync(documentId.ToString("D"), "t", ["source"], [new[] { 1f }], default);
        var chunk = Assert.Single(await rag.SearchAsync("t", [1f], 1, default));
        var contexts = new InMemoryContextRepository(rag, policyTenants: ["t"]);
        var fixture = await FixtureAsync(maxConcurrency: 2,
            contextTools: ["backend.retrieval_search"], knowledgeSources: [documentId.ToString("D")], contexts: contexts);
        var root = await fixture.Runs.CreateAsync("t", "u", "ADMIN", [], [], fixture.OrchestratorId, "context", "plan", "context-root", default);
        var rootId = root.Run!.Id;
        var evidence = new ContextEvidenceInput("document", documentId.ToString("D"), "s1", $"document://{documentId:D}#chunk/{chunk.ChunkId}", Backend.Api.Skills.SkillHash.Sha256("source"),
            Observations: JsonSerializer.SerializeToElement(new { completeness = 1m }),
            Lineage: JsonSerializer.SerializeToElement(new { catalog_source_id = "backend_documents", adapter_id = "backend.retrieval_search" }));
        var measures = new ContextObjectiveMeasurements(1, 2);
        var baseRevision = await contexts.CreateRevisionAsync("t", "u", Guid.NewGuid(), new ContextRevisionSubmitRequest(rootId,
            JsonSerializer.SerializeToElement(new { base_value = 1 }), [evidence],
            [new("planner", JsonSerializer.SerializeToElement(new { role = "planner" })), new("worker", JsonSerializer.SerializeToElement(new { role = "worker" })), new("verifier", JsonSerializer.SerializeToElement(new { role = "verifier" }))], measures), default);
        Assert.Equal(ContextStatuses.Ready, baseRevision.Revision.Status);

        var worker = await ChildAsync(fixture, rootId, "worker-task");
        Assert.NotNull(worker);
        var request = await fixture.Runs.GetOrCreateContextRequestAsync("t", "u", rootId, worker!.Id, default);
        Assert.NotNull(request);
        Assert.Equal("worker", request!.Role);
        Assert.Equal(baseRevision.Revision.ContextRef!.ContextId, request.BaseContextRef!.ContextId);
        Assert.Null(await fixture.Runs.GetContextRequestAsync("t", "other", rootId, worker.Id, request.Id, default));

        var delta = new OrchestratorContextDeltaRequest(JsonSerializer.SerializeToElement(new { task_local = true }), [evidence],
            [new("worker", JsonSerializer.SerializeToElement(new { role = "worker", facts = new { value = 2 } }))], measures);
        var applied = await fixture.Runs.AppendContextDeltaAsync("t", "u", rootId, worker.Id, request.Id, request.Version, delta, default);
        Assert.Equal(OrchestratorContextDeltaStatus.Success, applied.Status);
        Assert.Equal(1, applied.Revision!.Revision);
        Assert.Equal(2, applied.Version);
        Assert.Equal(request.ContextId, applied.Revision.ContextId);
        var stale = await fixture.Runs.AppendContextDeltaAsync("t", "u", rootId, worker.Id, request.Id, 1, delta, default);
        Assert.Equal(OrchestratorContextDeltaStatus.Conflict, stale.Status);
        Assert.Equal(1, (await contexts.GetRevisionAsync("t", request.ContextId, 1, default))!.Revision);
        Assert.Null(await contexts.GetRevisionAsync("t", request.ContextId, 2, default));

        var verifier = await fixture.Runs.CreateChildAsync("t", "u", rootId,
            new OrchestratorChildCreateRequest("verify-task", 1, "verifier", fixture.VerifierId, 1, TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default);
        var verifierRequest = await fixture.Runs.GetOrCreateContextRequestAsync("t", "u", rootId, verifier!.Id, default);
        Assert.Equal("verifier", verifierRequest!.Role);
        var wrongRole = new OrchestratorContextDeltaRequest(JsonSerializer.SerializeToElement(new { }), [evidence],
            [new("worker", JsonSerializer.SerializeToElement(new { conflict = "untrusted" }))], measures);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Runs.AppendContextDeltaAsync("t", "u", rootId, verifier.Id, verifierRequest.Id, verifierRequest.Version, wrongRole, default));
        var verifierDelta = wrongRole with { Views = [new("verifier", JsonSerializer.SerializeToElement(new { conflicts = new[] { "fact conflict" } }))] };
        Assert.Equal(OrchestratorContextDeltaStatus.Success, (await fixture.Runs.AppendContextDeltaAsync("t", "u", rootId, verifier.Id, verifierRequest.Id, verifierRequest.Version, verifierDelta, default)).Status);

        var events = await fixture.Runs.EventsAsync("t", "u", rootId, 0, 50, default);
        var contextEvents = events!.Events.Where(x => x.EventType.StartsWith("context.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["context.requested", "context.delta_applied", "context.requested", "context.delta_applied"], contextEvents.Select(x => x.EventType));
        Assert.Equal(contextEvents.Select(x => x.Sequence).Order().ToArray(), contextEvents.Select(x => x.Sequence).ToArray());
        Assert.All(contextEvents, x =>
        {
            Assert.False(x.Payload.TryGetProperty("definition", out _));
            Assert.False(x.Payload.TryGetProperty("evidence", out _));
            Assert.False(x.Payload.TryGetProperty("context", out _));
        });
    }

    private sealed record RunFixture(InMemoryOrchestratorRunRepository Runs, Guid OrchestratorId, Guid WorkerId, Guid VerifierId);

    private static async Task<RunFixture> FixtureAsync(
        int maxTasks = 2, int maxChildRuns = 3, int maxConcurrency = 1,
        string[]? contextTools = null, string[]? knowledgeSources = null,
        IAgentRunRepository? agentRuns = null, IContextRepository? contexts = null)
    {
        var workflows = new Data.InMemory.InMemoryWorkflowRepository();
        var root = await workflows.CreateAsync("t", "root", "orchestrator", "{\"schemaVersion\":1}", "{}", "u", default);
        Assert.True(await workflows.MarkValidatedAsync("t", root.Workflow!.Id, 1, "{\"schemaVersion\":1}", "{}", default));
        await workflows.PublishAsync("t", root.Workflow.Id, 1, "{\"schemaVersion\":1}", "{}", WorkflowCompilerContracts.Current, "u", default);
        var workerWorkflow = await CreateWorkflow(workflows, "worker", "agent-runtime");
        var verifierWorkflow = await CreateWorkflow(workflows, "verifier", "agent-runtime");
        var workerId = Guid.NewGuid();
        var verifierId = Guid.NewGuid();
        var orchestrators = new StubOrchestrators(
            Guid.NewGuid(),
            Definition(root.Workflow.Id, workerId, verifierId, maxTasks, maxChildRuns, maxConcurrency, contextTools, knowledgeSources));
        var agents = new StubAgents(workerId, verifierId, workerWorkflow, verifierWorkflow);
        return new RunFixture(
            new InMemoryOrchestratorRunRepository(orchestrators, workflows, agents, agentRuns, contexts,
                contexts is null ? null : new ContextEnrichmentState(true)),
            orchestrators.Id,
            workerId, verifierId);
    }

    private static Task<OrchestratorChildResponse?> ChildAsync(RunFixture fixture, Guid rootRunId, string taskId)
        => fixture.Runs.CreateChildAsync("t", "u", rootRunId,
            new OrchestratorChildCreateRequest(taskId, 1, "worker", fixture.WorkerId, 1,
                TaskEnvelope: TaskEnvelope(), TokenCap: AgentExecutionContract.DefaultTokenBudget), default);

    /// <summary>
    /// 手寫的 D3 child run 假實作:只提供 GetChildAsync 鏡射所需的建立與讀取,
    /// 其餘方法刻意不支援(走到就代表 orchestrator 用了不該用的路徑)。
    /// </summary>
    private sealed class ScriptedChildAgentRuns : IAgentRunRepository, IOrchestratorChildRunRepository
    {
        private AgentRunResponse? _run;

        /// <summary>The exact canonical envelope the orchestrator persists on the child (same value
        /// goes to `orchestrator_run_child.task_envelope` and to the D3 child start command).</summary>
        public JsonElement DispatchedEnvelope { get; private set; }

        public Task<AgentRunWriteResult> CreateOrchestratorChildAsync(
            string tenantId, string userId, string role, IReadOnlyCollection<string> groups,
            IReadOnlyCollection<string> capabilityClaims, PublishedAgentSnapshotSource agent,
            WorkflowSnapshotSource workflow, OrchestratorChildSnapshotProvenance provenance,
            string runKind, int tokenCap, JsonElement taskEnvelope, string idempotencyKey, CancellationToken ct)
        {
            DispatchedEnvelope = taskEnvelope.Clone();
            _run = new AgentRunResponse(
                Guid.NewGuid(), provenance.RootRunId, provenance.RootRunId, provenance.TaskId, runKind,
                agent.AgentId, agent.Revision, workflow.WorkflowId, workflow.Revision, agent.DefinitionSha256,
                "queued", 1, 0, 0, null, 0, 0, false, null, null, null, null, 0, null,
                DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10), DateTime.UtcNow, null, [],
                JsonDocument.Parse("{}").RootElement.Clone());
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.Success, _run, Dispatch: new AgentRunCommandDispatch(Guid.NewGuid(), "claim", DateTime.UtcNow.AddSeconds(30), 1)));
        }

        public void Complete(string resultJson) => _run = _run! with
        {
            Status = "completed",
            Result = JsonDocument.Parse(resultJson).RootElement.Clone(),
        };

        public Task<AgentRunResponse?> GetAsync(string t, string u, Guid id, CancellationToken ct)
            => Task.FromResult(_run is not null && _run.Id == id ? _run : null);

        public Task<AgentRunWriteResult> CreateDirectAsync(string a, string b, string c, IReadOnlyCollection<string> d, IReadOnlyCollection<string> e, Guid f, string g, string h, CancellationToken i) => throw new NotSupportedException();
        public Task<string?> GetExecutionArtifactAsync(string a, string b, Guid c, CancellationToken d) => throw new NotSupportedException();
        public Task<AgentRunEventsResponse?> GetEventsAsync(string a, string b, Guid c, long d, int e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> ResumeAsync(string a, string b, Guid c, string d, long e, string f, CancellationToken g) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> CancelAsync(string a, string b, Guid c, string? d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> TransitionAsync(string a, string b, Guid c, AgentRunTransitionRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<AgentRunWriteResult> AppendEventsAsync(string a, string b, Guid c, AgentRunEventsAppendRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<AgentRunLeaseResult> ClaimLeaseAsync(string a, string b, Guid c, AgentRunLeaseRequest d, CancellationToken e) => throw new NotSupportedException();
        public Task<AgentRunCommandClaimResult> ClaimCommandAsync(string a, string b, Guid c, Guid d, AgentRunCommandClaimRequest e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunDispatchCompleteStatus> CompleteDispatchAsync(string a, string b, Guid c, Guid d, string e, CancellationToken f) => throw new NotSupportedException();
        public Task<AgentRunRecoveryClaimResponse> ClaimRecoveryAsync(AgentRunRecoveryClaimRequest a, CancellationToken b) => throw new NotSupportedException();
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
    private static string Definition(
        Guid workflow, Guid worker, Guid verifier,
        int maxTasks = 2, int maxChildRuns = 3, int maxConcurrency = 1,
        string[]? contextTools = null, string[]? knowledgeSources = null) => new JsonObject
    {
        ["instructions"] = "root",
        ["policy"] = new JsonObject { { "dispatchMode", "bounded-parallel" }, { "joinPolicy", "repair" }, { "repairPolicy", "redispatch" }, { "aggregationPolicy", "verified-only" }, { "denialPolicy", "fail-closed" } },
        ["workflow"] = new JsonObject { { "id", workflow.ToString("D") }, { "revision", 1 } },
        ["verifier"] = new JsonObject { { "agentId", verifier.ToString("D") }, { "revision", 1 }, { "variant", "read-only" }, { "independent", true }, { "outputContract", new JsonObject { { "type", "verification-report" } } } },
        ["workerPool"] = new JsonArray(new JsonObject { { "agentId", worker.ToString("D") }, { "revision", 1 } }),
        ["workerPolicy"] = new JsonObject { { "requiredAudience", new JsonArray() }, { "requiredCapabilities", new JsonArray() }, { "selection", "pinned-only" } },
        ["context"] = new JsonObject
        {
            { "readOnly", true },
            { "allowedTools", new JsonArray((contextTools ?? []).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) },
            { "knowledgeSources", new JsonArray((knowledgeSources ?? []).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) },
        },
        ["audience"] = new JsonArray(),
        ["capabilities"] = new JsonArray(),
        ["budgets"] = new JsonObject { { "maxContextRounds", 1 }, { "maxTasks", maxTasks }, { "maxChildRuns", maxChildRuns }, { "maxConcurrency", maxConcurrency }, { "maxRepairRounds", 1 }, { "tokenBudget", 10 }, { "timeoutSeconds", 10 } },
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

    // H1(PostgreSQL 權威側):同一組輸入,旗標關閉必須回既有的 ready:true 短路,開啟才走 revision。
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireContext_UserInputClarification_DependsOnTheEnrichmentFlag(bool enrichmentEnabled)
    {
        fixture.SkipIfUnavailable();
        var root = Guid.NewGuid(); var document = Guid.NewGuid();
        await InsertContextRootAsync(root, JsonSerializer.Serialize(new
        {
            authority = new { knowledge_sources = new[] { document.ToString("D") }, context_tools = new[] { "backend.retrieval_search" } },
        }));
        var repo = new OrchestratorRunRepository(fixture.DataSource!, new ContextRepository(fixture.DataSource!), new ContextEnrichmentState(enrichmentEnabled));
        var current = JsonSerializer.SerializeToElement(new { user_input = "2026 Q1" });

        var result = await repo.AcquireContextAsync(Tenant, "user", root,
            new OrchestratorContextAcquireRequest(1, current, ["backend.retrieval_search"], [document.ToString("D")]), default);

        Assert.NotNull(result);
        if (enrichmentEnabled)
        {
            Assert.False(result!.Ready);
            Assert.Equal(["clarification-revision-required"], result.Missing);
            Assert.Equal("{}", result.Context.GetRawText());
        }
        else
        {
            Assert.True(result!.Ready);
            Assert.Empty(result.Missing);
            Assert.Equal("2026 Q1", result.Context.GetProperty("user_input").GetString());
        }
    }

    [SkippableFact]
    public async Task ContextRequest_DapperStorage_IsOwnerScopedIdempotentAndUsesRootCursor()
    {
        fixture.SkipIfUnavailable();
        var root = Guid.NewGuid(); var child = Guid.NewGuid();
        await InsertAsync(root, "context-request", Guid.NewGuid(), "queued");
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("INSERT INTO orchestrator_run_child(id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,workflow_id,workflow_revision,agent_snapshot_sha256,task_envelope,dispatch_artifact,status) VALUES(@child,@root,'task',1,'worker',@agent,1,@workflow,1,@hash,@envelope::jsonb,'{}'::jsonb,'queued')", new { child, root, agent = Guid.NewGuid(), workflow = Guid.NewGuid(), hash = new string('b', 64), envelope = "{\"objective\":\"task\"}" });
        }
        var repo = new OrchestratorRunRepository(fixture.DataSource!, new ContextRepository(fixture.DataSource!), new ContextEnrichmentState(true));
        var first = await repo.GetOrCreateContextRequestAsync(Tenant, "user", root, child, default);
        var replay = await repo.GetOrCreateContextRequestAsync(Tenant, "user", root, child, default);
        Assert.NotNull(first);
        Assert.Equal(first!.Id, replay!.Id);
        Assert.Equal("worker", first.Role);
        Assert.Null(await repo.GetContextRequestAsync(Tenant, "other-user", root, child, first.Id, default));
        await using var verify = await fixture.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM context_request WHERE orchestrator_child_id=@child", new { child }));
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@root AND event_type='context.requested'", new { root }));
    }

    [SkippableFact]
    public async Task ContextDelta_DapperLifecycle_IsAtomicRoleScopedAndDoesNotReplaceRootReadyContext()
    {
        fixture.SkipIfUnavailable();
        var root = Guid.NewGuid(); var child = Guid.NewGuid(); var document = Guid.NewGuid(); var chunk = Guid.NewGuid();
        const string content = "authoritative source";
        var rootSnapshot = JsonSerializer.Serialize(new
        {
            authority = new { knowledge_sources = new[] { document.ToString("D") }, context_tools = new[] { "backend.retrieval_search" } },
            limits = new { max_context_rounds = 2, max_tasks = 2, max_child_runs = 2, max_concurrency = 2, max_repair_rounds = 1, timeout_seconds = 60 },
            token_budget = 10,
        });
        await InsertContextRootAsync(root, rootSnapshot);
        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            const string policy = "{\"readiness\":{\"ready_threshold\":0.85,\"assumptions_min\":0.70,\"optional_failure_penalty\":0.10},\"bootstrap_requirements\":[{\"name\":\"document\",\"evidence_type\":\"document\",\"mandatory\":true}],\"source_requirements\":[{\"source_id\":\"backend_documents\",\"required\":true}],\"source_precedence\":[\"backend_documents\"]}";
            var embedding = "[" + string.Join(',', Enumerable.Repeat("0", 1536)) + "]";
            await connection.ExecuteAsync("INSERT INTO context_policy(id,tenant_id,name,is_active,values,created_by) VALUES(@id,@tenant,'e3',true,@policy::jsonb,'test'); INSERT INTO rag_documents(id,tenant_id,title,chunk_count,status) VALUES(@document,@tenant,'e3',1,'ready'); INSERT INTO rag_chunks(id,document_id,tenant_id,content,embedding) VALUES(@chunk,@document,@tenant,@content,@embedding::vector)", new { id = Guid.NewGuid(), tenant = Tenant, policy, document, chunk, content, embedding });
        }
        var contexts = new ContextRepository(fixture.DataSource!);
        var evidence = new ContextEvidenceInput("document", document.ToString("D"), "snapshot", $"document://{document:D}#chunk/{chunk:D}", Skills.SkillHash.Sha256(content),
            Observations: JsonSerializer.SerializeToElement(new { completeness = 1m }), Lineage: JsonSerializer.SerializeToElement(new { catalog_source_id = "backend_documents", adapter_id = "backend.retrieval_search" }));
        var measurements = new ContextObjectiveMeasurements(1, 2);
        var baseRevision = await contexts.CreateRevisionAsync(Tenant, "user", Guid.NewGuid(), new ContextRevisionSubmitRequest(root,
            JsonSerializer.SerializeToElement(new { root = true }), [evidence],
            [new("planner", JsonSerializer.SerializeToElement(new { role = "planner" })), new("worker", JsonSerializer.SerializeToElement(new { role = "worker" })), new("verifier", JsonSerializer.SerializeToElement(new { role = "verifier" }))], measurements), default);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            var envelope = JsonSerializer.Serialize(new { context_ref = baseRevision.Revision.ContextRef, objective = "task" });
            await connection.ExecuteAsync("INSERT INTO orchestrator_run_child(id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,workflow_id,workflow_revision,agent_snapshot_sha256,task_envelope,dispatch_artifact,status) VALUES(@child,@root,'worker-task',1,'worker',@agent,1,@workflow,1,@hash,@envelope::jsonb,'{}'::jsonb,'queued')", new { child, root, agent = Guid.NewGuid(), workflow = Guid.NewGuid(), hash = new string('b', 64), envelope });
        }
        var repo = new OrchestratorRunRepository(fixture.DataSource!, contexts, new ContextEnrichmentState(true));
        var request = await repo.GetOrCreateContextRequestAsync(Tenant, "user", root, child, default);
        Assert.Equal(baseRevision.Revision.ContextRef, request!.BaseContextRef);
        var delta = new OrchestratorContextDeltaRequest(JsonSerializer.SerializeToElement(new { task = true }), [evidence], [new("worker", JsonSerializer.SerializeToElement(new { role = "worker", facts = new { value = 2 } }))], measurements);
        var applied = await repo.AppendContextDeltaAsync(Tenant, "user", root, child, request.Id, request.Version, delta, default);
        Assert.Equal(OrchestratorContextDeltaStatus.Success, applied.Status);
        Assert.Equal(ContextStatuses.Ready, applied.Revision!.Status);
        Assert.Equal(2, applied.Version);
        var stale = await repo.AppendContextDeltaAsync(Tenant, "user", root, child, request.Id, 1, delta, default);
        Assert.Equal(OrchestratorContextDeltaStatus.Conflict, stale.Status);
        var wrongRole = delta with { Views = [new("verifier", JsonSerializer.SerializeToElement(new { conflict = "only verifier sees this" }))] };
        await Assert.ThrowsAsync<ArgumentException>(() => repo.AppendContextDeltaAsync(Tenant, "user", root, child, request.Id, applied.Version, wrongRole, default));
        var acquire = await repo.AcquireContextAsync(Tenant, "user", root, new OrchestratorContextAcquireRequest(1, JsonSerializer.SerializeToElement(new { }), ["backend.retrieval_search"], [document.ToString("D")]), default);
        Assert.True(acquire!.Ready);
        Assert.Equal(baseRevision.Revision.ContextRef!.ContextId.ToString("D"), acquire.Context.GetProperty("context_ref").GetProperty("context_id").GetString());
        await using var verify = await fixture.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM context_revision WHERE context_id=@context", new { context = request.ContextId }));
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM context_delta WHERE context_request_id=@request", new { request = request.Id }));
        Assert.Equal(2L, await verify.ExecuteScalarAsync<long>("SELECT version FROM context_request WHERE id=@request", new { request = request.Id }));
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>("SELECT count(*) FROM orchestrator_run_event WHERE run_id=@root AND event_type='context.delta_applied'", new { root }));
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

    private async Task InsertContextRootAsync(Guid id, string snapshot)
    {
        var canonical = AgentCanonicalizer.CanonicalizeDefinition(snapshot);
        var hash = Skills.SkillHash.Sha256(canonical);
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("INSERT INTO orchestrator_run(id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,request_sha256,idempotency_key_sha256,status,deadline_at) VALUES(@id,@tenant,'user','USER',@orchestrator,1,'context-lifecycle',@workflow,1,@snapshot::jsonb,@bytes,@hash,@hash,@key,'queued',clock_timestamp()+interval '1 hour')", new { id, tenant = Tenant, orchestrator = Guid.NewGuid(), workflow = Guid.NewGuid(), snapshot = canonical, bytes = Encoding.UTF8.GetBytes(canonical), hash, key = Skills.SkillHash.Sha256(Guid.NewGuid().ToString("N")) });
    }

    private static OrchestratorRunReplayRequest ReplayRequest(string conversation, Guid orchestrator, string message) => new(conversation, orchestrator, message);

    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM context_delta WHERE context_request_id IN (SELECT id FROM context_request WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant)); DELETE FROM context_request WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM context_evidence WHERE tenant_id=@tenant; DELETE FROM context_view WHERE tenant_id=@tenant; DELETE FROM context_revision WHERE tenant_id=@tenant; DELETE FROM context_policy WHERE tenant_id=@tenant; DELETE FROM rag_chunks WHERE tenant_id=@tenant; DELETE FROM rag_documents WHERE tenant_id=@tenant; DELETE FROM agent_run_command WHERE run_id IN (SELECT id FROM agent_run WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant)); DELETE FROM agent_run_event WHERE run_id IN (SELECT id FROM agent_run WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant)); DELETE FROM agent_run WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run_event WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run_command WHERE run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run_child WHERE orchestrator_root_run_id IN (SELECT id FROM orchestrator_run WHERE tenant_id=@tenant); DELETE FROM orchestrator_run WHERE tenant_id=@tenant;", new { tenant = Tenant });
    }
}
