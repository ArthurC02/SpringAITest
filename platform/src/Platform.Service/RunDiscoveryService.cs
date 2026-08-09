using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Service;

/// <summary>See <see cref="IRunDiscoveryService"/>.</summary>
public sealed class RunDiscoveryService : IRunDiscoveryService
{
    private const string FailurePrefix = "Run 發現服務失敗：Backend ";

    private readonly BackendClient _backend;

    public RunDiscoveryService(BackendClient backend) => _backend = backend;

    public Task<AgentProxyResponse> ListAsync(string queryString, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForAgentProxyAsync(
            HttpMethod.Get, "/api/runs" + queryString, ctx, null, FailurePrefix, null, ct);
}
