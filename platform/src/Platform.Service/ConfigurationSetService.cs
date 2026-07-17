using System.Net.Http.Json;
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
/// </summary>
public sealed class ConfigurationSetService : IConfigurationSetService
{
    private const string FailurePrefix = "Configuration Set 服務呼叫失敗：";
    private const string BasePath = "/api/configuration-sets";

    private readonly BackendClient _backend;

    public ConfigurationSetService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public async Task<IReadOnlyList<ConfigurationSetInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, BasePath, ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<List<ConfigurationSetInfo>>(_backend.Json, ct)
            ?? new List<ConfigurationSetInfo>();
    }

    public async Task<ConfigurationSet> GetAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, $"{BasePath}/{id}", ctx);
        return await ReadSetAsync(req, ct);
    }

    public async Task<ConfigurationSet> CreateAsync(
        ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Post, BasePath, ctx, request);
        return await ReadSetAsync(req, ct);
    }

    public async Task<ConfigurationSet> UpdateAsync(
        string id, ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Put, $"{BasePath}/{id}", ctx, request);
        return await ReadSetAsync(req, ct);
    }

    public async Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Delete, $"{BasePath}/{id}", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }
    }

    public async Task<ConfigurationSet> ActivateAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Post, $"{BasePath}/{id}/activate", ctx);
        return await ReadSetAsync(req, ct);
    }

    private async Task<ConfigurationSet> ReadSetAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<ConfigurationSet>(_backend.Json, ct)
            ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
    }

    private Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(resp, _backend, FailurePrefix, ct);
}
