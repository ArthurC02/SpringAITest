using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 系統組態服務:代理 backend /api/config(value 為 camelCase 契約,原樣轉發不改)。
/// backend 的錯誤狀態碼走 <see cref="BackendErrorMapper"/>(與 Skill/ConfigurationSet 一致):
/// PUT 非 ADMIN → backend 403 → WorkflowForbiddenException(對外 403)、其餘 4xx 各自對應同狀態碼、
/// 非預期狀態 → WorkflowInvocationException(對外 502);傳輸層失敗一律 502。
/// </summary>
public sealed class ConfigService : IConfigService
{
    private const string FailurePrefix = "組態服務呼叫失敗：";

    private readonly BackendClient _backend;

    public ConfigService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public async Task<IReadOnlyList<ConfigItem>> ListAsync(UserContext ctx, CancellationToken ct = default)
        => await _backend.SendForJsonListAsync<ConfigItem>(
            _backend.BuildRequest(HttpMethod.Get, "/api/config", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<ConfigItem> UpdateAsync(string key, ConfigUpdateRequest request, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonAsync<ConfigItem>(
            _backend.BuildRequest(HttpMethod.Put, $"/api/config/{key}", ctx, new { value = request.Value }),
            WrapTransport,
            MapErrorAsync,
            () => new WorkflowInvocationException(FailurePrefix + "回應內容為空"),
            ct);

    private Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(resp, _backend, FailurePrefix, ct);
}
