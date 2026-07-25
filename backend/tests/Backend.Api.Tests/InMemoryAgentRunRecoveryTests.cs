using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;
using System.Text.Json;

namespace Backend.Api.Tests;

public sealed class InMemoryAgentRunRecoveryTests
{
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

        clock.Advance(TimeSpan.FromSeconds(31));
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
        clock.Advance(TimeSpan.FromSeconds(3_601));

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
        clock.Advance(TimeSpan.FromSeconds(3_601));

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
    {
        var commandsField = typeof(InMemoryAgentRunRepository).GetField(
            "_commands",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!;
        var commands = (System.Collections.IDictionary)commandsField.GetValue(runs)!;
        var command = commands.Values.Cast<object>()
            .Where(value =>
                (Guid)value.GetType().GetField("RunId")!.GetValue(value)! == runId)
            .OrderBy(value =>
                (long)value.GetType().GetField("Sequence")!.GetValue(value)!)
            .Last();
        command.GetType().GetField("Input")!.SetValue(command, input);
    }

    private static void SetPrivateCommandHash(
        InMemoryAgentRunRepository runs,
        Guid runId,
        string inputHash)
    {
        var commandsField = typeof(InMemoryAgentRunRepository).GetField(
            "_commands",
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!;
        var commands = (System.Collections.IDictionary)commandsField.GetValue(runs)!;
        var command = commands.Values.Cast<object>()
            .Where(value =>
                (Guid)value.GetType().GetField("RunId")!.GetValue(value)! == runId)
            .OrderBy(value =>
                (long)value.GetType().GetField("Sequence")!.GetValue(value)!)
            .Last();
        command.GetType().GetField("InputHash")!.SetValue(command, inputHash);
    }

    private static string V2CheckpointRef(long generation)
        => $"v2:{generation}:{new string('b', 64)}:{Guid.NewGuid():D}";

    private static async Task<(
        InMemoryAgentRepository Agents,
        InMemorySkillRepository Skills,
        Agent Agent)> CreatePublishedAgentAsync(string slug)
    {
        var skills = new InMemorySkillRepository();
        var agents = new InMemoryAgentRepository(skills);
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: "test",
            ExecutionRoles: new[] { "worker" },
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
