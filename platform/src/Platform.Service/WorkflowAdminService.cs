using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

public sealed class WorkflowAdminService : IWorkflowAdminService
{
    private readonly BackendClient _backend;

    public WorkflowAdminService(BackendClient backend) => _backend = backend;

    public async Task<AdminProxyResponse> SendAsync(
        HttpMethod method,
        string resource,
        Guid? id,
        string? suffix,
        UserContext context,
        string? ifMatch = null,
        JsonElement? body = null,
        CancellationToken cancellationToken = default)
    {
        if (resource is not ("workflows" or "orchestrators"))
        {
            throw new ArgumentOutOfRangeException(nameof(resource));
        }

        var path = $"/api/admin/{resource}";
        if (id is not null)
        {
            path += $"/{id.Value:D}";
        }

        if (!string.IsNullOrEmpty(suffix))
        {
            path += "/" + suffix;
        }

        using var request = _backend.BuildRequest(
            method,
            path,
            context,
            body.HasValue ? (object)body.Value : null);
        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await _backend.SendAsync(
            request,
            ex => new WorkflowInvocationException("Workflow Designer backend 無法連線：" + ex.Message, ex),
            cancellationToken);
        var status = (int)response.StatusCode;
        if (status >= 500)
        {
            throw new WorkflowInvocationException($"Workflow Designer backend 回應 HTTP {status}");
        }

        return new AdminProxyResponse(
            status,
            await response.Content.ReadAsStringAsync(cancellationToken),
            response.Headers.ETag?.ToString());
    }
}
