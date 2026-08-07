using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Web.Tests;

/// <summary>測試 fake 共用:把原始 JSON 字串解析成獨立的 JsonElement(比照 backend 讀取端點的穿透回應)。</summary>
internal static class FakeJson
{
    public static JsonElement Of(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}

/// <summary>
/// fake 的呼叫紀錄必須是 static(fake 本身註冊為 Scoped,每個請求都是新實例,實例欄位活不過一個請求),
/// 但 static 清單從不清空會讓「模糊 Contains」被前一個測試的殘留誤判成綠。此類別是每個
/// <see cref="TestWebAppFactory"/> 一份的重置權杖:某個 factory 第一次碰到某份清單時清空它,
/// 之後同一 factory 內的多次請求照常累加。建 factory 是 per-test 的類別因此就得到 per-test 隔離。
/// </summary>
public sealed class FakeCallScope
{
    private readonly HashSet<object> _owned = new();
    private readonly Lock _gate = new();

    public void Own(params System.Collections.IList[] collections)
    {
        lock (_gate)
        {
            foreach (var collection in collections)
            {
                if (_owned.Add(collection))
                {
                    collection.Clear();
                }
            }
        }
    }
}

/// <summary>
/// Web 整合測試用的 LLM 代理 fake:一律回固定字串 → 路由回覆不匹配任何工具 → 走純聊天兜底(reply 仍是「測試回覆」)。
/// 新流程下工具改以「路由目錄」文字經路由呼叫傳入(非原生 tools 引數);測試改斷言 LastRoutingCatalog。
/// 單例跨同一測試類的方法共用,測試以 Reset() 隔離每次請求。
/// </summary>
public sealed class FakeLlmAgent : ILlmAgent
{
    /// <summary>最近一次「路由呼叫」的 system 目錄內容(以路由指令開頭者);未路由的請求維持 null。</summary>
    public string? LastRoutingCatalog { get; private set; }

    /// <summary>本輪 CompleteAsync 的次數(匿名純聊天=1;路由+兜底/摘要=2)。</summary>
    public int CompleteCallCount { get; private set; }

    /// <summary>
    /// P4:CompleteAsync 的回覆(路由呼叫回這個值)。預設維持既有行為(不匹配任何 skill 名 → 路由 NONE);
    /// 需要驗證「路由命中」的測試(T-P4-2/T-P4-3/B-P4-12/13)可設成目標 skill 名。用畢應還原預設值,
    /// 避免污染共用 Singleton fake 實例的後續測試(此 fake 跨同一測試類的方法共用)。
    /// </summary>
    public string Response { get; set; } = "測試回覆";

    public void Reset()
    {
        LastRoutingCatalog = null;
        CompleteCallCount = 0;
        Response = "測試回覆";
    }

    public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
    {
        CompleteCallCount++;
        if (messages.Count > 0 && messages[0].Role == "system"
            && messages[0].Content.StartsWith("你是一個路由器", StringComparison.Ordinal))
        {
            LastRoutingCatalog = messages[0].Content;
        }

        return Task.FromResult(Response);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var last = messages.Count > 0 ? messages[^1].Content : "";

        // 訊息為「多行」時,吐一塊含換行的 chunk,驗 SSE 把單一 chunk 拆成多個 data: 行。
        if (last == "多行")
        {
            yield return "甲\n乙";
            yield break;
        }

        // 訊息為「串流爆炸」時,先吐一個 token 再中途擲例外,驗 ChatController 補寫 event:error 終止 frame。
        if (last == "串流爆炸")
        {
            yield return "半截";
            throw new InvalidOperationException("串流中途失敗");
        }

        // 路由命中後的摘要輪(HIT 路徑走「裸」ILlmAgent):把工具結果(已由 answer 等 OutputKeys 萃取)原樣吐回,
        // 讓 Web SSE 測試觀察到「最終內容確實由 answer 鍵萃取而來」(AST-P1-012)。純聊天(MISS)不經此路徑。
        const string marker = "工具結果：\n";
        var markerAt = last.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt >= 0)
        {
            yield return last[(markerAt + marker.Length)..];
            yield break;
        }

        yield return "你好";
        yield return "世界";
    }
}

/// <summary>
/// mem0 fake(記憶最佳努力,不影響聊天):RecallAsync 一律回空字串(無前言注入);
/// RememberAsync 記錄呼叫供 A-16 等測試斷言「串流持久化失敗仍照常 remember」。Remembered 是靜態的,
/// 因為 IMem0Client 在 DI 是 singleton(整個 factory 生命週期共用同一顆實例),用靜態欄位單純是明確表態。
/// </summary>
public sealed class FakeMem0Client : IMem0Client
{
    public static readonly List<(string UserId, string UserMessage, string AiReply)> Remembered = new();

    public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
    {
        Remembered.Add((userId, userMessage, aiReply));
        return Task.CompletedTask;
    }
}

/// <summary>
/// 聊天歷史 store fake(代表 backend /api/conversations,以 (tenant_id, user_id) 隔離)。
/// AddAsync 回遞增 id 並依 ctx 的租戶/使用者(連同 D6 lineage metadata)記進 Saved;ListDescAsync 依 ctx
/// 過濾只回同租戶同使用者的紀錄(鏡射真正 ConversationStore 的隔離語意,供 A-21 的租戶隔離斷言使用)。
/// ThrowOnAdd 讓 A-15/A-16 腳本化持久化失敗(阻塞 500 vs 串流 best-effort 的決策表兩半)。
/// 靜態:controller 端以 AddScoped 註冊,每次請求都是新實例,狀態要跨請求可見必須是靜態。
/// </summary>
public sealed class FakeConversationStore : IConversationStore
{
    private static long _nextId = 1;

