using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// Transparent D4 management proxy. Authorization and rollout gating live at the Web boundary;
/// Backend remains authoritative for tenant isolation, draft concurrency and immutable revisions.
/// </summary>
public interface IWorkflowAdminService
{
    Task<AdminProxyResponse> SendAsync(
        HttpMethod method,
        string resource,
        Guid? id,
        string? suffix,
        UserContext context,
        string? ifMatch = null,
        JsonElement? body = null,
        CancellationToken cancellationToken = default);
}

public sealed record AdminProxyResponse(int Status, string Body, string? ETag);
