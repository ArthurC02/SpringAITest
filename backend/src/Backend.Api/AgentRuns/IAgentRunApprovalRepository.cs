namespace Backend.Api.AgentRuns;

public interface IAgentRunApprovalRepository
{
    Task<AgentRunApprovalWriteResult> CreateAsync(string tenantId, string userId, Guid runId, AgentRunApprovalCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<AgentRunApprovalResponse>?> ListAsync(string tenantId, string userId, string role, Guid runId, CancellationToken ct);

    /// <summary>
    /// O3 discoverable approval queue, spanning every run in the tenant. When
    /// <paramref name="actionableOnly"/> is false this is the "visible to me" predicate (the
    /// caller's own requested approvals, plus any role-eligible non-owner approval regardless of
    /// status/expiry); when true it is "actionable to me" and reuses <see cref="DecideAsync"/>'s
    /// exact authorization predicate word for word (tenant, required role, pending, not expired,
    /// separation of duties) so the two can never drift. Ordered created_at DESC, id DESC;
    /// <paramref name="position"/> is an exclusive keyset cursor in that same order.
    /// </summary>
    Task<IReadOnlyList<AgentRunApprovalQueueItem>> ListQueueAsync(
        string tenantId, string userId, string role, bool actionableOnly,
        AgentRunApprovalQueuePosition? position, int limit, CancellationToken ct);
    Task<AgentRunApprovalWriteResult> DecideAsync(string tenantId, string approverId, string approverRole, Guid runId, Guid approvalId, bool approve, string idempotencyKey, string? reason, CancellationToken ct);
    Task<(AgentRunApprovalWriteStatus Status, AgentRunApprovalConsumeResponse? Response, string? Message)> ConsumeAsync(string tenantId, Guid runId, Guid approvalId, AgentRunApprovalConsumeRequest request, CancellationToken ct);
    Task<AgentRunApprovalWriteStatus> CompleteEffectAsync(string tenantId, Guid runId, Guid effectId, bool succeeded, CancellationToken ct);
    Task<(AgentRunApprovalWriteStatus Status, AgentRunWriteEvidenceResponse? Response, string? Message)> WriteEvidenceAsync(string tenantId, Guid runId, Guid effectId, AgentRunWriteEvidenceRequest request, CancellationToken ct);
    Task<AgentRunApprovalExecutionIdentity?> GetExecutionIdentityAsync(string tenantId, string approverId, Guid runId, Guid approvalId, CancellationToken ct);
    Task<AgentRunApprovalExecuteClaim?> ClaimExecuteAsync(string tenantId, Guid runId, Guid approvalId, CancellationToken ct);
    Task<IReadOnlyList<AgentRunApprovalExecuteClaim>> ClaimExecuteRecoveryAsync(int limit, CancellationToken ct);
    Task<AgentRunApprovalWriteStatus> CompleteExecuteAsync(Guid approvalId, string claimToken, bool deadLetter, CancellationToken ct);
}