    /// <summary>測試腳本開關:true 時 AddAsync 擲例外,模擬持久化層失敗。用畢務必在 finally 還原為 false。</summary>
    public static bool ThrowOnAdd { get; set; }

    public static readonly List<(string TenantCode, string UserId, ChatResponse Response, ChatTurnMetadata? Metadata)> Saved = new();

    public Task<ChatResponse> AddAsync(
        string prompt, string reply, UserContext ctx, ChatTurnMetadata? metadata = null,
        CancellationToken ct = default)
    {
        if (ThrowOnAdd)
        {
            throw new InvalidOperationException("持久化失敗（測試腳本）");
        }

        var response = new ChatResponse(_nextId++, reply, DateTime.UtcNow);
        Saved.Add((ctx.TenantCode, ctx.UserId, response, metadata));
        return Task.FromResult(response);
    }

    public Task<IReadOnlyList<ChatResponse>> ListDescAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatResponse>>(Saved
            .Where(s => s.TenantCode == ctx.TenantCode && s.UserId == ctx.UserId)
            .Select(s => s.Response)
            .Reverse()
            .ToList());

    public Task<ChatHistoryPage> ListPageAsync(
        int limit, string? before, UserContext ctx, CancellationToken ct = default)
    {
        var ordered = Saved
            .Where(s => s.TenantCode == ctx.TenantCode && s.UserId == ctx.UserId)
            .Select(s => s.Response)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .ToList();
        if (before == "bad")
        {
            throw new WorkflowBadInputException("聊天歷史游標無效");
        }

        var offset = before is null ? 0 : int.Parse(before, System.Globalization.CultureInfo.InvariantCulture);
        var items = ordered.Skip(offset).Take(limit).ToList();
        var next = offset + items.Count;
        var hasMore = next < ordered.Count;
        return Task.FromResult(new ChatHistoryPage(
            items,
            hasMore ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            hasMore));
    }
}

/// <summary>
/// 認證服務 fake:重現 backend 的行為與訊息(依輸入判斷),讓 controller/驗證/例外映射契約測試維持。
/// login 用 TestTokens 簽出真正可通過 Bearer 中介軟體的 token。
/// </summary>
public sealed class FakeAuthService : IAuthService
{
    public Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var tenant = request.TenantCode!;
        if (tenant != "demo-a" && tenant != "demo-b")
        {
            throw new TenantNotFoundException("找不到租戶：" + tenant);
        }

        if (request.InviteCode != tenant + "-invite")
        {
            throw new InvalidInviteCodeException("邀請碼無效");
        }

        if (request.Username is "user-a" or "user-b" or "admin-a")
        {
            throw new UsernameTakenException("使用者名稱已存在：" + request.Username);
        }

        return Task.FromResult(new AuthResult(request.Username!, "USER", tenant));
    }

    public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        if (request.Password != "password123")
        {
            throw new InvalidCredentialsException("帳號或密碼錯誤");
        }

        var role = request.Username == "admin-a" ? "ADMIN" : "USER";
        var token = TestTokens.Mint(request.Username!, role, "demo-a");
        return Task.FromResult(new LoginResult(token, request.Username!, role, "demo-a"));
    }
}

/// <summary>Skill 引擎服務 fake:依名稱決定行為(ghost → NotFound),用來測 controller/認證/序列化/例外映射。</summary>
public sealed class FakeWorkflowEngineClient : IWorkflowEngineClient
{
    // ---- Skill 引擎(:8001)----

    /// <summary>記錄引擎端的呼叫,用來斷言「catalog 走引擎、不走 backend CRUD」。</summary>
    public static readonly List<string> EngineCalls = new();
    public static UserContext? LastRuleContext { get; private set; }

    /// <summary>
    /// 補 G5(copilot-shared-core 04-acceptance-test.md §4.4):IWorkflowEngineClient 在 DI 是 Scoped,
    /// SkillRoutingAgent 每次呼叫各自開一個新 scope,跨請求(HTTP request)拿到的是不同實例,無法用實例
    /// 欄位收集 (Name, Input) 供 B-P4-12(ChatView 與副駕的 SkillInvokes 應完全相同)這類跨請求斷言。
    /// 靜態集合擇簡繞過:不必改動 DI 生命週期,天然跨 scope/跨請求可見(與 EngineCalls/Calls 等既有靜態
    /// 收集器同一慣例)。測試須自行在案例開頭/結尾清空,避免跨測試污染。
    /// </summary>
    public static readonly List<(string Name, Dictionary<string, JsonElement> Input)> SkillInvokes = new();
    public static readonly List<ArtifactUsageOrigin> SkillInvokeOrigins = new();

