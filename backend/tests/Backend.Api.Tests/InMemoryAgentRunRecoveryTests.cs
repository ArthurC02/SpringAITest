using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;
using System.Text.Json;

namespace Backend.Api.Tests;

public sealed class InMemoryAgentRunRecoveryTests
{
    [Fact]
    public async Task HistoricalRev2PublishedPin_RemainsExecutableWhileNewAuthoringRejectsIt()
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync("historical-rev2");
        var published = agents.GetPublishedSnapshotUnsafe("demo-a", agent.Id)!;
        using var definitionDocument = JsonDocument.Parse(published.Definition);
        var definition = System.Text.Json.Nodes.JsonNode.Parse(definitionDocument.RootElement.GetRawText())!.AsObject();
        definition["runtime_workflow"]!["revision"] = AgentDefaults.PreviousRuntimeWorkflowRevision;
        var historicalDefinition = AgentCanonicalizer.CanonicalizeDefinition(definition.ToJsonString());
        SetPublishedWorkflowRevision(agents, agent.Id, historicalDefinition, AgentDefaults.PreviousRuntimeWorkflowRevision);

        var authoredErrors = await agents.ValidateReferencesAsync("demo-a", historicalDefinition, default);
        Assert.Contains(authoredErrors, error => error.Field == "runtime_workflow");

