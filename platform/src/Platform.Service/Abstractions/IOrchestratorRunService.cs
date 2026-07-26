using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>D5 root-run allocation proxy.  A 202 means Backend durably accepted the command;
/// it never implies Workflow has claimed or executed it.</summary>
public interface IOrchestratorRunService
{
    Task<AgentProxyResponse> StartAsync(Guid orchestratorId, string? message, string? conversationId, string? key, UserContext context, CancellationToken ct = default);
    Task<AgentProxyResponse> GetAsync(Guid runId, UserContext context, CancellationToken ct = default);
    Task<AgentProxyResponse> EventsAsync(Guid runId, long after, int limit, UserContext context, CancellationToken ct = default);
    Task<AgentProxyResponse> CancelAsync(Guid runId, string? reason, string? key, UserContext context, CancellationToken ct = default);
}