    public Task<JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx,
        ArtifactUsageOrigin origin, CancellationToken ct = default)
    {
        EngineCalls.Add("invoke:" + name);
        SkillInvokes.Add((name, input));
        SkillInvokeOrigins.Add(origin);

        // skill invoke 的錯誤碼與 /workflows/{name}/invoke 逐一相同(同一組觸發名稱)。
        switch (name)
        {
            case "ghost":
                throw new WorkflowNotFoundException("找不到 Skill：" + name);
            case "forbidden":
                throw new WorkflowForbiddenException("權限不足，無法執行 Skill：" + name);
            case "badinput":
                throw new WorkflowBadInputException("Skill 輸入不符合規範：缺少 query");
            case "toolarge":
                throw new WorkflowPayloadTooLargeException("Skill request exceeds the allowed size");
            case "boom":
                throw new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 500");
        }

        return Task.FromResult(Json("{\"skill\":\"" + name + "\",\"output\":{\"answer\":\"42\"}}"));
    }

    public Task<JsonElement> ValidateSkillAsync(
        string definition, UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("validate");

        // 引擎一律回 200,結果在 body(不合法不是 HTTP 錯誤)。
        return Task.FromResult(definition.Contains("__invalid__", StringComparison.Ordinal)
            ? Json("""{"valid":false,"errors":[{"code":"unbounded_loop","message":"loop 缺少 max_iterations","line":7}]}""")
            : Json("""{"valid":true,"errors":[],"skill":{"name":"quarterly-qa","description":"季報問答","required_role":"USER","kind":"flow"}}"""));
    }

    public Task<JsonElement> ValidateBusinessWorkflowAsync(
        string definition, UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("business-workflow-validate");
        return ValidateSkillAsync(definition, ctx, ct);
    }

    /// <summary>
    /// 非 null 時 GetSkillCatalogAsync 回這包目錄(供聊天 → Skill 路由的 Web 整合測試注入可路由目錄)。
    /// 設計成與 Catalog_Returns200 的斷言結構相容(2 筆、source 依序 builtin/custom),即使並行讀取也安全。
    /// 用後務必在 finally 還原為 null。
    /// </summary>
    public static JsonElement? CatalogOverride { get; set; }

    public Task<JsonElement> GetSkillCatalogAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("catalog");
        if (CatalogOverride is JsonElement over)
        {
            return Task.FromResult(over);
        }

        return Task.FromResult(Json(
            """[{"name":"kb-query","description":"知識查詢","required_role":"USER","source":"builtin","revision":null,"bindable":false},"""
            + """{"name":"quarterly-qa","description":"季報問答","required_role":"USER","source":"custom","revision":3,"bindable":true}]"""));
    }

    public Task<JsonElement> GetNodeCatalogAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("nodes");
        return Task.FromResult(Json(
            """[{"name":"query_intake","version":"1.0","description":"輸入正規化","reads":[],"writes":["original_query"],"requires_tools":[]}]"""));
    }

    public Task<JsonElement> GetToolCatalogAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("tools");
        return Task.FromResult(Json(
            """[{"name":"backend.retrieval_search","kind":"http","description":"在目前租戶已授權的知識庫中進行向量檢索","risk":"read","returns":"list[chunk]"}]"""));
    }

    public Task<JsonElement> GetBusinessRuleFactsAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("rule-facts");
        LastRuleContext = ctx;
        return Task.FromResult(Json(
            """{"version":1,"gates":["pre-action"],"limits":{"maxDepth":8},"operators":[{"name":"gt"}],"facts":[{"name":"action.amount","type":"decimal","provenance":"system","trustTier":"trusted","gates":["pre-action"]}]}"""));
    }

    public Task<JsonElement> GetBusinessRuleActionsAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("rule-actions");
        LastRuleContext = ctx;
        return Task.FromResult(Json(
            """{"version":1,"gates":["pre-action"],"limits":{"maxDepth":8},"actions":[{"name":"deny","precedence":100},{"name":"require_approval","precedence":90}]}"""));
    }

    public Task<JsonElement> ValidateBusinessRulesAsync(
        BusinessRuleValidateRequest request, UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("rule-validate:" + request.Gate);
        LastRuleContext = ctx;
        return Task.FromResult(Json(
            """{"valid":true,"canonicalRuleSet":{"version":1,"rules":[]},"errors":[]}"""));
    }

    public Task<JsonElement> SimulateBusinessRulesAsync(
        BusinessRuleSimulateRequest request, UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("rule-simulate:" + request.Gate);
        LastRuleContext = ctx;
        return Task.FromResult(Json(
            """{"valid":true,"canonicalRuleSet":{"version":1,"rules":[]},"errors":[],"simulation":{"decision":"allow","matchedRules":[],"trace":[]}}"""));
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}

public sealed class FakeBusinessWorkflowService : IBusinessWorkflowService
{
    public static readonly List<string> Calls = new();

    public Task<JsonElement> ListAsync(UserContext context, CancellationToken ct = default)
    {
        Calls.Add("list");
        return Task.FromResult(JsonDocument.Parse(
            """[{"name":"quarterly-flow","description":"flow","required_role":"USER","enabled":true,"current_revision":2,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","kind":"flow"}]""")
            .RootElement.Clone());
    }

    public Task<JsonElement> GetAsync(string name, UserContext context, CancellationToken ct = default)
    {
        Calls.Add("get:" + name);
        return Task.FromResult(JsonSerializer.SerializeToElement(
            new { name, definition = "name: " + name, kind = "flow" }));
    }

