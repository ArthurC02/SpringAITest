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

    /// <summary>
    /// backend 錯誤 → 對外同狀態碼的例外,message 沿用 backend(platform 不改寫);
    /// 400 與 422 另外把 backend 的 fieldErrors 帶上,否則欄位級/values 越界原因會在代理層被吞成空 map。
    /// 422 復用 SkillValidationFailedException(全域處理裡它是唯一映射到 422 的載體;語意上就是「下游 422」)。
    /// </summary>
    private async Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        if (status is not (400 or 403 or 404 or 409 or 422))
        {
            return new WorkflowInvocationException(FailurePrefix + "HTTP " + status);
        }

        var error = await _backend.ReadErrorAsync(resp, ct);
        var message = error.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = FailurePrefix + "HTTP " + status;
        }

        return status switch
        {
            400 => new WorkflowBadInputException(message) { FieldErrors = error.FieldErrors },
            403 => new WorkflowForbiddenException(message),
            404 => new WorkflowNotFoundException(message),
            409 => new DownstreamConflictException(message),
            _ => new SkillValidationFailedException(message) { FieldErrors = error.FieldErrors },
        };
    }
}
