using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// Transparent D4 management proxy. Authorization and rollout gating live at the Web boundary;
/// Backend remains authoritative for tenant isolation, draft concurrency and immutable revisions.
/// </summary>
public interface IWorkflowAdminService
{
    /// <summary>回傳與 Agent Registry 代理同一形狀的 <see cref="AgentProxyResponse"/>(status + 原始 body + ETag)。</summary>
    Task<AgentProxyResponse> SendAsync(
        HttpMethod method,
        string resource,
        Guid? id,
        string? suffix,
        UserContext context,
        string? ifMatch = null,
        JsonElement? body = null,
        CancellationToken cancellationToken = default);
}
