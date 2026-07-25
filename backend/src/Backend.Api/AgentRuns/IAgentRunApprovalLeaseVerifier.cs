namespace Backend.Api.AgentRuns;

/// <summary>
/// Internal parity seam for Lite mode.  Approval consume is security-sensitive:
/// it must verify the same active generation-fenced lease as the PostgreSQL
/// repository instead of trusting a private approval cache.
/// </summary>
public interface IAgentRunApprovalLeaseVerifier
{
    bool HasActiveApprovalLease(string tenantId, string userId, Guid runId, string leaseToken, long leaseGeneration);
}
