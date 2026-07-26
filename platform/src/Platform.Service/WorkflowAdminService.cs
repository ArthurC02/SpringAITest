using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Service;

public sealed class WorkflowAdminService : IWorkflowAdminService
{
    private const string FailurePrefix = "Workflow Designer backend 呼叫失敗：";

    private readonly BackendClient _backend;

    public WorkflowAdminService(BackendClient backend) => _backend = backend;

    public async Task<AgentProxyResponse> SendAsync(
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

        var request = _backend.BuildRequest(
            method,
            path,
            context,
            body.HasValue ? (object)body.Value : null);
        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        var (status, responseBody, etag) = await _backend.SendForProxyAsync(
            request, FailurePrefix, cancellationToken);
        return new AgentProxyResponse(status, responseBody, etag);
    }
}
