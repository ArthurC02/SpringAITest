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

    /// <summary>
    /// Mirrors the PostgreSQL authority's direct terminal UPDATE for an approval
    /// that can no longer complete.  Terminalization is a server decision, not a
    /// worker transition: it holds no lease, promotes no checkpoint, and applies
    /// only to the exact statuses the SQL WHERE clause allows.
    /// </summary>
    Task FailApprovalRunAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunApprovalFailure failure,
        CancellationToken ct);
}

public enum AgentRunApprovalFailure
{
    /// <summary>waiting_approval expired before a decision.</summary>
    ApprovalExpired,

    /// <summary>An approved approval expired before its write could be claimed and executed.</summary>
    ApprovalExpiredBeforeWrite,

    /// <summary>An approved write was dead-lettered and cannot be resumed safely.</summary>
    ApprovedWriteUnrecoverable,
}