    public Task<BusinessWorkflowCreated> CreateAsync(
        SkillUpsert request, UserContext context, CancellationToken ct = default)
    {
        Calls.Add("create");
        RequireAdmin(context);
        var workflow = new Skill(
            "quarterly-flow", "flow", request.Definition!, "USER", true, 1,
            "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "flow",
            JsonSerializer.SerializeToElement(new { templateId = "template-stats" }));
        return Task.FromResult(new BusinessWorkflowCreated(
            workflow, "/api/business-workflows/quarterly-flow"));
    }

    public Task<Skill> UpdateAsync(
        string name, SkillUpsert request, UserContext context, CancellationToken ct = default)
    {
        Calls.Add("update:" + name);
        RequireAdmin(context);
        return Task.FromResult(new Skill(
            name, "flow", request.Definition!, "USER", true, 2,
            "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "flow",
            request.SimpleForm));
    }

    public Task DeleteAsync(string name, UserContext context, CancellationToken ct = default)
    {
        Calls.Add("delete:" + name);
        RequireAdmin(context);
        return Task.CompletedTask;
    }

    public Task<SkillExport> ExportAsync(string name, UserContext context, CancellationToken ct = default)
    {
        Calls.Add("export:" + name);
        return Task.FromResult(new SkillExport(new byte[] { 80, 75 }, "application/zip", $"{name}.zip"));
    }

    private static void RequireAdmin(UserContext context)
    {
        if (context.Role != "ADMIN")
        {
            throw new WorkflowForbiddenException("權限不足，無法存取 Business Workflow");
        }
    }
}

/// <summary>文件服務 fake:ghost → NotFound。建立回受理狀態(202 語意)。</summary>
public sealed class FakeDocumentService : IDocumentService
{
    public static string? LastIdempotencyKey { get; private set; }

    public Task<DocumentAccepted> CreateAsync(
        DocumentCreateRequest request,
        UserContext ctx,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        LastIdempotencyKey = idempotencyKey;
        return Task.FromResult(new DocumentAccepted("doc-1", request.Title ?? string.Empty, "processing"));
    }

    public Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult(FakeJson.Of(
            """[{"id":"doc-1","title":"標題","chunk_count":3,"created_at":"2026-07-11T00:00:00Z","status":"ready"}]"""));

    public Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        if (id == "ghost")
        {
            throw new DocumentNotFoundException("找不到文件：" + id);
        }

        return Task.CompletedTask;
    }
}

/// <summary>分析服務 fake:回固定摘要。</summary>
public sealed class FakeAnalysisService : IAnalysisService
{
    public Task<AnalysisSummary> SummaryAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult(new AnalysisSummary(2, 7, new[] { "甲", "乙" }));
}

/// <summary>
/// Skill CRUD 服務 fake(代表 backend :8002)。重現 backend 的錯誤回應:
/// definition 內含 dup-skill → 409、名稱 ghost → 404、定義含 __invalid__ → 422(引擎錯誤碼)。
/// Calls 是靜態的:讓「401 時請求不得抵達 backend」與「catalog 不得走到 CRUD」可被斷言。
/// </summary>
public sealed class FakeSkillService : ISkillService
{
    public static readonly List<string> Calls = new();

    /// <summary>backend 422 的 fieldErrors:引擎錯誤碼清單,必須原樣穿過 platform。</summary>
    public static readonly Dictionary<string, string> EngineErrors =
        new() { ["unbounded_loop"] = "loop 缺少 max_iterations（第 7 行）" };

    /// <summary>backend 400 的欄位級錯誤(規則漂移時才會發生):不得被代理層吞掉。</summary>
    public static readonly Dictionary<string, string> BackendFieldErrors =
        new() { ["definition"] = "definition 不可為空" };

    private static Skill Make(string name, string definition) =>
        new(name, "季報問答", definition, "USER", true, 1,
            "2026-07-14T00:00:00Z", "2026-07-14T00:00:00Z", "flow");

    /// <summary>fake 版的「引擎解析 YAML」:取 `name:` 那行的值(真實流程由 backend 打引擎取得)。</summary>
    private static string NameOf(string definition) => definition
        .Split('\n').Select(l => l.Trim())
        .Where(l => l.StartsWith("name:", StringComparison.Ordinal))
        .Select(l => l["name:".Length..].Trim())
        .FirstOrDefault() ?? "unnamed";

    /// <summary>definition 未通過引擎驗證時,backend 回 422 + 引擎錯誤碼。</summary>
    private static void ThrowIfInvalid(string definition)
    {
        if (definition.Contains("__invalid__", StringComparison.Ordinal))
        {
            throw new SkillValidationFailedException("Skill 定義驗證失敗") { FieldErrors = EngineErrors };
        }
    }

