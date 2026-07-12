using System.Net.Http.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 系統組態服務:代理 backend /api/config。ADMIN 把關在 backend:PUT 非 ADMIN → backend 403,
/// 本服務映射成 WorkflowForbiddenException(對外 403,比照 Document/Workflow 模式);
/// 其餘失敗 → WorkflowInvocationException(對外 502)。
/// </summary>
public sealed class ConfigService : IConfigService
{
    private const string FailurePrefix = "組態服務呼叫失敗：";

    private readonly BackendClient _backend;

    public ConfigService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public async Task<IReadOnlyList<ConfigItem>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, "/api/config", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await resp.Content.ReadFromJsonAsync<List<ConfigItem>>(_backend.Json, ct)
            ?? new List<ConfigItem>();
    }

    public async Task<ConfigItem> UpdateAsync(string key, ConfigUpdateRequest request, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Put, $"/api/config/{key}", ctx, new { value = request.Value });
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (resp.IsSuccessStatusCode)
        {
            return await resp.Content.ReadFromJsonAsync<ConfigItem>(_backend.Json, ct)
                ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
        }

        if ((int)resp.StatusCode == 403)
        {
            var message = await _backend.ReadErrorMessageAsync(resp, ct);
            throw new WorkflowForbiddenException(
                string.IsNullOrWhiteSpace(message) ? "權限不足，無法修改系統組態" : message);
        }

        throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
    }
}
