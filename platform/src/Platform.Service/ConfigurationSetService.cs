using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// Configuration Set CRUD 服務:代理 backend /api/configuration-sets,不含任何商業邏輯
/// (角色/唯一性/values 型別範圍驗證全在 backend)。比照 SkillService:
/// backend 的錯誤原樣轉發(400/403/404/409/422 各自映射到對外同狀態碼的例外並沿用 backend 的 message);
/// 其餘(5xx、傳輸失敗)→ WorkflowInvocationException(對外 502)。
/// 400 與 422 的 fieldErrors 都要帶上來,否則 values 越界的欄位級原因會在代理層被吞成空 map。
/// 讀取端點(list/get)原樣穿透 backend JSON(不套 DTO),避免 backend 新增欄位被靜默吃掉。
/// </summary>
public sealed class ConfigurationSetService : IConfigurationSetService
{
    private const string FailurePrefix = "Configuration Set 服務呼叫失敗：";
    private const string BasePath = "/api/configuration-sets";

    private readonly BackendClient _backend;

    public ConfigurationSetService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(HttpMethod.Get, BasePath, ctx), WrapTransport, MapErrorAsync, ct);

    public Task<JsonElement> GetAsync(string id, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(HttpMethod.Get, $"{BasePath}/{id}", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<ConfigurationSet> CreateAsync(
        ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default)
        => ReadSetAsync(_backend.BuildRequest(HttpMethod.Post, BasePath, ctx, request), ct);

    public Task<ConfigurationSet> UpdateAsync(
        string id, ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default)
        => ReadSetAsync(_backend.BuildRequest(HttpMethod.Put, $"{BasePath}/{id}", ctx, request), ct);

    public Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default)
        => _backend.SendExpectSuccessAsync(
            _backend.BuildRequest(HttpMethod.Delete, $"{BasePath}/{id}", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<ConfigurationSet> ActivateAsync(string id, UserContext ctx, CancellationToken ct = default)
        => ReadSetAsync(_backend.BuildRequest(HttpMethod.Post, $"{BasePath}/{id}/activate", ctx), ct);

    private Task<ConfigurationSet> ReadSetAsync(HttpRequestMessage req, CancellationToken ct)
        => _backend.SendForJsonAsync<ConfigurationSet>(
            req, WrapTransport, MapErrorAsync,
            () => new WorkflowInvocationException(FailurePrefix + "回應內容為空"), ct);

    private Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(resp, _backend, FailurePrefix, ct);
}
