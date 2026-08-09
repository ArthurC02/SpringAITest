using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// O2 "Unified Runs and Tasks center" (04-operations-trigger-plan.md §3). A transparent proxy like
/// <see cref="IAgentRunService.QueueAsync"/>'s O3 queue: Backend owns the tenant/owner/ADMIN-gated
/// visibility rule, every filter, and the keyset cursor — Platform only forwards identity and the
/// query string.
/// </summary>
public interface IRunDiscoveryService
{
    Task<AgentProxyResponse> ListAsync(string queryString, UserContext ctx, CancellationToken ct = default);
}
