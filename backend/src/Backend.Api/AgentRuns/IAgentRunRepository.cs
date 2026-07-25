namespace Backend.Api.AgentRuns;

public interface IAgentRunRepository
{
    Task<AgentRunWriteResult> CreateDirectAsync(
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        Guid agentId,
        string message,
        string idempotencyKey,
        CancellationToken ct);

    Task<AgentRunResponse?> GetAsync(
        string tenantId, string userId, Guid runId, CancellationToken ct);

    Task<string?> GetExecutionArtifactAsync(
        string tenantId, string userId, Guid runId, CancellationToken ct);

    Task<AgentRunEventsResponse?> GetEventsAsync(
        string tenantId,
        string userId,
        Guid runId,
        long afterSequence,
        int limit,
        CancellationToken ct);

    Task<AgentRunWriteResult> ResumeAsync(
        string tenantId,
        string userId,
        Guid runId,
        string message,
        long expectedCheckpointVersion,
        string idempotencyKey,
        CancellationToken ct);

    Task<AgentRunWriteResult> CancelAsync(
        string tenantId,
        string userId,
        Guid runId,
        string? reason,
        string idempotencyKey,
        CancellationToken ct);

    Task<AgentRunWriteResult> TransitionAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunTransitionRequest request,
        CancellationToken ct);

    Task<AgentRunWriteResult> AppendEventsAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunEventsAppendRequest request,
        CancellationToken ct);

    Task<AgentRunLeaseResult> ClaimLeaseAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunLeaseRequest request,
        CancellationToken ct);

    Task<AgentRunCommandClaimResult> ClaimCommandAsync(
        string tenantId,
        string userId,
        Guid runId,
        Guid commandId,
        AgentRunCommandClaimRequest request,
        CancellationToken ct);

    Task<AgentRunDispatchCompleteStatus> CompleteDispatchAsync(
        string tenantId,
        string userId,
        Guid runId,
        Guid commandId,
        string claimToken,
        CancellationToken ct);

    Task<AgentRunRecoveryClaimResponse> ClaimRecoveryAsync(
        AgentRunRecoveryClaimRequest request,
        CancellationToken ct);
}