    public Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("list");
        return Task.FromResult(FakeJson.Of(
            """[{"name":"echo-flow","description":"季報問答","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-07-14T00:00:00Z","updated_at":"2026-07-14T00:00:00Z","kind":"flow"},{"name":"echo-agent","description":"代理技能","required_role":"USER","enabled":true,"current_revision":2,"created_at":"2026-07-14T00:00:00Z","updated_at":"2026-07-15T00:00:00Z","kind":"agentic"}]"""));
    }

    public Task<JsonElement> GetAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("get:" + name);
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        // 穿透:直接把 backend 形狀(含 JsonPropertyName snake_case)序列化成 JsonElement 回傳。
        return Task.FromResult(JsonSerializer.SerializeToElement(
            Make(name, $"name: {name}\nflow:\n  - node: query_intake\n")));
    }

    public Task<JsonElement> GetRevisionsAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("revisions:" + name);
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        return Task.FromResult(FakeJson.Of(
            """[{"revision":2,"definition":"# r2","definition_sha256":"sha2","created_by":"admin-a","created_at":"2026-07-14T00:00:00Z"},{"revision":1,"definition":"# r1","definition_sha256":"sha1","created_by":"admin-a","created_at":"2026-07-13T00:00:00Z"}]"""));
    }

    public Task<JsonElement> RestoreRevisionAsync(
        string name, int revision, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add($"restore:{name}:{revision}");
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        return Task.FromResult(FakeJson.Of(
            $$"""{"name":"{{name}}","description":"季報問答","definition":"# restored","required_role":"USER","enabled":true,"current_revision":3,"kind":"flow","created_at":"2026-07-14T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}"""));
    }

    /// <summary>匯出用的假 zip bytes(內容不必是真 zip — 這層只驗代理原封轉發)。</summary>
    public static readonly byte[] ExportBytes = Encoding.UTF8.GetBytes("PK-fake-zip-bytes");

    public Task<SkillExport> ExportAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("export:" + name);
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        return Task.FromResult(new SkillExport(ExportBytes, "application/zip", $"{name}.zip"));
    }

    /// <summary>
    /// Agent Skill 匯入的 fake(代表 backend):重現 backend [AdminOnly](非 ADMIN → 403,早於套件驗證)、
    /// 套件驗證失敗(name=badpkg → 422 帶 forbidden_script)、成功回傳含 additive kind 的 agentic Skill JSON。
    /// 收到的是已由 Web 層 IFormFile 讀出的位元組(內容不檢查)——這層驗代理與錯誤映射,不驗解壓。
    /// </summary>
    public Task<JsonElement> ImportAsync(
        string name, byte[] package, string fileName, UserContext ctx, CancellationToken ct = default)
        => ImportCore(name, package, ctx);

    public Task<JsonElement> ImportAsync(
        byte[] package, string fileName, UserContext ctx, CancellationToken ct = default)
        => ImportCore("server-derived", package, ctx);

    private static Task<JsonElement> ImportCore(
        string name, byte[] package, UserContext ctx)
    {
        Calls.Add("import:" + name);

        // backend [AdminOnly]:非 ADMIN 一律 403(早於 package 驗證)。
        if (ctx.Role != "ADMIN")
        {
            throw new WorkflowForbiddenException("權限不足，無法存取 Skill");
        }

        if (name == "badpkg")
        {
            throw new SkillValidationFailedException("Skill 套件驗證失敗")
            {
                FieldErrors = new Dictionary<string, string> { ["forbidden_script"] = "腳本未通過 AST 掃描" },
            };
        }

        // 成功:含 additive kind 的 agentic Skill JSON(原樣穿透,舊 consumer 忽略未知 kind 仍可運作)。
        return Task.FromResult(FakeJson.Of(
            $$"""{"name":"{{name}}","description":"匯入的代理技能","definition":"kind: agentic\n...","required_role":"USER","enabled":true,"current_revision":1,"kind":"agentic","created_at":"2026-07-14T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}"""));
    }

    public Task<Skill> CreateAsync(SkillUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        var name = NameOf(request.Definition!);
        Calls.Add("create:" + name);
        ThrowIfInvalid(request.Definition!);

        if (name == "dup-skill")
        {
            // 重現 backend 的 409(同名衝突):訊息原樣往上拋,platform 不改寫。
            throw new DownstreamConflictException("Skill 名稱已存在：" + name);
        }

        if (name == "bad-field")
        {
            throw new WorkflowBadInputException("輸入驗證失敗") { FieldErrors = BackendFieldErrors };
        }

        return Task.FromResult(Make(name, request.Definition!));
    }

    public Task<Skill> UpdateAsync(
        string name, SkillUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("update:" + name);
        ThrowIfInvalid(request.Definition!);
        return Task.FromResult(Make(name, request.Definition!) with { CurrentRevision = 2 });
    }

    public Task DeleteAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("delete:" + name);
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Configuration Set CRUD 服務 fake(代表 backend :8002)。依 upsert 的 name / route 的 id 重現 backend 錯誤:
/// name=dup_set → 409、name=bad_values → 422(values 越界的 fieldErrors)、id=GhostId → 404。
/// Calls 是靜態的:讓「401 時請求不得抵達 backend」與「/active 不得被 {id} 吞掉轉發」可被斷言。
/// </summary>
public sealed class FakeConfigurationSetService : IConfigurationSetService
{
    public static readonly List<string> Calls = new();

    public const string ExistingId = "11111111-1111-1111-1111-111111111111";
    public const string GhostId = "22222222-2222-2222-2222-222222222222";

    /// <summary>backend 422 的 values 越界 fieldErrors:必須原樣穿過 platform。</summary>
    public static readonly Dictionary<string, string> RangeErrors =
        new() { ["retrieval.top_k"] = "必須介於 1 到 50" };

    private static JsonElement El(object v) => JsonSerializer.SerializeToElement(v);

    private static ConfigurationSet Make(string id, string name, bool active) =>
        new(id, name, active,
            new Dictionary<string, JsonElement> { ["retrieval.top_k"] = El(8), ["llm.model"] = El("gpt-4o-mini") },
            "admin-a", "2026-07-14T00:00:00Z", "2026-07-14T00:00:00Z");

