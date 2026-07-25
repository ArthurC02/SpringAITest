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
}
