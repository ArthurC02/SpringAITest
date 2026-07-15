using System.Net.Http.Json;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// 工作流服務:代理下游 Python。角色把關在 Python 端,本服務只轉發 X-User-Role 並轉譯狀態碼:
/// 404 → NotFound、403 → Forbidden、422 → BadInput(對外變 400)、其他 → Invocation(對外 502)。
/// </summary>
public sealed class WorkflowService : DownstreamServiceBase, IWorkflowService
{
    private const string FailurePrefix = "工作流服務呼叫失敗：";

    public WorkflowService(HttpClient http, WorkflowOptions options) : base(http, options)
    {
    }

    public async Task<IReadOnlyList<WorkflowInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/workflows", ctx);
        using var resp = await SendAsync(req, FailurePrefix, ct);

        // list:任何失敗(含 4xx/5xx)都當成呼叫失敗。
        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await resp.Content.ReadFromJsonAsync<List<WorkflowInfo>>(JsonOpts, ct)
            ?? new List<WorkflowInfo>();
    }

    public async Task<WorkflowInvokeResponse> InvokeAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"{BaseUrl}/workflows/{name}/invoke", ctx, new { input });
        using var resp = await SendAsync(req, FailurePrefix, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapInvokeErrorAsync(resp, "工作流", name, ct);
        }

        return await resp.Content.ReadFromJsonAsync<WorkflowInvokeResponse>(JsonOpts, ct)
            ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
    }

    /// <summary>執行 skill:錯誤碼映射與 /workflows/{name}/invoke 逐一相同(前端可共用呼叫程式)。</summary>
    public async Task<JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"{BaseUrl}/skills/{name}/invoke", ctx, new { input });
        using var resp = await SendAsync(req, FailurePrefix, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapInvokeErrorAsync(resp, "Skill", name, ct);
        }

        return await ReadJsonAsync(resp, ct);
    }

    /// <summary>驗證 skill 定義:引擎一律回 200(結果在 body);非 200 才是呼叫失敗 → 502。</summary>
    public async Task<JsonElement> ValidateSkillAsync(
        string definition, UserContext ctx, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"{BaseUrl}/skills/validate", ctx, new { definition });
        using var resp = await SendAsync(req, FailurePrefix, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await ReadJsonAsync(resp, ct);
    }

    public Task<JsonElement> GetSkillCatalogAsync(UserContext ctx, CancellationToken ct = default)
        => GetCatalogAsync("/skills", ctx, ct);

    public Task<JsonElement> GetNodeCatalogAsync(UserContext ctx, CancellationToken ct = default)
        => GetCatalogAsync("/nodes", ctx, ct);

    /// <summary>目錄類 GET:任何失敗(含 4xx/5xx)都當成呼叫失敗(同 ListAsync 的既有語義)。</summary>
    private async Task<JsonElement> GetCatalogAsync(string path, UserContext ctx, CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Get, BaseUrl + path, ctx);
        using var resp = await SendAsync(req, FailurePrefix, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await ReadJsonAsync(resp, ct);
    }

    /// <summary>invoke 的狀態碼轉譯:404 → NotFound、403 → Forbidden、422 → BadInput(對外 400)、其他 → 502。</summary>
    private async Task<Exception> MapInvokeErrorAsync(
        HttpResponseMessage resp, string kind, string name, CancellationToken ct) => (int)resp.StatusCode switch
        {
            404 => new WorkflowNotFoundException($"找不到{kind}：{name}"),
            403 => new WorkflowForbiddenException($"權限不足，無法執行{kind}：{name}"),
            // 下游 422 → 本服務 400;訊息帶上下游回應 body 字串。
            422 => new WorkflowBadInputException(
                $"{kind}輸入不符合規範：" + await resp.Content.ReadAsStringAsync(ct)),
            _ => new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode),
        };

    /// <summary>原樣穿透下游 JSON(引擎的欄位由引擎定義,代理層不套 DTO 以免靜默吃掉新欄位)。</summary>
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            // JsonDocument 一旦 Dispose,其 RootElement 即失效 → Clone 出獨立副本再回傳。
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new WorkflowInvocationException(FailurePrefix + "回應不是有效 JSON：" + ex.Message, ex);
        }
    }
}