    public Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("list");
        // 穿透:含 created_at(舊 ConfigurationSetInfo DTO 會丟掉此欄位),不含 values(清單省略)。
        return Task.FromResult(FakeJson.Of(
            $$"""[{"id":"{{ExistingId}}","name":"prod","is_active":true,"created_at":"2026-07-14T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}]"""));
    }

    public Task<JsonElement> GetAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("get:" + id);
        if (id == GhostId)
        {
            throw new WorkflowNotFoundException("找不到 Configuration Set");
        }

        // 穿透:直接把 backend 形狀(含 JsonPropertyName snake_case、values jsonb)序列化成 JsonElement。
        return Task.FromResult(JsonSerializer.SerializeToElement(Make(id, "prod", true)));
    }

    public Task<ConfigurationSet> CreateAsync(ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("create:" + request.Name);
        ThrowForName(request.Name);
        return Task.FromResult(Make(ExistingId, request.Name!, false));
    }

    public Task<ConfigurationSet> UpdateAsync(string id, ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("update:" + id);
        ThrowForName(request.Name);
        return Task.FromResult(Make(id, request.Name!, false));
    }

    public Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("delete:" + id);
        if (id == GhostId)
        {
            throw new WorkflowNotFoundException("找不到 Configuration Set");
        }

        return Task.CompletedTask;
    }

    public Task<ConfigurationSet> ActivateAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("activate:" + id);
        if (id == GhostId)
        {
            throw new WorkflowNotFoundException("找不到 Configuration Set");
        }

        return Task.FromResult(Make(id, "prod", true));
    }

    private static void ThrowForName(string? name)
    {
        switch (name)
        {
            case "dup_set":
                throw new DownstreamConflictException("Configuration Set 名稱已存在：" + name);
            case "bad_values":
                throw new SkillValidationFailedException("Configuration Set 驗證失敗") { FieldErrors = RangeErrors };
        }
    }
}

/// <summary>D3 run API fake used to verify the Platform authentication and feature-gate boundary.</summary>
public sealed class FakeAgentRunService : IAgentRunService
{
    public static readonly List<string> Calls = new();
    public static UserContext? LastContext { get; set; }
    public const string RunIdText = "44444444-4444-4444-4444-444444444444";

    public FakeAgentRunService(FakeCallScope scope) => scope.Own(Calls);

    private const string RunJson =
        """{"id":"44444444-4444-4444-4444-444444444444","status":"queued","state_version":1,"checkpoint_version":0}""";

    public Task<AgentProxyResponse> StartAsync(
        Guid agentId,
        string? message,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        Calls.Add($"start:{agentId:D}:{message}:{idempotencyKey}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(202, RunJson, null));
    }

    public Task<AgentProxyResponse> GetAsync(
        Guid runId,
        UserContext ctx,
        CancellationToken ct = default)
    {
        Calls.Add($"get:{runId:D}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(200, RunJson, null));
    }

    public Task<AgentProxyResponse> EventsAsync(
        Guid runId,
        long afterSequence,
        int limit,
        UserContext ctx,
        CancellationToken ct = default)
    {
        Calls.Add($"events:{runId:D}:{afterSequence}:{limit}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(
            200,
            $$"""{"run_id":"{{RunIdText}}","events":[],"next_sequence":{{afterSequence}}}""",
            null));
    }

    public Task<AgentProxyResponse> ResumeAsync(
        Guid runId,
        string? message,
        long? expectedCheckpointVersion,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        Calls.Add($"resume:{runId:D}:{message}:{expectedCheckpointVersion}:{idempotencyKey}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(202, RunJson, null));
    }

    public Task<AgentProxyResponse> CancelAsync(
        Guid runId,
        string? reason,
        string? idempotencyKey,
        UserContext ctx,
        CancellationToken ct = default)
    {
        Calls.Add($"cancel:{runId:D}:{reason}:{idempotencyKey}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(202, RunJson, null));
    }

    public Task<AgentProxyResponse> ApprovalsAsync(Guid runId, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add($"approvals:{runId:D}:{ctx.UserId}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(200,
            """[{"id":"66666666-6666-4666-8666-666666666666","run_id":"44444444-4444-4444-4444-444444444444","status":"pending","required_role":"USER","action_fingerprint":"redacted"}]""", null));
    }

    public Task<AgentProxyResponse> DecideApprovalAsync(Guid runId, Guid approvalId, bool approve, string? reason, string? idempotencyKey, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add($"approval:{runId:D}:{approvalId:D}:{approve}:{reason}:{idempotencyKey}:{ctx.UserId}");
        LastContext = ctx;
        return Task.FromResult(new AgentProxyResponse(202,
            """{"id":"66666666-6666-4666-8666-666666666666","status":"approved"}""", null));
    }
}

public sealed class FakeOrchestratorRunService : IOrchestratorRunService
{
    public static readonly List<string> Calls = new();
    public const string RunIdText = "55555555-5555-5555-5555-555555555555";
    private const string Body = """{"id":"55555555-5555-5555-5555-555555555555","status":"queued","state_version":1}""";
    public FakeOrchestratorRunService(FakeCallScope scope) => scope.Own(Calls);
    public Task<AgentProxyResponse> StartAsync(Guid id,string? message,string? conversation,string? key,UserContext user,CancellationToken ct=default)
    { Calls.Add($"start:{id:D}:{message}:{conversation}:{key}:{user.UserId}"); return Task.FromResult(new AgentProxyResponse(202,Body,null)); }
    public Task<AgentProxyResponse> GetAsync(Guid id,UserContext user,CancellationToken ct=default)
    { Calls.Add($"get:{id:D}:{user.UserId}"); return Task.FromResult(new AgentProxyResponse(200,Body,null)); }
    public Task<AgentProxyResponse> EventsAsync(Guid id,long after,int limit,UserContext user,CancellationToken ct=default)
    { Calls.Add($"events:{id:D}:{after}:{limit}:{user.UserId}"); return Task.FromResult(new AgentProxyResponse(200,$$"""{"run_id":"{{RunIdText}}","events":[],"next_sequence":{{after}}}""",null)); }
    public Task<AgentProxyResponse> CancelAsync(Guid id,string? reason,string? key,UserContext user,CancellationToken ct=default)
    { Calls.Add($"cancel:{id:D}:{reason}:{key}:{user.UserId}"); return Task.FromResult(new AgentProxyResponse(202,Body,null)); }
}

/// <summary>
/// D4 管理代理 fake:把收到的 resource/id/suffix/method/身分/If-Match 原樣回成 JSON body,
/// 讓 Web 層可斷言「兩個 controller 各自送出正確的 resource 與 If-Match 轉發決策」。
/// </summary>
public sealed class FakeWorkflowAdminService : IWorkflowAdminService
{
    public Task<AgentProxyResponse> SendAsync(
        HttpMethod method,
        string resource,
        Guid? id,
        string? suffix,
        UserContext context,
        string? ifMatch = null,
        JsonElement? body = null,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            resource,
            id,
            suffix,
            method = method.Method,
            user = context.UserId,
            tenant = context.TenantCode,
            if_match = ifMatch,
        });
        return Task.FromResult(new AgentProxyResponse(200, payload, "\"7\""));
    }
}

