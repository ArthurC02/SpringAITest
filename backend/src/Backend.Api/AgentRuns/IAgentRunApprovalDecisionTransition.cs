namespace Backend.Api.AgentRuns;

/// <summary>
/// Internal Lite-mode equivalent of Dapper's approval decision transaction.
/// This is deliberately narrower than <see cref="IAgentRunRepository"/>:
/// only a waiting_approval row at its exact state version can be resolved, and
/// it never weakens the public generic transition lease rules.
/// </summary>
public interface IAgentRunApprovalDecisionTransition
{
    Task<AgentRunWriteResult> ResolveApprovalAsync(
        string tenantId,
        string userId,
        Guid runId,
        long expectedVersion,
        bool approve,
        CancellationToken ct);
}