        var runs = new InMemoryAgentRunRepository(agents, skills);
        var started = await runs.CreateDirectAsync("demo-a", "admin-a", "ADMIN", agent.Id, "historical", "historical-start", default);
        Assert.Equal(AgentRunWriteStatus.Success, started.Status);
        Assert.Equal(AgentDefaults.PreviousRuntimeWorkflowRevision, started.Run!.WorkflowRevision);
    }

    [Fact]
    public async Task OrchestratorChild_UsesPinnedChildSnapshotAndNeverDirectRunShape()
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync("orchestrator-child");
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var source = agents.GetPublishedSnapshotUnsafe("demo-a", agent.Id)!;
        var workflowDefinition = AgentRunSnapshotBuilder.CanonicalizeJson(
            AgentDefaults.RuntimeWorkflowDefinition);
        var workflow = new WorkflowSnapshotSource(
            source.WorkflowId, source.WorkflowRevision, 1, workflowDefinition,
            SkillHash.Sha256(workflowDefinition), "1");
        var rootRunId = Guid.Parse("41111111-1111-4111-8111-111111111111");
        var taskEnvelope = JsonDocument.Parse("""
            {"objective":"research","required_capabilities":["research"],"context":{"query":"q"},"context_provenance":[{"context_key":"query","source_type":"caller","source_id":"user","observed_at":"2026-01-01T00:00:00Z","content_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"write_intent":false,"delegation_depth":0,"repair_of":null}
            """).RootElement.Clone();

        var started = await ((IOrchestratorChildRunRepository)runs)
            .CreateOrchestratorChildAsync(
                "demo-a", "admin-a", "ADMIN", Array.Empty<string>(),
                Array.Empty<string>(), source, workflow,
                new OrchestratorChildSnapshotProvenance(rootRunId, "research-a", 2),
                "worker", 10_000, taskEnvelope, "child-key", default);

        Assert.Equal(AgentRunWriteStatus.Success, started.Status);
        Assert.Equal(rootRunId, started.Run!.RootRunId);
        Assert.Equal("research-a", started.Run.TaskId);
        Assert.Equal("worker", started.Run.RunKind);
        var artifact = await runs.GetExecutionArtifactAsync(
            "demo-a", "admin-a", started.Run.Id, default);
        using var envelope = JsonDocument.Parse(artifact!);
        var bytes = Convert.FromBase64String(envelope.RootElement
            .GetProperty("snapshot_canonical_base64").GetString()!);
        Assert.Equal(started.Run.SnapshotHash, SkillHash.Sha256(bytes));
        using var snapshot = JsonDocument.Parse(bytes);
        Assert.Equal("orchestrator-worker", snapshot.RootElement
            .GetProperty("execution_kind").GetString());
        Assert.Equal(rootRunId, snapshot.RootElement
            .GetProperty("orchestrator_root_run_id").GetGuid());
        Assert.Equal("research-a", snapshot.RootElement
            .GetProperty("orchestrator_task_id").GetString());
        Assert.Equal(2, snapshot.RootElement
            .GetProperty("orchestrator_attempt").GetInt32());
        Assert.Equal(10_000, snapshot.RootElement
            .GetProperty("orchestrator_token_cap").GetInt32());
        Assert.Equal(10_000, snapshot.RootElement.GetProperty("agent")
            .GetProperty("runtime_limits").GetProperty("token_budget").GetInt32());

        var claim = await runs.ClaimCommandAsync(
            "demo-a", "admin-a", started.Run.Id, started.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("workflow", 30), default);
        Assert.Equal(AgentRunWriteStatus.Success, claim.Status);
        var input = claim.Item!.Input;
        Assert.Equal(new[] { "message", "task_envelope" }, input
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal("research", input.GetProperty("message").GetString());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(taskEnvelope.GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(input.GetProperty("task_envelope").GetRawText())));
    }

    // 等價類:runKind 只接受 "worker"/"verifier"。上面的測試只走過 worker,這裡補 verifier
    // (合法類 → execution_kind = orchestrator-verifier)與任意其他字串(非法類 → InvalidState)。
    [Fact]
    public async Task OrchestratorChild_PinsVerifierExecutionKind_AndRejectsUnknownRunKind()
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync(
            "orchestrator-verifier-child", new[] { "worker", "verifier" });
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var source = agents.GetPublishedSnapshotUnsafe("demo-a", agent.Id)!;
        var workflowDefinition = AgentRunSnapshotBuilder.CanonicalizeJson(
            AgentDefaults.RuntimeWorkflowDefinition);
        var workflow = new WorkflowSnapshotSource(
            source.WorkflowId, source.WorkflowRevision, 1, workflowDefinition,
            SkillHash.Sha256(workflowDefinition), "1");
        var rootRunId = Guid.Parse("42222222-2222-4222-8222-222222222222");
        var taskEnvelope = JsonDocument.Parse("""
            {"objective":"verify","required_capabilities":["research"],"context":{"query":"q"},"context_provenance":[{"context_key":"query","source_type":"caller","source_id":"user","observed_at":"2026-01-01T00:00:00Z","content_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"write_intent":false,"delegation_depth":0,"repair_of":null}
            """).RootElement.Clone();

        var verifier = await ((IOrchestratorChildRunRepository)runs)
            .CreateOrchestratorChildAsync(
                "demo-a", "admin-a", "ADMIN", Array.Empty<string>(),
                Array.Empty<string>(), source, workflow,
                new OrchestratorChildSnapshotProvenance(rootRunId, "verify-a", 1),
                "verifier", 10_000, taskEnvelope, "verifier-key", default);
        var unknownKind = await ((IOrchestratorChildRunRepository)runs)
            .CreateOrchestratorChildAsync(
                "demo-a", "admin-a", "ADMIN", Array.Empty<string>(),
                Array.Empty<string>(), source, workflow,
                new OrchestratorChildSnapshotProvenance(rootRunId, "verify-a", 1),
                "observer", 10_000, taskEnvelope, "observer-key", default);

        Assert.Equal(AgentRunWriteStatus.Success, verifier.Status);
        Assert.Equal("verifier", verifier.Run!.RunKind);
        var artifact = await runs.GetExecutionArtifactAsync(
            "demo-a", "admin-a", verifier.Run.Id, default);
        using var envelope = JsonDocument.Parse(artifact!);
        var bytes = Convert.FromBase64String(envelope.RootElement
            .GetProperty("snapshot_canonical_base64").GetString()!);
        using var snapshot = JsonDocument.Parse(bytes);
        Assert.Equal("orchestrator-verifier", snapshot.RootElement
            .GetProperty("execution_kind").GetString());
        Assert.Equal(AgentRunWriteStatus.InvalidState, unknownKind.Status);
        Assert.Null(unknownKind.Run);
    }

    [Fact]
    public async Task QueuedRun_AckDefersExecutionRecoveryUntilGraceExpires()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("acked-dispatch");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "start",
            "start-key",
            default);
        Assert.NotNull(started.Dispatch);
        clock.Advance(TimeSpan.FromSeconds(31));
        var firstRecovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("scrubber", 20, 30),
            default);
        var command = Assert.Single(
            firstRecovery.Items,
            item => item.RunId == started.Run!.Id);
        Assert.Equal("start", command.CommandType);
        Assert.Equal("start", command.Input.GetProperty("message").GetString());
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run!.Id,
                command.CommandId,
                command.ClaimToken,
                default));

        var immediatelyAfterAck = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("scrubber", 20, 30),
            default);
        Assert.DoesNotContain(
            immediatelyAfterAck.Items,
            item => item.RunId == started.Run.Id);

        // 邊界值:no-lease grace 判定是 `completedAt <= now.AddSeconds(-30)`,
        // 剛好 30 秒(等號成立)就必須可回收。用 31 秒測不出把 `<=` 寫成 `<`。
        clock.Advance(TimeSpan.FromSeconds(30));
        var executionRecovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("replacement-worker", 20, 30),
            default);
        var recoveredAgain = Assert.Single(
            executionRecovery.Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal(3, recoveredAgain.DispatchAttempt);
        Assert.Equal(
            "start",
            recoveredAgain.Input.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CancelledRun_UnackedCancelIsRecoveredOnce_ThenAckStopsRecovery()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("cancel-recovery");

        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "start",
            "start-key",
            default);
        var cancelled = await runs.CancelAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            "scrub me",
            "cancel-key",
            default);
        Assert.Equal(AgentRunStatuses.Queued, cancelled.Run!.Status);
        Assert.True(cancelled.Run.CancelRequested);
        Assert.NotNull(cancelled.Dispatch);
        var terminalNoOp = await runs.CancelAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            "already stopped",
            "cancel-noop",
            default);
        Assert.Null(terminalNoOp.Dispatch);

        var directClaim = await runs.ClaimCommandAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            cancelled.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("direct-cleanup", 30),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, directClaim.Status);
        Assert.Equal(JsonValueKind.Undefined, directClaim.Item!.Input.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, directClaim.Item.Snapshot.ValueKind);
        Assert.Equal("ADMIN", directClaim.Item.Role);
        Assert.Equal(AgentRunStatuses.Cancelled, directClaim.Item.TargetTerminal);

        clock.Advance(TimeSpan.FromSeconds(31));
        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("scrubber", 20, 30),
            default);
        var command = Assert.Single(
            recovery.Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal("cancel", command.CommandType);
        Assert.Equal(JsonValueKind.Undefined, command.Input.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, command.Snapshot.ValueKind);
        Assert.Equal("ADMIN", command.Role);
        Assert.Equal(AgentRunStatuses.Cancelled, command.TargetTerminal);
        var terminal = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                command.StateVersion,
                AgentRunStatuses.Cancelled,
                command.LeaseToken,
                LeaseGeneration: command.LeaseGeneration,
                ExpectedEventAckCursor: command.EventAckCursor,
                CheckpointRef: V2CheckpointRef(command.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);

        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                command.CommandId,
                command.ClaimToken,
                default));
        clock.Advance(TimeSpan.FromSeconds(31));
        var afterAck = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("scrubber-2", 20, 30),
            default);
        Assert.DoesNotContain(
            afterAck.Items,
            item => item.RunId == started.Run.Id);
    }

    [Fact]
    public async Task RunningRun_UnackedCancelImmediatelyTakesOverAndFencesActiveLease()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("running-cancel-recovery");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "start",
            "start-key",
            default);
        var lease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "worker-a", 300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, lease.Status);
        Assert.NotNull(lease.Lease);
        var running = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);

        var cancelled = await runs.CancelAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            "stop active worker",
            "cancel-key",
            default);
        Assert.Equal(AgentRunStatuses.Running, cancelled.Run!.Status);
        Assert.NotNull(cancelled.Dispatch);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(lease.Lease.LeaseExpiresAt > clock.GetUtcNow());
        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("scrubber", 20, 30),
            default);
        var command = Assert.Single(
            recovery.Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal("cancel", command.CommandType);
        Assert.Equal(AgentRunStatuses.Running, command.RunStatus);
        Assert.Equal(
            lease.Lease.LeaseGeneration + 1,
            command.LeaseGeneration);
        Assert.Equal(JsonValueKind.Undefined, command.Input.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, command.Snapshot.ValueKind);
        Assert.Equal("ADMIN", command.Role);
        Assert.Equal(AgentRunStatuses.Cancelled, command.TargetTerminal);

        var staleEvent = await runs.AppendEventsAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunEventsAppendRequest(
                command.StateVersion,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                command.EventAckCursor,
                new[]
                {
                    new AgentRunEventAppend(
                        Guid.NewGuid(),
                        "run_cancelled",
                        "stale-worker",
                        command.SnapshotHash,
                        JsonSerializer.SerializeToElement(new { status = "stale" })),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, staleEvent.Status);
        var staleTransition = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                command.StateVersion,
                AgentRunStatuses.Cancelled,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: command.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Conflict, staleTransition.Status);

        var audit = await runs.AppendEventsAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunEventsAppendRequest(
                command.StateVersion,
                command.LeaseToken,
                command.LeaseGeneration,
                command.EventAckCursor,
                new[]
                {
                    new AgentRunEventAppend(
                        Guid.NewGuid(),
                        "run_cancelled",
                        "scrubber",
                        command.SnapshotHash,
                        JsonSerializer.SerializeToElement(new { status = "cancelled" })),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, audit.Status);
        var terminal = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                command.StateVersion,
                AgentRunStatuses.Cancelled,
                command.LeaseToken,
                LeaseGeneration: command.LeaseGeneration,
                ExpectedEventAckCursor: audit.Run!.EventAckCursor,
                CheckpointRef: V2CheckpointRef(command.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);
    }

    [Fact]
    public async Task CrashAfterAck_ExpiredExecutionLeaseRecoversOriginalInputOncePerLease()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("crash-after-ack");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "durable start input",
            "start-key",
            default);
        var firstLease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "worker-a", 300),
            default);
        var running = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                firstLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                firstLease.Lease.LeaseToken,
                LeaseGeneration: firstLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: firstLease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                started.Dispatch!.CommandId,
                started.Dispatch.ClaimToken,
                default));

        clock.Advance(TimeSpan.FromSeconds(301));
        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("replacement-worker", 20, 30),
            default);
        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal("start", recovered.CommandType);
        Assert.Equal(
            "durable start input",
            recovered.Input.GetProperty("message").GetString());
        Assert.Equal(2, recovered.DispatchAttempt);
        Assert.DoesNotContain(
            (await runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("duplicate-worker", 20, 30),
                default)).Items,
            item => item.RunId == started.Run.Id);

        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                recovered.CommandId,
                recovered.ClaimToken,
                default));
        Assert.DoesNotContain(
            (await runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("post-ack-worker", 20, 30),
                default)).Items,
            item => item.RunId == started.Run.Id);

        clock.Advance(TimeSpan.FromSeconds(301));
        var secondGeneration = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("second-replacement", 20, 30),
            default);
        var recoveredAgain = Assert.Single(
            secondGeneration.Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal(3, recoveredAgain.DispatchAttempt);
        Assert.Equal(
            "durable start input",
            recoveredAgain.Input.GetProperty("message").GetString());
    }

    [Fact]
    public async Task DeadlineCleanup_OmitsPrivateEnvelope_AndFailsWithSanitizedAudit()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("deadline-cleanup-failed");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "private input must not be returned",
            "start-key",
            default);
        // 邊界值:timeout 3600 秒,判定是 `DeadlineAt <= now`,所以剛好 3600 秒(等號)就算逾期。
        clock.Advance(TimeSpan.FromSeconds(3_600));

        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("cleanup-worker", 20, 30),
            default);
        var cleanup = Assert.Single(
            recovery.Items,
            item => item.RunId == started.Run!.Id);
        Assert.Equal("deadline_cleanup", cleanup.CommandType);
        Assert.Equal(AgentRunStatuses.Failed, cleanup.TargetTerminal);
        Assert.Equal(JsonValueKind.Undefined, cleanup.Input.ValueKind);
        Assert.Equal("demo-a", cleanup.TenantId);
        Assert.Equal("admin-a", cleanup.UserId);
        Assert.Equal("ADMIN", cleanup.Role);
        Assert.Equal(JsonValueKind.Undefined, cleanup.Snapshot.ValueKind);

        var unsafeEvent = await runs.AppendEventsAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            new AgentRunEventsAppendRequest(
                cleanup.StateVersion,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                cleanup.EventAckCursor,
                new[]
                {
                    new AgentRunEventAppend(
                        Guid.NewGuid(),
                        "model_step",
                        "worker",
                        cleanup.SnapshotHash,
                        JsonSerializer.SerializeToElement(new { status = "unsafe" })),
                }),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, unsafeEvent.Status);
        var audit = await AppendDeadlineAuditAsync(runs, started.Run.Id, cleanup);
        var terminal = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                cleanup.StateVersion,
                AgentRunStatuses.Failed,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                audit.Run!.EventAckCursor,
                V2CheckpointRef(cleanup.LeaseGeneration),
                1,
                ErrorCode: "deadline_exceeded",
                ErrorMessage: "Agent runtime exceeded its authoritative deadline."),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);
        Assert.Equal(AgentRunStatuses.Failed, terminal.Run!.Status);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                cleanup.CommandId,
                cleanup.ClaimToken,
                default));
        var lateCancel = await runs.CancelAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            "too late",
            "late-cancel",
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, lateCancel.Status);
    }

    [Fact]
    public async Task DeadlineCleanup_FreezesCancelledTarget_WhenCancelWinsRunLock()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("deadline-cleanup-cancelled");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "private",
            "start-key",
            default);
        var cancel = await runs.CancelAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            "cancel first",
            "cancel-key",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, cancel.Status);
        // 同上的 `DeadlineAt <= now` 邊界:cancel 已在前面完成,逾期仍以 deadline cleanup 為主。
        clock.Advance(TimeSpan.FromSeconds(3_600));

        var cleanup = Assert.Single(
            (await runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("cleanup-worker", 20, 30),
                default)).Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal("deadline_cleanup", cleanup.CommandType);
        Assert.Equal(AgentRunStatuses.Cancelled, cleanup.TargetTerminal);
        var audit = await AppendDeadlineAuditAsync(runs, started.Run.Id, cleanup);
        var terminal = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                cleanup.StateVersion,
                AgentRunStatuses.Cancelled,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                audit.Run!.EventAckCursor,
                V2CheckpointRef(cleanup.LeaseGeneration),
                1,
                ErrorMessage: "Agent runtime exceeded its authoritative deadline."),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, terminal.Status);
        var replay = await runs.CancelAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            "terminal replay",
            "cancel-after-terminal",
            default);
        Assert.Equal(AgentRunWriteStatus.Replay, replay.Status);
    }

    [Fact]
    public async Task Recovery_DeadLettersPoisonCandidates_WithoutStarvingHealthyRun()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("recovery-poison-isolation");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var corrupt = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "corrupt", "corrupt", default);
        var exhausted = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "exhausted", "exhausted", default);
        var scalarInput = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "scalar", "scalar", default);
        var emptyObjectInput = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "empty", "empty", default);
        var tamperedMessage = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "original", "message", default);
        var tamperedHash = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "hash", "hash", default);
        var tamperedResumeRef = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "resume", "resume", default);
        var healthy = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "healthy", "healthy", default);
        SetPrivateRunField(runs, corrupt.Run!.Id, "SnapshotCanonical", "{}"u8.ToArray());
        SetPrivateRunField(runs, exhausted.Run!.Id, "LeaseGeneration", long.MaxValue);
        SetPrivateCommandInput(
            runs,
            scalarInput.Run!.Id,
            JsonSerializer.SerializeToElement(42));
        SetPrivateCommandInput(
            runs,
            emptyObjectInput.Run!.Id,
            JsonSerializer.SerializeToElement(new { }));
        SetPrivateCommandInput(
            runs,
            tamperedMessage.Run!.Id,
            JsonSerializer.SerializeToElement(new { message = "changed" }));
        SetPrivateCommandHash(
            runs,
            tamperedHash.Run!.Id,
            new string('f', 64));
        var resumeLease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            tamperedResumeRef.Run!.Id,
            new AgentRunLeaseRequest(
                tamperedResumeRef.Run.StateVersion,
                "resume-worker",
                300),
            default);
        var resumeRunning = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            tamperedResumeRef.Run.Id,
            new AgentRunTransitionRequest(
                resumeLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                resumeLease.Lease.LeaseToken,
                resumeLease.Lease.LeaseGeneration,
                resumeLease.Lease.EventAckCursor),
            default);
        var resumeWaiting = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            tamperedResumeRef.Run.Id,
            new AgentRunTransitionRequest(
                resumeRunning.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                resumeLease.Lease.LeaseToken,
                resumeLease.Lease.LeaseGeneration,
                resumeLease.Lease.EventAckCursor,
                V2CheckpointRef(resumeLease.Lease.LeaseGeneration),
                1),
            default);
        Assert.Equal(
            AgentRunWriteStatus.Success,
            (await runs.ResumeAsync(
                "demo-a",
                "admin-a",
                tamperedResumeRef.Run.Id,
                "resume answer",
                1,
                "resume-answer",
                default)).Status);
        SetPrivateCommandInput(
            runs,
            tamperedResumeRef.Run.Id,
            JsonSerializer.SerializeToElement(new
            {
                message = "resume answer",
                expected_checkpoint_version = 1,
                expected_checkpoint_ref =
                    V2CheckpointRef(resumeLease.Lease.LeaseGeneration),
            }));
        clock.Advance(TimeSpan.FromSeconds(301));

        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("worker", 20, 30),
            default);
        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == healthy.Run!.Id);
        Assert.Equal("healthy", recovered.Input.GetProperty("message").GetString());
        var corruptResult = await runs.GetAsync(
            "demo-a", "admin-a", corrupt.Run.Id, default);
        var exhaustedResult = await runs.GetAsync(
            "demo-a", "admin-a", exhausted.Run.Id, default);
        var scalarInputResult = await runs.GetAsync(
            "demo-a", "admin-a", scalarInput.Run.Id, default);
        var emptyObjectInputResult = await runs.GetAsync(
            "demo-a", "admin-a", emptyObjectInput.Run.Id, default);
        var tamperedMessageResult = await runs.GetAsync(
            "demo-a", "admin-a", tamperedMessage.Run.Id, default);
        var tamperedHashResult = await runs.GetAsync(
            "demo-a", "admin-a", tamperedHash.Run.Id, default);
        var tamperedResumeRefResult = await runs.GetAsync(
            "demo-a", "admin-a", tamperedResumeRef.Run.Id, default);
        Assert.Equal(AgentRunStatuses.Failed, corruptResult!.Status);
        Assert.Equal("run_recovery_snapshot_invalid", corruptResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, exhaustedResult!.Status);
        Assert.Equal(
            "run_recovery_generation_exhausted",
            exhaustedResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, scalarInputResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            scalarInputResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, emptyObjectInputResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            emptyObjectInputResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, tamperedMessageResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            tamperedMessageResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, tamperedHashResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            tamperedHashResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, tamperedResumeRefResult!.Status);
        Assert.Equal(
            "run_recovery_command_invalid",
            tamperedResumeRefResult.ErrorCode);
    }

    // 等價類:"run_recovery_counter_exhausted" 有兩個來源——run 自己的計數器
    // (StateVersion/CheckpointVersion/EventAckCursor/LatestEventSequence)接近 long.MaxValue,
    // 以及 command.DispatchAttempts 接近 int.MaxValue。兩者都取臨界值(on-point)。
    [Fact]
    public async Task Recovery_QuarantinesCounterExhaustedCandidates_WithoutStarvingHealthyRun()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("recovery-counter-exhausted");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var stateVersionExhausted = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "state-version", "state-version", default);
        var attemptsExhausted = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "attempts", "attempts", default);
        var healthy = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "healthy", "healthy", default);
        SetPrivateRunField(
            runs, stateVersionExhausted.Run!.Id, "StateVersion", long.MaxValue - 2);
        SetPrivateCommandField(
            runs, attemptsExhausted.Run!.Id, "DispatchAttempts", int.MaxValue - 1);
        clock.Advance(TimeSpan.FromSeconds(31));

        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("worker", 20, 30),
            default);

        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == healthy.Run!.Id);
        Assert.Equal("healthy", recovered.Input.GetProperty("message").GetString());
        var stateVersionResult = await runs.GetAsync(
            "demo-a", "admin-a", stateVersionExhausted.Run.Id, default);
        var attemptsResult = await runs.GetAsync(
            "demo-a", "admin-a", attemptsExhausted.Run.Id, default);
        Assert.Equal(AgentRunStatuses.Failed, stateVersionResult!.Status);
        Assert.Equal(
            "run_recovery_counter_exhausted",
            stateVersionResult.ErrorCode);
        Assert.Equal(AgentRunStatuses.Failed, attemptsResult!.Status);
        Assert.Equal(
            "run_recovery_counter_exhausted",
            attemptsResult.ErrorCode);
    }

    // 等價類:checkpoint seed 不一致(CheckpointVersion 還是 0,卻已有 CheckpointGeneration)
    // 必須以 "run_recovery_seed_invalid" dead-letter,而不是被當成可回收的正常 run。
    [Fact]
    public async Task Recovery_DeadLettersInvalidCheckpointSeed_WithoutStarvingHealthyRun()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("recovery-seed-invalid");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var seedInvalid = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "seed", "seed", default);
        var healthy = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "healthy", "healthy", default);
        SetPrivateRunField(runs, seedInvalid.Run!.Id, "CheckpointGeneration", 5L);
        clock.Advance(TimeSpan.FromSeconds(31));

        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("worker", 20, 30),
            default);

        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == healthy.Run!.Id);
        Assert.Equal("healthy", recovered.Input.GetProperty("message").GetString());
        var seedInvalidResult = await runs.GetAsync(
            "demo-a", "admin-a", seedInvalid.Run.Id, default);
        Assert.Equal(AgentRunStatuses.Failed, seedInvalidResult!.Status);
        Assert.Equal("run_recovery_seed_invalid", seedInvalidResult.ErrorCode);
    }

    [Fact]
    public async Task DeadlineRecovery_DeadLettersBlankPinnedIdentity_WithoutStarvingHealthyRun()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("deadline-identity-isolation");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var corrupt = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "corrupt", "corrupt", default);
        var healthy = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "healthy", "healthy", default);
        SetPrivateRunField(runs, corrupt.Run!.Id, "CallerRole", "   ");
        clock.Advance(TimeSpan.FromSeconds(3_601));

        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("cleanup-worker", 20, 30),
            default);

        var cleanup = Assert.Single(
            recovery.Items,
            item => item.RunId == healthy.Run!.Id);
        Assert.Equal("deadline_cleanup", cleanup.CommandType);
        Assert.Equal("ADMIN", cleanup.Role);
        var corruptResult = await runs.GetAsync(
            "demo-a", "admin-a", corrupt.Run.Id, default);
        Assert.Equal(AgentRunStatuses.Failed, corruptResult!.Status);
        Assert.Equal("run_recovery_identity_invalid", corruptResult.ErrorCode);
    }

    [Fact]
    public async Task WaitingInput_RequiresCheckpointIdentityAndStrictVersionAdvance()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("waiting-checkpoint-invariant");
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "initial prompt",
            "start-key",
            default);
        var lease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "worker-a", 300),
            default);
        var running = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                lease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor),
            default);

        var missingRef = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                running.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointVersion: 1),
            default);
        var nonAdvancedVersion = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 0),
            default);
        var oversizedPendingInput = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1,
                PendingInput: JsonSerializer.SerializeToElement(
                    new { value = new string('p', 64 * 1024) })),
            default);
        var oversizedResult = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1,
                Result: JsonSerializer.SerializeToElement(
                    new { value = new string('r', 1024 * 1024) })),
            default);

        Assert.Equal(AgentRunWriteStatus.InvalidState, missingRef.Status);
        Assert.Equal(AgentRunWriteStatus.InvalidState, nonAdvancedVersion.Status);
        Assert.Equal(AgentRunWriteStatus.InvalidState, oversizedPendingInput.Status);
        Assert.Equal(AgentRunWriteStatus.InvalidState, oversizedResult.Status);
        var unchanged = await runs.GetAsync(
            "demo-a", "admin-a", started.Run.Id, default);
        Assert.Equal(AgentRunStatuses.Running, unchanged!.Status);
        Assert.Equal(running.Run.StateVersion, unchanged.StateVersion);

        var valid = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                running.Run.StateVersion,
                AgentRunStatuses.WaitingInput,
                lease.Lease.LeaseToken,
                LeaseGeneration: lease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: lease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(lease.Lease.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, valid.Status);
        Assert.StartsWith(
            $"v2:{lease.Lease.LeaseGeneration}:",
            valid.Run!.CheckpointRef,
            StringComparison.Ordinal);
        Assert.Equal(1, valid.Run.CheckpointVersion);
    }

    [Fact]
    public async Task ResumeCrashAfterAck_ExpiredLeaseRecoversLatestResumeInput()
    {
        var (agents, skills, agent) =
            await CreatePublishedAgentAsync("resume-crash-after-ack");
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var started = await runs.CreateDirectAsync(
            "demo-a",
            "admin-a",
            "ADMIN",
            agent.Id,
            "initial prompt",
            "start-key",
            default);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run!.Id,
                started.Dispatch!.CommandId,
                started.Dispatch.ClaimToken,
                default));
        var firstLease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "worker-a", 300),
            default);
        var running = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                firstLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                firstLease.Lease.LeaseToken,
                LeaseGeneration: firstLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: firstLease.Lease.EventAckCursor),
            default);
        var waiting = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                running.Run!.StateVersion,
                AgentRunStatuses.WaitingInput,
                firstLease.Lease.LeaseToken,
                LeaseGeneration: firstLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: firstLease.Lease.EventAckCursor,
                CheckpointRef: V2CheckpointRef(firstLease.Lease.LeaseGeneration),
                CheckpointVersion: 1),
            default);
        var resumed = await runs.ResumeAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            "durable resume answer",
            1,
            "resume-key",
            default);
        Assert.Equal(AgentRunWriteStatus.Success, waiting.Status);
        Assert.Equal(AgentRunWriteStatus.Success, resumed.Status);
        var secondLease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunLeaseRequest(
                resumed.Run!.StateVersion,
                "worker-a",
                300),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, secondLease.Status);
        var resumedRunning = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                secondLease.Lease!.Run.StateVersion,
                AgentRunStatuses.Running,
                secondLease.Lease.LeaseToken,
                LeaseGeneration: secondLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: secondLease.Lease.EventAckCursor),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, resumedRunning.Status);
        var secondCheckpointRef =
            V2CheckpointRef(secondLease.Lease.LeaseGeneration);
        var payloadMutation = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                resumedRunning.Run!.StateVersion,
                AgentRunStatuses.Running,
                secondLease.Lease.LeaseToken,
                LeaseGeneration: secondLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: secondLease.Lease.EventAckCursor,
                CheckpointRef: secondCheckpointRef,
                CheckpointVersion: 2,
                Result: JsonSerializer.SerializeToElement(
                    new { forbidden = true })),
            default);
        Assert.Equal(AgentRunWriteStatus.InvalidState, payloadMutation.Status);
        var secondPromotion = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                resumedRunning.Run.StateVersion,
                AgentRunStatuses.Running,
                secondLease.Lease.LeaseToken,
                LeaseGeneration: secondLease.Lease.LeaseGeneration,
                ExpectedEventAckCursor: secondLease.Lease.EventAckCursor,
                CheckpointRef: secondCheckpointRef,
                CheckpointVersion: 2),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, secondPromotion.Status);
        var staleDirectClaim = await runs.ClaimCommandAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            resumed.Dispatch!.CommandId,
            new AgentRunCommandClaimRequest("late-direct-worker", 30),
            default);
        Assert.Equal(
            AgentRunWriteStatus.InvalidState,
            staleDirectClaim.Status);
        Assert.Equal(
            secondPromotion.Run!.StateVersion,
            (await runs.GetAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                default))!.StateVersion);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                resumed.Dispatch.CommandId,
                resumed.Dispatch.ClaimToken,
                default));

        clock.Advance(TimeSpan.FromSeconds(301));
        var recovery = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("resume-replacement", 20, 30),
            default);
        var recovered = Assert.Single(
            recovery.Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal("resume", recovered.CommandType);
        Assert.Equal(
            "durable resume answer",
            recovered.Input.GetProperty("message").GetString());
        Assert.Equal(
            1,
            recovered.Input.GetProperty("expected_checkpoint_version").GetInt64());
        Assert.StartsWith(
            $"v2:{firstLease.Lease.LeaseGeneration}:",
            recovered.Input.GetProperty("expected_checkpoint_ref").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(AgentRunStatuses.Running, recovered.RunStatus);
        Assert.Equal(secondCheckpointRef, recovered.CheckpointRef);
        Assert.Equal(2, recovered.CheckpointVersion);
        var thirdCheckpointRef = V2CheckpointRef(recovered.LeaseGeneration);
        var thirdPromotion = await runs.TransitionAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunTransitionRequest(
                recovered.StateVersion,
                AgentRunStatuses.Running,
                recovered.LeaseToken,
                LeaseGeneration: recovered.LeaseGeneration,
                ExpectedEventAckCursor: recovered.EventAckCursor,
                CheckpointRef: thirdCheckpointRef,
                CheckpointVersion: 3),
            default);
        Assert.Equal(AgentRunWriteStatus.Success, thirdPromotion.Status);
        Assert.Equal(
            AgentRunDispatchCompleteStatus.Success,
            await runs.CompleteDispatchAsync(
                "demo-a",
                "admin-a",
                started.Run.Id,
                recovered.CommandId,
                recovered.ClaimToken,
                default));

        clock.Advance(TimeSpan.FromSeconds(31));
        var secondRecovery = Assert.Single(
            (await runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest(
                    "resume-second-replacement",
                    20,
                    30),
                default)).Items,
            item => item.RunId == started.Run.Id);
        Assert.Equal("resume", secondRecovery.CommandType);
        Assert.Equal(
            1,
            secondRecovery.Input
                .GetProperty("expected_checkpoint_version")
                .GetInt64());
        Assert.Equal(
            waiting.Run!.CheckpointRef,
            secondRecovery.Input.GetProperty("expected_checkpoint_ref").GetString());
        Assert.Equal(thirdCheckpointRef, secondRecovery.CheckpointRef);
        Assert.Equal(3, secondRecovery.CheckpointVersion);
        Assert.DoesNotContain(
            (await runs.ClaimRecoveryAsync(
                new AgentRunRecoveryClaimRequest("resume-duplicate", 20, 30),
                default)).Items,
            item => item.RunId == started.Run.Id);
    }

    private static Task<AgentRunWriteResult> AppendDeadlineAuditAsync(
        InMemoryAgentRunRepository runs,
        Guid runId,
        AgentRunRecoveryItem cleanup)
        => runs.AppendEventsAsync(
            "demo-a",
            "admin-a",
            runId,
            new AgentRunEventsAppendRequest(
                cleanup.StateVersion,
                cleanup.LeaseToken,
                cleanup.LeaseGeneration,
                cleanup.EventAckCursor,
                new[]
                {
                    new AgentRunEventAppend(
                        Guid.NewGuid(),
                        "deadline_exceeded",
                        "cleanup",
                        cleanup.SnapshotHash,
                        JsonSerializer.SerializeToElement(
                            new { error_code = "deadline_exceeded" })),
                }),
            default);

    private static void SetPrivateRunField(
        InMemoryAgentRunRepository runs,
        Guid runId,
        string fieldName,
        object value)
    {
        var runsField = typeof(InMemoryAgentRunRepository).GetField(
            "_runs",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!;
        var entries = (System.Collections.IDictionary)runsField.GetValue(runs)!;
        var entry = entries[runId]!;
        entry.GetType().GetField(fieldName)!.SetValue(entry, value);
    }

    private static void SetPrivateCommandInput(
        InMemoryAgentRunRepository runs,
        Guid runId,
        JsonElement input)
        => SetPrivateCommandField(runs, runId, "Input", input);

    private static void SetPrivateCommandHash(
        InMemoryAgentRunRepository runs,
        Guid runId,
        string inputHash)
        => SetPrivateCommandField(runs, runId, "InputHash", inputHash);

    private static void SetPrivateCommandField(
        InMemoryAgentRunRepository runs,
        Guid runId,
        string fieldName,
        object value)
    {
        var commandsField = typeof(InMemoryAgentRunRepository).GetField(
            "_commands",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!;
        var commands = (System.Collections.IDictionary)commandsField.GetValue(runs)!;
        var command = commands.Values.Cast<object>()
            .Where(item =>
                (Guid)item.GetType().GetField("RunId")!.GetValue(item)! == runId)
            .OrderBy(item =>
                (long)item.GetType().GetField("Sequence")!.GetValue(item)!)
            .Last();
        command.GetType().GetField(fieldName)!.SetValue(command, value);
    }

    // has_more 是 `candidates.Length > limit`:limit 剛好等於候選數必須是 false(否則 worker 永遠
    // 以為還有工作、無止盡輪詢),limit 少一個才是 true。N 與 N+1 兩側都要測。
    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 3, false)]
    public async Task Recovery_HasMoreOnlyWhenCandidatesExceedLimit(
        int limit, int expectedItems, bool expectedHasMore)
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync("recovery-has-more");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(
                AgentRunWriteStatus.Success,
                (await runs.CreateDirectAsync(
                    "demo-a", "admin-a", "ADMIN", agent.Id, "m" + index, "key-" + index, default)).Status);
        }
        clock.Advance(TimeSpan.FromSeconds(31)); // 三筆 dispatch claim 全部過期 → 三個候選

        var claimed = await runs.ClaimRecoveryAsync(
            new AgentRunRecoveryClaimRequest("scrubber", limit, 30), default);

        Assert.Equal(expectedItems, claimed.Items.Count);
        Assert.Equal(expectedHasMore, claimed.HasMore);
    }

    // 邊界值:lease duration 合法區間是 `is < 5 or > 900`,兩端各測 on-point(5/900 成立)與
    // off-point(4/901 被擋)。其他測試一律用區間中央的 300,測不出把 5 寫成 6 之類的錯。
    [Theory]
    [InlineData(4, AgentRunWriteStatus.InvalidState)]
    [InlineData(5, AgentRunWriteStatus.Success)]
    [InlineData(900, AgentRunWriteStatus.Success)]
    [InlineData(901, AgentRunWriteStatus.InvalidState)]
    public async Task ClaimLease_AcceptsOnlyDurationBetweenFiveAndNineHundredSeconds(
        int durationSeconds, AgentRunWriteStatus expected)
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync("lease-duration-bounds");
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var started = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "start", "start-key", default);

        var lease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "worker-a", durationSeconds),
            default);

        Assert.Equal(expected, lease.Status);
        Assert.Equal(expected == AgentRunWriteStatus.Success, lease.Lease is not null);
    }

    // 邊界值:單次 append 的 events 數量限制是 `is < 1 or > 100`,所以 100 必須成功、101 與空陣列
    // 都必須被擋下;被擋下時 event_ack_cursor 不可前進(部分寫入等於帳目破洞)。
    [Fact]
    public async Task UnicodeNodeId_ExactReplayMatchesDapperContract()
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync("unicode-node");
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var created = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "start", "unicode-node-start", default);
        var lease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            created.Run!.Id,
            new AgentRunLeaseRequest(created.Run.StateVersion, "worker-a", 300),
            default);
        var nodeId = new string('n', 199) + "😀";
        var appendedEvent = new AgentRunEventAppend(
            Guid.NewGuid(),
            "model_step",
            nodeId,
            created.Run.SnapshotHash,
            JsonSerializer.SerializeToElement(new { step = 1 }));
        var request = new AgentRunEventsAppendRequest(
            lease.Lease!.Run.StateVersion,
            lease.Lease.LeaseToken,
            lease.Lease.LeaseGeneration,
            lease.Lease.EventAckCursor,
            new[] { appendedEvent });

        var appended = await runs.AppendEventsAsync(
            "demo-a", "admin-a", created.Run.Id, request, default);
        var replay = await runs.AppendEventsAsync(
            "demo-a", "admin-a", created.Run.Id, request, default);
        var events = await runs.GetEventsAsync(
            "demo-a", "admin-a", created.Run.Id, 0, 10, default);

        Assert.Equal(AgentRunWriteStatus.Success, appended.Status);
        Assert.Equal(AgentRunWriteStatus.Replay, replay.Status);
        Assert.Equal(
            new string('n', 199),
            Assert.Single(events!.Events, item => item.EventId == appendedEvent.EventId).NodeId);
    }

    [Theory]
    [InlineData(0, AgentRunWriteStatus.InvalidState)]
    [InlineData(100, AgentRunWriteStatus.Success)]
    [InlineData(101, AgentRunWriteStatus.InvalidState)]
    public async Task AppendEvents_AcceptsAtMostOneHundredEventsPerBatch(
        int eventCount, AgentRunWriteStatus expected)
    {
        var (agents, skills, agent) = await CreatePublishedAgentAsync("append-batch-bounds");
        var runs = new InMemoryAgentRunRepository(agents, skills);
        var started = await runs.CreateDirectAsync(
            "demo-a", "admin-a", "ADMIN", agent.Id, "start", "start-key", default);
        var lease = await runs.ClaimLeaseAsync(
            "demo-a",
            "admin-a",
            started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "worker-a", 300),
            default);
        var events = Enumerable.Range(0, eventCount)
            .Select(index => new AgentRunEventAppend(
                Guid.NewGuid(),
                "model_step",
                "worker",
                started.Run.SnapshotHash,
                JsonSerializer.SerializeToElement(new { step = index })))
            .ToArray();

        var appended = await runs.AppendEventsAsync(
            "demo-a",
            "admin-a",
            started.Run.Id,
            new AgentRunEventsAppendRequest(
                lease.Lease!.Run.StateVersion,
                lease.Lease.LeaseToken,
                lease.Lease.LeaseGeneration,
                lease.Lease.EventAckCursor,
                events),
            default);

        Assert.Equal(expected, appended.Status);
        Assert.Equal(
            (long)(expected == AgentRunWriteStatus.Success ? eventCount : 0),
            (await runs.GetAsync("demo-a", "admin-a", started.Run.Id, default))!.EventAckCursor);
    }

    private static string V2CheckpointRef(long generation)
        => $"v2:{generation}:{new string('b', 64)}:{Guid.NewGuid():D}";

    private static void SetPublishedWorkflowRevision(
        InMemoryAgentRepository agents, Guid agentId, string definition, int workflowRevision)
    {
        var entries = (System.Collections.IEnumerable)typeof(InMemoryAgentRepository)
            .GetField("_agents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(agents)!;
        var entry = entries.Cast<object>().Single(item =>
            (Guid)item.GetType().GetField("Id")!.GetValue(item)! == agentId);
        var revisions = (System.Collections.IList)entry.GetType().GetField("Revisions")!.GetValue(entry)!;
        var revision = revisions.Cast<object>().Single();
        revision.GetType().GetField("DefinitionSnapshot")!.SetValue(revision, definition);
        revision.GetType().GetField("DefinitionSha256")!.SetValue(revision, SkillHash.Sha256(definition));
        revision.GetType().GetField("RuntimeWorkflowRevision")!.SetValue(revision, (int?)workflowRevision);
    }

    private static async Task<(
        InMemoryAgentRepository Agents,
        InMemorySkillRepository Skills,
        Agent Agent)> CreatePublishedAgentAsync(
        string slug,
        string[]? executionRoles = null)
    {
        var skills = new InMemorySkillRepository();
        var agents = new InMemoryAgentRepository(skills);
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "test",
            ExecutionRoles: executionRoles ?? new[] { "worker" },
            Capabilities: null,
            OutputContract: null,
            Audience: new[] { "ADMIN" },
            AllowedTools: Array.Empty<string>(),
            SkillBindings: null,
            KnowledgeSources: Array.Empty<string>(),
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(
                TimeoutSeconds: 3_600,
                StepBudget: 4),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync(
            "demo-a",
            slug,
            "Cancel recovery",
            "test",
            definition,
            hash,
            "admin-a",
            default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync(
            "demo-a",
            agent!.Id,
            agent.DraftVersion,
            definition,
            hash,
            default));
        Assert.Equal(
            AgentWriteStatus.Success,
            (await agents.PublishAsync(
                "demo-a",
                agent.Id,
                agent.DraftVersion,
                definition,
                hash,
                "admin-a",
                default)).Status);
        return (agents, skills, agent);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
