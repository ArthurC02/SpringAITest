using Backend.Api.Agents;
using Backend.Api.AgentRuns;
using Backend.Api.Data.InMemory;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>Parity coverage for the lite D7 approval/effect state machine.</summary>
public sealed class AgentRunApprovalInMemoryTests
{
    [Fact]
    public async Task Approval_EnforcesSoDLeaseClaimExpiryAndEvidenceIdempotency()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        var (agents, skills, agent) = await PublishedAgentAsync("d7-inmemory");
        var runs = new InMemoryAgentRunRepository(agents, skills, clock);
        var approvals = new InMemoryAgentRunApprovalRepository(runs, clock);
        var running = await StartRunningAsync(runs, agent.Id);
        var fingerprint = new string('b', 64);
        var created = await approvals.CreateAsync("demo-a", "admin-a", running.Run!.Id,
            new AgentRunApprovalCreateRequest(running.Run.StateVersion, running.Token, running.Generation,
                Checkpoint(running.Generation), 1, "USER", fingerprint, clock.GetUtcNow().UtcDateTime.AddMinutes(5), SelfApprovalForbidden: false), default);
        Assert.True(created.Status == AgentRunApprovalWriteStatus.Success, created.Message);
        Assert.True(created.Approval!.SelfApprovalForbidden);
        Assert.Equal(AgentRunApprovalWriteStatus.Forbidden,
            (await approvals.DecideAsync("demo-a", "admin-a", "USER", running.Run.Id, created.Approval.Id, true, "self", null, default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.NotFound,
            (await approvals.DecideAsync("demo-b", "approver-b", "USER", running.Run.Id, created.Approval.Id, true, "cross", null, default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await approvals.DecideAsync("demo-a", "approver-a", "USER", running.Run.Id, created.Approval.Id, true, "approve", null, default)).Status);

        var firstClaim = await approvals.ClaimExecuteAsync("demo-a", running.Run.Id, created.Approval.Id, default);
        Assert.NotNull(firstClaim);
        clock.Advance(TimeSpan.FromSeconds(61));
        var reclaimed = await approvals.ClaimExecuteAsync("demo-a", running.Run.Id, created.Approval.Id, default);
        Assert.NotNull(reclaimed);
        Assert.NotEqual(firstClaim!.ClaimToken, reclaimed!.ClaimToken);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            await approvals.CompleteExecuteAsync(created.Approval.Id, firstClaim.ClaimToken, false, default));

        var queued = await runs.GetAsync("demo-a", "admin-a", running.Run.Id, default);
        var writeLease = await runs.ClaimLeaseAsync("demo-a", "admin-a", running.Run.Id,
            new AgentRunLeaseRequest(queued!.StateVersion, "write-worker", 120), default);
        var writeRunning = await runs.TransitionAsync("demo-a", "admin-a", running.Run.Id,
            new AgentRunTransitionRequest(writeLease.Lease!.Run.StateVersion, AgentRunStatuses.Running,
                writeLease.Lease.LeaseToken, writeLease.Lease.LeaseGeneration, writeLease.Lease.EventAckCursor), default);
        Assert.Equal(AgentRunWriteStatus.Success, writeRunning.Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            (await approvals.ConsumeAsync("demo-a", running.Run.Id, created.Approval.Id,
                new AgentRunApprovalConsumeRequest(fingerprint, "stale", running.Generation), default)).Status);
        var effect = await approvals.ConsumeAsync("demo-a", running.Run.Id, created.Approval.Id,
            new AgentRunApprovalConsumeRequest(fingerprint, writeLease.Lease.LeaseToken, writeLease.Lease.LeaseGeneration), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, effect.Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await approvals.WriteEvidenceAsync("demo-a", running.Run.Id, effect.Response!.EffectId,
                new AgentRunWriteEvidenceRequest("refund-1", "approved"), default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Replay,
            (await approvals.WriteEvidenceAsync("demo-a", running.Run.Id, effect.Response.EffectId,
                new AgentRunWriteEvidenceRequest("refund-1", "approved"), default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            (await approvals.WriteEvidenceAsync("demo-a", running.Run.Id, effect.Response.EffectId,
                new AgentRunWriteEvidenceRequest("refund-1", "changed"), default)).Status);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            await approvals.CompleteExecuteAsync(created.Approval.Id, reclaimed.ClaimToken, false, default));

        // A pending expired approval is terminalized, rather than leaving a
        // permanently waiting_approval run with no recovery command.
        var expiredRun = await StartRunningAsync(runs, agent.Id, "expired");
        var expired = await approvals.CreateAsync("demo-a", "admin-a", expiredRun.Run!.Id,
            new AgentRunApprovalCreateRequest(expiredRun.Run.StateVersion, expiredRun.Token, expiredRun.Generation,
                Checkpoint(expiredRun.Generation), 1, "USER", new string('c', 64), clock.GetUtcNow().UtcDateTime.AddSeconds(1)), default);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(AgentRunApprovalWriteStatus.Expired,
            (await approvals.DecideAsync("demo-a", "approver-a", "USER", expiredRun.Run.Id, expired.Approval!.Id, true, "expired", null, default)).Status);
        Assert.Equal(AgentRunStatuses.Failed, (await runs.GetAsync("demo-a", "admin-a", expiredRun.Run.Id, default))!.Status);

        // A waiting approval is active work for cancellation recovery even
        // though generic resume is forbidden.  If the initial Workflow kick is
        // lost, the durable cancel command must still be claimable.
        var cancellable = await StartRunningAsync(runs, agent.Id, "cancel-waiting");
        var waiting = await approvals.CreateAsync("demo-a", "admin-a", cancellable.Run!.Id,
            new AgentRunApprovalCreateRequest(cancellable.Run.StateVersion, cancellable.Token, cancellable.Generation,
                Checkpoint(cancellable.Generation), 1, "USER", new string('d', 64), clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, waiting.Status);
        var cancelled = await runs.CancelAsync("demo-a", "admin-a", cancellable.Run.Id, "cancel approval", "cancel-waiting", default);
        Assert.Equal(AgentRunStatuses.WaitingApproval, cancelled.Run!.Status);
        clock.Advance(TimeSpan.FromSeconds(31)); // simulate a lost initial Workflow kick
        var recovery = await runs.ClaimRecoveryAsync(new AgentRunRecoveryClaimRequest("recovery", 20, 30), default);
        Assert.Contains(recovery.Items, item => item.RunId == cancellable.Run.Id && item.CommandType == "cancel");
        var cancelWins = await approvals.DecideAsync("demo-a", "approver-a", "USER", cancellable.Run.Id,
            waiting.Approval!.Id, true, "approve-after-cancel", null, default);
        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState, cancelWins.Status);
        Assert.Equal("cancelled", cancelWins.Approval!.Status);
        Assert.Null(await approvals.ClaimExecuteAsync("demo-a", cancellable.Run.Id, waiting.Approval.Id, default));
        Assert.Equal(AgentRunApprovalWriteStatus.Conflict,
            (await approvals.ConsumeAsync("demo-a", cancellable.Run.Id, waiting.Approval.Id,
                new AgentRunApprovalConsumeRequest(new string('d', 64), cancellable.Token, cancellable.Generation), default)).Status);

        // Once a write effect is reserved, cancellation still fences the
        // durable evidence/outbox transaction before it can make the write.
        var reserveThenCancel = await StartRunningAsync(runs, agent.Id, "reserve-then-cancel");
        var reserveApproval = await approvals.CreateAsync("demo-a", "admin-a", reserveThenCancel.Run!.Id,
            new AgentRunApprovalCreateRequest(reserveThenCancel.Run.StateVersion, reserveThenCancel.Token, reserveThenCancel.Generation,
                Checkpoint(reserveThenCancel.Generation), 1, "USER", new string('e', 64), clock.GetUtcNow().UtcDateTime.AddMinutes(5)), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success,
            (await approvals.DecideAsync("demo-a", "approver-a", "USER", reserveThenCancel.Run.Id, reserveApproval.Approval!.Id, true, "reserve-approve", null, default)).Status);
        var reserveQueued = await runs.GetAsync("demo-a", "admin-a", reserveThenCancel.Run.Id, default);
        var reserveLease = await runs.ClaimLeaseAsync("demo-a", "admin-a", reserveThenCancel.Run.Id,
            new AgentRunLeaseRequest(reserveQueued!.StateVersion, "write-worker", 120), default);
        Assert.Equal(AgentRunWriteStatus.Success,
            (await runs.TransitionAsync("demo-a", "admin-a", reserveThenCancel.Run.Id,
                new AgentRunTransitionRequest(reserveLease.Lease!.Run.StateVersion, AgentRunStatuses.Running, reserveLease.Lease.LeaseToken,
                    reserveLease.Lease.LeaseGeneration, reserveLease.Lease.EventAckCursor), default)).Status);
        var reserved = await approvals.ConsumeAsync("demo-a", reserveThenCancel.Run.Id, reserveApproval.Approval.Id,
            new AgentRunApprovalConsumeRequest(new string('e', 64), reserveLease.Lease.LeaseToken, reserveLease.Lease.LeaseGeneration), default);
        Assert.Equal(AgentRunApprovalWriteStatus.Success, reserved.Status);
        await runs.CancelAsync("demo-a", "admin-a", reserveThenCancel.Run.Id, "cancel reserved write", "reserve-cancel", default);
        Assert.Equal(AgentRunApprovalWriteStatus.InvalidState,
            (await approvals.WriteEvidenceAsync("demo-a", reserveThenCancel.Run.Id, reserved.Response!.EffectId,
                new AgentRunWriteEvidenceRequest("must-not-write", "cancelled"), default)).Status);
    }

    private static async Task<(AgentRunResponse Run, string Token, long Generation)> StartRunningAsync(InMemoryAgentRunRepository runs, Guid agentId, string key = "start")
    {
        var started = await runs.CreateDirectAsync("demo-a", "admin-a", "ADMIN", agentId, "write", key + Guid.NewGuid().ToString("N"), default);
        var lease = await runs.ClaimLeaseAsync("demo-a", "admin-a", started.Run!.Id,
            new AgentRunLeaseRequest(started.Run.StateVersion, "workflow", 120), default);
        var running = await runs.TransitionAsync("demo-a", "admin-a", started.Run.Id,
            new AgentRunTransitionRequest(lease.Lease!.Run.StateVersion, AgentRunStatuses.Running,
                lease.Lease.LeaseToken, lease.Lease.LeaseGeneration, lease.Lease.EventAckCursor), default);
        Assert.Equal(AgentRunWriteStatus.Success, running.Status);
        return (running.Run!, lease.Lease.LeaseToken, lease.Lease.LeaseGeneration);
    }

    private static async Task<(InMemoryAgentRepository Agents, InMemorySkillRepository Skills, Agent Agent)> PublishedAgentAsync(string slug)
    {
        var skills = new InMemorySkillRepository();
        var agents = new InMemoryAgentRepository(skills);
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null, Name: null, Description: null, SystemPrompt: "test", ExecutionRoles: new[] { "worker" },
            Capabilities: null, OutputContract: null, Audience: new[] { "ADMIN" }, AllowedTools: Array.Empty<string>(),
            SkillBindings: null, KnowledgeSources: Array.Empty<string>(), BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(TimeoutSeconds: 3600, StepBudget: 4),
            RuntimeWorkflow: new AgentWorkflowRef(AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));
        var hash = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync("demo-a", slug, "D7", "test", definition, hash, "admin-a", default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync("demo-a", agent!.Id, agent.DraftVersion, definition, hash, default));
        Assert.Equal(AgentWriteStatus.Success,
            (await agents.PublishAsync("demo-a", agent.Id, agent.DraftVersion, definition, hash, "admin-a", default)).Status);
        return (agents, skills, agent);
    }

    private static string Checkpoint(long generation) => $"v2:{generation}:{new string('d', 64)}:{Guid.NewGuid():D}";

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
