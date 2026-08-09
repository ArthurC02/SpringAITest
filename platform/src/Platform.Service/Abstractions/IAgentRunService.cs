using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// D3 direct Agent test-run orchestration. Backend remains the durable authority; Workflow
/// executes only the exact immutable snapshot allocated by Backend.
/// </summary>
public interface IAgentRunService
{
    Task<AgentProxyResponse> StartAsync(
        Guid agentId,
        string? message,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default);

    Task<AgentProxyResponse> GetAsync(
        Guid runId,
        UserContext ctx,
        CancellationToken ct = default);

    Task<AgentProxyResponse> EventsAsync(
        Guid runId,
        long afterSequence,
        int limit,
        UserContext ctx,
        CancellationToken ct = default);

    Task<AgentProxyResponse> ResumeAsync(
        Guid runId,
        string? message,
        long? expectedCheckpointVersion,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default);

    Task<AgentProxyResponse> CancelAsync(
        Guid runId,
        string? reason,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default);

    /// <summary>
    /// D7 approval queue for a run.  This deliberately has no admin/capability parameter:
    /// Backend is the authority for tenant, required-role and separation-of-duties checks.
    /// </summary>
    Task<AgentProxyResponse> ApprovalsAsync(
        Guid runId,
        UserContext ctx,
        CancellationToken ct = default);

    /// <summary>
    /// O3 discoverable approval queue, spanning every run in the tenant (scope is "visible" or
    /// "actionable" — see 04-operations-trigger-plan.md §4). A transparent proxy like
    /// <see cref="ApprovalsAsync"/>: Backend owns both predicates and the keyset cursor.
    /// </summary>
    Task<AgentProxyResponse> QueueAsync(
        string scope,
        string? cursor,
        int limit,
        UserContext ctx,
        CancellationToken ct = default);

    Task<AgentProxyResponse> DecideApprovalAsync(
        Guid runId,
        Guid approvalId,
        bool approve,
        string? reason,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default);
}