/// <summary>
/// Agent Registry 服務 fake(代表 backend :8002 的 /api/agents 透明代理)。重現 D1 需驗的 backend 行為:
/// 所有 Builder 端點非 ADMIN → 403、GET 帶 ETag、If-Match 版本不符 → 409、
/// 未知 id → 404。回傳 <see cref="AgentProxyResponse"/>(status + 原始 JSON body + ETag),由 controller 原樣寫回。
/// Calls 是靜態的,讓「flag off 時請求不得抵達代理」可被斷言。
/// </summary>
public sealed class FakeAgentService : IAgentService
{
    public static readonly List<string> Calls = new();
    public static UserContext? LastContext { get; set; }
    public const string ExistingIdText = "11111111-1111-1111-1111-111111111111";
    public const string CreatedIdText = "33333333-3333-3333-3333-333333333333";
    public const string GhostIdText = "22222222-2222-2222-2222-222222222222";
    public static readonly Guid ExistingId = Guid.Parse(ExistingIdText);
    public static readonly Guid GhostId = Guid.Parse(GhostIdText);

    public FakeAgentService(FakeCallScope scope) => scope.Own(Calls);

    /// <summary>目前 draft 版本的 ETag(GET 回傳、If-Match 需相符才放行 PUT/validate)。</summary>
    public const string CurrentETag = "\"1\"";

    private static AgentProxyResponse Ok(string body, string? etag = null) => new(200, body, etag);

    /// <summary>backend 的 ApiError envelope(六欄,含 code/correlationId),由代理層原樣穿透。</summary>
    private static AgentProxyResponse ApiError(int status, string message) => new(
        status,
        $"{{\"timestamp\":\"2026-07-24T00:00:00Z\",\"status\":{status},\"code\":\"backend_code_{status}\",\"message\":{JsonSerializer.Serialize(message)},\"correlationId\":\"backend-trace-1\",\"fieldErrors\":{{}}}}",
        null);

    private static AgentProxyResponse Forbidden() => ApiError(403, "權限不足");

    private const string AgentJson =
        """{"id":"11111111-1111-1111-1111-111111111111","name":"研究員","slug":"researcher","description":"內部研究","enabled":true,"draft_version":1,"draft_validated_version":1,"published_revision":null,"draft":{}}""";

