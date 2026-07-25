namespace Backend.Api.AgentRuns;

public interface IAgentRunApprovalRepository
{
    Task<AgentRunApprovalWriteResult> CreateAsync(string tenantId, string userId, Guid runId, AgentRunApprovalCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<AgentRunApprovalResponse>?> ListAsync(string tenantId, string userId, string role, Guid runId, CancellationToken ct);
    Task<AgentRunApprovalWriteResult> DecideAsync(string tenantId, string approverId, string approverRole, Guid runId, Guid approvalId, bool approve, string idempotencyKey, string? reason, CancellationToken ct);
    Task<(AgentRunApprovalWriteStatus Status, AgentRunApprovalConsumeResponse? Response, string? Message)> ConsumeAsync(string tenantId, Guid runId, Guid approvalId, AgentRunApprovalConsumeRequest request, CancellationToken ct);
    Task<AgentRunApprovalWriteStatus> CompleteEffectAsync(string tenantId, Guid runId, Guid effectId, bool succeeded, CancellationToken ct);
    Task<(AgentRunApprovalWriteStatus Status, AgentRunWriteEvidenceResponse? Response, string? Message)> WriteEvidenceAsync(string tenantId, Guid runId, Guid effectId, AgentRunWriteEvidenceRequest request, CancellationToken ct);
    Task<AgentRunApprovalExecutionIdentity?> GetExecutionIdentityAsync(string tenantId, string approverId, Guid runId, Guid approvalId, CancellationToken ct);
    Task<AgentRunApprovalExecuteClaim?> ClaimExecuteAsync(string tenantId, Guid runId, Guid approvalId, CancellationToken ct);
    Task<IReadOnlyList<AgentRunApprovalExecuteClaim>> ClaimExecuteRecoveryAsync(int limit, CancellationToken ct);
    Task<AgentRunApprovalWriteStatus> CompleteExecuteAsync(Guid approvalId, string claimToken, bool deadLetter, CancellationToken ct);
}
