using System.Text.Json;
using System.Net.Http.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>代理 backend /api/business-workflows；所有權威驗證與租戶隔離留在 backend。</summary>
public sealed class BusinessWorkflowService : IBusinessWorkflowService
{
    private const string FailurePrefix = "Business Workflow 服務呼叫失敗：";
    private readonly BackendClient _backend;

    public BusinessWorkflowService(BackendClient backend) => _backend = backend;

    public Task<JsonElement> ListAsync(UserContext context, CancellationToken ct = default)
        => ReadJsonAsync(_backend.BuildRequest(HttpMethod.Get, "/api/business-workflows", context), ct);

    public Task<JsonElement> GetAsync(string name, UserContext context, CancellationToken ct = default)
        => ReadJsonAsync(_backend.BuildRequest(HttpMethod.Get, $"/api/business-workflows/{name}", context), ct);

    public async Task<BusinessWorkflowCreated> CreateAsync(
        SkillUpsert request, UserContext context, CancellationToken ct = default)
    {
        using var message = _backend.BuildRequest(HttpMethod.Post, "/api/business-workflows", context, request);
        using var response = await _backend.SendAsync(message, WrapTransport, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(response, ct);
        }

        Skill workflow;
        try
        {
            workflow = await response.Content.ReadFromJsonAsync<Skill>(_backend.Json, ct)
                ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
        }
        catch (JsonException exception)
        {
            throw WrapTransport(exception);
        }

        EnsureFlowKind(workflow);
        return new BusinessWorkflowCreated(workflow, response.Headers.Location?.ToString());
    }

    public Task<Skill> UpdateAsync(
        string name, SkillUpsert request, UserContext context, CancellationToken ct = default)
        => ReadSkillAsync(
            _backend.BuildRequest(HttpMethod.Put, $"/api/business-workflows/{name}", context, request), ct);

    public Task DeleteAsync(string name, UserContext context, CancellationToken ct = default)
        => _backend.SendExpectSuccessAsync(
            _backend.BuildRequest(HttpMethod.Delete, $"/api/business-workflows/{name}", context),
            WrapTransport,
            MapErrorAsync,
            ct);

    public async Task<SkillExport> ExportAsync(
        string name, UserContext context, CancellationToken ct = default)
    {
        using var request = _backend.BuildRequest(
            HttpMethod.Get, $"/api/business-workflows/{name}/export", context);
        using var response = await _backend.SendAsync(request, WrapTransport, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(response, ct);
        }

        return new SkillExport(
            await response.Content.ReadAsByteArrayAsync(ct),
            response.Content.Headers.ContentType?.MediaType ?? "application/zip",
            $"{name}.zip");
    }

    private Task<JsonElement> ReadJsonAsync(HttpRequestMessage request, CancellationToken ct)
        => _backend.SendForJsonElementAsync(request, WrapTransport, MapErrorAsync, ct);

    private async Task<Skill> ReadSkillAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var workflow = await _backend.SendForJsonAsync<Skill>(
            request,
            WrapTransport,
            MapErrorAsync,
            () => new WorkflowInvocationException(FailurePrefix + "回應內容為空"),
            ct);
        EnsureFlowKind(workflow);
        return workflow;
    }

    private static void EnsureFlowKind(Skill workflow)
    {
        if (workflow.Kind != "flow")
        {
            throw new WorkflowInvocationException(FailurePrefix + "回應包含非 flow 的 kind");
        }
    }

    private Exception WrapTransport(Exception exception)
        => new WorkflowInvocationException(FailurePrefix + exception.Message, exception);

    private Task<Exception> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(response, _backend, FailurePrefix, ct);
}
