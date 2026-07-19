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
        => await _backend.SendForJsonListAsync<ConfigItem>(
            _backend.BuildRequest(HttpMethod.Get, "/api/config", ctx),
            WrapTransport,
            (r, _) => Task.FromResult<Exception>(new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)r.StatusCode)),
            ct);

    public Task<ConfigItem> UpdateAsync(string key, ConfigUpdateRequest request, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonAsync<ConfigItem>(
            _backend.BuildRequest(HttpMethod.Put, $"/api/config/{key}", ctx, new { value = request.Value }),
            WrapTransport,
            async (r, c) =>
            {
                // PUT 非 ADMIN → backend 403 → 對外 403(比照 Document/Workflow 模式);其餘 → 502。
                if ((int)r.StatusCode == 403)
                {
                    var message = await _backend.ReadErrorMessageAsync(r, c);
                    return new WorkflowForbiddenException(
                        string.IsNullOrWhiteSpace(message) ? "權限不足，無法修改系統組態" : message);
                }

                return new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)r.StatusCode);
            },
            () => new WorkflowInvocationException(FailurePrefix + "回應內容為空"),
            ct);
}