    public Task<AgentProxyResponse> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("list");
        LastContext = ctx;
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        return Task.FromResult(Ok(
            """[{"id":"11111111-1111-1111-1111-111111111111","name":"研究員","slug":"researcher","enabled":true,"draft_version":1,"draft_validated_version":1,"published_revision":null}]"""));
    }

    public Task<AgentProxyResponse> CreateAsync(UserContext ctx, JsonElement? body, CancellationToken ct = default)
    {
        Calls.Add("create");
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        return Task.FromResult(new AgentProxyResponse(
            201,
            """{"id":"33333333-3333-3333-3333-333333333333","name":"新代理","slug":"new-agent","enabled":true,"draft_version":1,"draft_validated_version":null,"published_revision":null,"draft":{}}""",
            CurrentETag));
    }

    public Task<AgentProxyResponse> GetAsync(Guid id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("get:" + id.ToString("D"));
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        if (id == GhostId)
        {
            return Task.FromResult(ApiError(404, "找不到 Agent：" + GhostIdText));
        }

        return Task.FromResult(Ok(AgentJson, CurrentETag));
    }

    public Task<AgentProxyResponse> UpdateDraftAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default)
    {
        Calls.Add($"update:{id:D}:{ifMatch}");
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        // backend 樂觀鎖語意:缺 If-Match → 428、版本過期 → 409(透明穿透)。
        if (ifMatch is null)
        {
            return Task.FromResult(ApiError(428, "需要 If-Match 前置條件"));
        }

        if (ifMatch != CurrentETag)
        {
            return Task.FromResult(ApiError(409, "草稿版本衝突，請重新載入"));
        }

        return Task.FromResult(Ok(
            $$$"""{"id":"{{{id:D}}}","name":"研究員","slug":"researcher","enabled":true,"draft_version":2,"draft_validated_version":null,"published_revision":null,"draft":{}}""",
            "\"2\""));
    }

    public Task<AgentProxyResponse> DeactivateAsync(Guid id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("deactivate:" + id.ToString("D"));
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        return Task.FromResult(new AgentProxyResponse(204, string.Empty, null));
    }

    public Task<AgentProxyResponse> EnableAsync(Guid id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("enable:" + id.ToString("D"));
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        return Task.FromResult(Ok(
            $$$"""{"id":"{{{id:D}}}","slug":"researcher","enabled":true,"draft_version":1,"draft_validated_version":1,"published_revision":null,"draft":{}}""",
            CurrentETag));
    }

    public Task<AgentProxyResponse> ValidateAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default)
    {
        Calls.Add($"validate:{id:D}:{ifMatch}");
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        if (ifMatch is null)
        {
            return Task.FromResult(ApiError(428, "需要 If-Match 前置條件"));
        }

        if (ifMatch != CurrentETag)
        {
            return Task.FromResult(ApiError(409, "草稿版本衝突，請重新載入"));
        }

        return Task.FromResult(Ok("""{"valid":true,"errors":[]}""", CurrentETag));
    }

    public Task<AgentProxyResponse> PublishAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default)
    {
        Calls.Add($"publish:{id:D}:{ifMatch}");
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        // 真 backend publish 契約：body 必帶 expected_draft_version；If-Match 可被透明轉送，
        // 但不是 publish 的 concurrency authority。
        if (body is not JsonElement payload
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("expected_draft_version", out var versionElement)
            || !versionElement.TryGetInt64(out var expectedVersion))
        {
            return Task.FromResult(ApiError(400, "publish 必須帶 expected_draft_version"));
        }

        if (expectedVersion != 1)
        {
            return Task.FromResult(ApiError(409, "草稿版本衝突，請重新載入"));
        }

        return Task.FromResult(Ok(
            $$$"""{"id":"{{{id:D}}}","slug":"researcher","name":"研究員","description":"內部研究","enabled":true,"draft_version":1,"draft_validated_version":1,"published_revision":1,"draft":{}}""",
            CurrentETag));
    }

    public Task<AgentProxyResponse> RevisionsAsync(Guid id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("revisions:" + id.ToString("D"));
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        return Task.FromResult(Ok(
            """[{"revision":1,"status":"published","definition_sha256":"abc","runtime_workflow_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","runtime_workflow_revision":1,"skill_bindings":[],"created_by":"admin-a","created_at":"2026-07-24T00:00:00Z"}]"""));
    }

    public Task<AgentProxyResponse> RestoreRevisionAsync(
        Guid id, int revision, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add($"restore:{id:D}:{revision}");
        if (ctx.Role != "ADMIN")
        {
            return Task.FromResult(Forbidden());
        }

        return Task.FromResult(Ok(
            $$$"""{"id":"{{{id:D}}}","slug":"researcher","name":"研究員","description":"內部研究","enabled":true,"draft_version":1,"draft_validated_version":1,"published_revision":2,"draft":{}}""",
            CurrentETag));
    }
}

/// <summary>組態服務 fake:PUT 非 ADMIN → Forbidden(對外 403),重現 backend 的角色把關。</summary>
public sealed class FakeConfigService : IConfigService
{
    public Task<IReadOnlyList<ConfigItem>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        // backend 的 GET /api/config 也是 ADMIN-only(403);fake 照抄該契約,才測得到 platform 的轉發。
        if (ctx.Role != "ADMIN")
        {
            throw new WorkflowForbiddenException("權限不足，無法讀取系統組態");
        }

        return Task.FromResult<IReadOnlyList<ConfigItem>>(new List<ConfigItem>
        {
            new("chat_model", "gpt-4o-mini", DateTime.UtcNow),
        });
    }

    public Task<ConfigItem> UpdateAsync(string key, ConfigUpdateRequest request, UserContext ctx, CancellationToken ct = default)
    {
        if (ctx.Role != "ADMIN")
        {
            throw new WorkflowForbiddenException("權限不足，無法修改系統組態");
        }

        return Task.FromResult(new ConfigItem(key, request.Value!, DateTime.UtcNow));
    }

    // 這批 Web.Tests 從不啟用 PROMPT_ARTIFACTS_ENABLED(PromptCompositionResolver 因此未註冊,見
    // CopilotAguiApiTests 的說明),本方法目前沒有呼叫端會踩到——回 null(canary 未啟用)即可。
    public Task<ConfigItem?> GetRuntimeAsync(string key, UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<ConfigItem?>(null);
}
