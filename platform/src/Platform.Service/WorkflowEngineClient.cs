using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// Skill 引擎服務:代理下游 Python。角色把關在 Python 端,本服務只轉發內部身分 headers 並轉譯狀態碼:
/// 404 → NotFound、403 → Forbidden、422 → BadInput(對外變 400)、其他 → Invocation(對外 502)。
/// </summary>
public sealed class WorkflowEngineClient : IWorkflowEngineClient
{
    private const string FailurePrefix = "工作流服務呼叫失敗：";

    private readonly HttpClient _http;
    private readonly WorkflowOptions _options;

    public WorkflowEngineClient(HttpClient http, WorkflowOptions options)
    {
        _http = http;
        _options = options;
    }

    private string BaseUrl => _options.BaseUrl.TrimEnd('/');

    /// <summary>組一個帶內部身分 header 的下游請求(委由 <see cref="InternalRequest"/>);可選 JSON body。</summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string url, UserContext ctx, object? body = null)
        => InternalRequest.Build(method, url, _options.InternalToken, ctx, body);

    /// <summary>送出請求;網路錯誤與逾時統一轉成 WorkflowInvocationException(前綴由呼叫端指定)。</summary>
    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string failurePrefix, CancellationToken ct)
        => InternalRequest.SendAsync(
            _http, req, ex => new WorkflowInvocationException(failurePrefix + ex.Message, ex), ct);

    /// <summary>執行 skill:錯誤碼映射見 MapInvokeErrorAsync。</summary>
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
    public Task<JsonElement> ValidateSkillAsync(
        string definition, UserContext ctx, CancellationToken ct = default)
        => ValidateDefinitionAsync("/skills/validate", definition, ctx, ct);

    public Task<JsonElement> GetSkillCatalogAsync(UserContext ctx, CancellationToken ct = default)
        => GetCatalogAsync("/skills", ctx, ct);

    public Task<JsonElement> GetNodeCatalogAsync(UserContext ctx, CancellationToken ct = default)
        => GetCatalogAsync("/nodes", ctx, ct);

    public Task<JsonElement> GetToolCatalogAsync(UserContext ctx, CancellationToken ct = default)
        => GetCatalogAsync("/tools", ctx, ct);

    public Task<JsonElement> GetBusinessRuleFactsAsync(UserContext ctx, CancellationToken ct = default)
        => GetBusinessRuleCatalogPartAsync("facts", ctx, ct);

    public Task<JsonElement> GetBusinessRuleActionsAsync(UserContext ctx, CancellationToken ct = default)
        => GetBusinessRuleCatalogPartAsync("actions", ctx, ct);

    public Task<JsonElement> ValidateBusinessRulesAsync(
        BusinessRuleValidateRequest request, UserContext ctx, CancellationToken ct = default)
        => PostBusinessRulesAsync(
            "/business-rules/validate",
            new { gate = request.Gate, ruleSet = request.RuleSet },
            ctx,
            ct);

    public Task<JsonElement> SimulateBusinessRulesAsync(
        BusinessRuleSimulateRequest request, UserContext ctx, CancellationToken ct = default)
        => PostBusinessRulesAsync(
            "/business-rules/simulate",
            new { gate = request.Gate, ruleSet = request.RuleSet, facts = request.Facts },
            ctx,
            ct);

    private async Task<JsonElement> GetBusinessRuleCatalogPartAsync(
        string property, UserContext ctx, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync("/business-rules/catalog", ctx, ct);
        if (catalog.ValueKind != JsonValueKind.Object
            || !catalog.TryGetProperty(property, out var part)
            || part.ValueKind != JsonValueKind.Array)
        {
            throw new WorkflowInvocationException(
                FailurePrefix + $"Business Rule catalog 缺少 {property} 陣列");
        }

        // Public API has separate facts/actions routes, while Workflow owns one versioned catalog envelope.
        // Remove only the opposite collection and preserve version/gates/limits/operators plus additive future
        // metadata so the UI never needs to hardcode engine capabilities.
        var opposite = property == "facts" ? "actions" : "facts";
        var split = new JsonObject();
        foreach (var item in catalog.EnumerateObject())
        {
            if (!string.Equals(item.Name, opposite, StringComparison.Ordinal))
            {
                split[item.Name] = JsonNode.Parse(item.Value.GetRawText());
            }
        }

        using var document = JsonDocument.Parse(split.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Rule validate/simulate 的正常結果(包含 valid=false)原樣穿透。Pydantic request 422 映射成乾淨
    /// 的對外 400；其他 HTTP/傳輸/壞 JSON 錯誤收斂成 502，不洩漏 Workflow body。
    /// </summary>
    private async Task<JsonElement> PostBusinessRulesAsync(
        string path, object body, UserContext ctx, CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Post, BaseUrl + path, ctx, body);
        using var resp = await SendAsync(req, FailurePrefix, ct);
        if ((int)resp.StatusCode == 413)
        {
            throw new WorkflowPayloadTooLargeException(
                "Business Rule request exceeds the allowed size or nesting depth");
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            throw await MapBadInputAsync("Business Rule", resp, ct);
        }
        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await ReadJsonAsync(resp, ct);
    }

    public Task<JsonElement> ValidateBusinessWorkflowAsync(
        string definition, UserContext ctx, CancellationToken ct = default)
        => ValidateDefinitionAsync("/business-workflows/validate", definition, ctx, ct);

    /// <summary>
    /// Flow validation routes share the same transport contract: a 200 body contains both
    /// valid and invalid validation results; any non-2xx response is an invocation failure.
    /// </summary>
    private async Task<JsonElement> ValidateDefinitionAsync(
        string path, string definition, UserContext ctx, CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Post, BaseUrl + path, ctx, new { definition });
        using var resp = await SendAsync(req, FailurePrefix, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await ReadJsonAsync(resp, ct);
    }

    /// <summary>目錄類 GET:任何失敗(含 4xx/5xx)都當成呼叫失敗。</summary>
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

    /// <summary>invoke 的狀態碼轉譯:404 → NotFound、403 → Forbidden、413 → PayloadTooLarge、
    /// 422 → BadInput(對外 400)、其他 → 502。</summary>
    private async Task<Exception> MapInvokeErrorAsync(
        HttpResponseMessage resp, string kind, string name, CancellationToken ct) => (int)resp.StatusCode switch
        {
            404 => new WorkflowNotFoundException($"找不到{kind}：{name}"),
            403 => new WorkflowForbiddenException($"權限不足，無法執行{kind}：{name}"),
            // Body is intentionally ignored: Workflow detail is not part of the public contract.
            413 => new WorkflowPayloadTooLargeException("Skill request exceeds the allowed size"),
            // 下游 422 → 本服務 400;解析 detail 帶出乾淨訊息與 fieldErrors(見 MapBadInputAsync)。
            422 => await MapBadInputAsync(kind, resp, ct),
            _ => new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode),
        };

    /// <summary>
    /// 下游 422 body 契約:<c>{ detail: { error, message, field_errors } }</c>。
    /// 解析 <c>detail.message</c> 與 <c>detail.field_errors</c>(snake_case)填進對外 ApiError
    /// (<c>fieldErrors</c> camelCase 容器,鍵沿用引擎給的欄位名)。body 非預期形狀(舊版 workflow、
    /// 非 JSON、缺 field_errors)→ 回落到 detail.message,取不到再用固定文案;
    /// 絕不把原始 body 字串拼進 message(那正是要修掉的行為)。
    /// </summary>
    private static async Task<Exception> MapBadInputAsync(
        string kind, HttpResponseMessage resp, CancellationToken ct)
    {
        var message = $"{kind} 輸入不符合規範";
        Dictionary<string, string>? fieldErrors = null;

        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.Object)
            {
                if (detail.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(msg.GetString()))
                {
                    message = msg.GetString()!;
                }

                if (detail.TryGetProperty("field_errors", out var fe)
                    && fe.ValueKind == JsonValueKind.Object)
                {
                    fieldErrors = new Dictionary<string, string>();
                    foreach (var prop in fe.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            fieldErrors[prop.Name] = prop.Value.GetString()!;
                        }
                    }
                    if (fieldErrors.Count == 0)
                    {
                        fieldErrors = null;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON / 壞 body → 維持固定文案回落,不外洩原始 body。
        }

        return new WorkflowBadInputException(message) { FieldErrors = fieldErrors };
    }

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
