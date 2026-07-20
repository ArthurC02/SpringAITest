using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Web.Tests;

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
/// AddAsync 回遞增 id 並依 ctx 的租戶/使用者記進 Saved;ListDescAsync 依 ctx 過濾只回同租戶同使用者的紀錄
/// (鏡射真正 ConversationStore 的隔離語意,供 A-21 的租戶隔離斷言使用)。
/// ThrowOnAdd 讓 A-15/A-16 腳本化持久化失敗(阻塞 500 vs 串流 best-effort 的決策表兩半)。
/// 靜態:controller 端以 AddScoped 註冊,每次請求都是新實例,狀態要跨請求可見必須是靜態。
/// </summary>
public sealed class FakeConversationStore : IConversationStore
{
    private static long _nextId = 1;

    /// <summary>測試腳本開關:true 時 AddAsync 擲例外,模擬持久化層失敗。用畢務必在 finally 還原為 false。</summary>
    public static bool ThrowOnAdd { get; set; }

    public static readonly List<(string TenantCode, string UserId, ChatResponse Response)> Saved = new();

    public Task<ChatResponse> AddAsync(string prompt, string reply, UserContext ctx, CancellationToken ct = default)
    {
        if (ThrowOnAdd)
        {
            throw new InvalidOperationException("持久化失敗（測試腳本）");
        }

        var response = new ChatResponse(_nextId++, reply, DateTime.UtcNow);
        Saved.Add((ctx.TenantCode, ctx.UserId, response));
        return Task.FromResult(response);
    }

    public Task<IReadOnlyList<ChatResponse>> ListDescAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatResponse>>(Saved
            .Where(s => s.TenantCode == ctx.TenantCode && s.UserId == ctx.UserId)
            .Select(s => s.Response)
            .Reverse()
            .ToList());
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
public sealed class FakeWorkflowService : IWorkflowService
{
    // ---- Skill 引擎(:8001)----

    /// <summary>記錄引擎端的呼叫,用來斷言「catalog 走引擎、不走 backend CRUD」。</summary>
    public static readonly List<string> EngineCalls = new();

    /// <summary>
    /// 補 G5(copilot-shared-core 04-acceptance-test.md §4.4):IWorkflowService 在 DI 是 Scoped,
    /// SkillRoutingAgent 每次呼叫各自開一個新 scope,跨請求(HTTP request)拿到的是不同實例,無法用實例
    /// 欄位收集 (Name, Input) 供 B-P4-12(ChatView 與副駕的 SkillInvokes 應完全相同)這類跨請求斷言。
    /// 靜態集合擇簡繞過:不必改動 DI 生命週期,天然跨 scope/跨請求可見(與 EngineCalls/Calls 等既有靜態
    /// 收集器同一慣例)。測試須自行在案例開頭/結尾清空,避免跨測試污染。
    /// </summary>
    public static readonly List<(string Name, Dictionary<string, JsonElement> Input)> SkillInvokes = new();

    public Task<JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("invoke:" + name);
        SkillInvokes.Add((name, input));

        // skill invoke 的錯誤碼與 /workflows/{name}/invoke 逐一相同(同一組觸發名稱)。
        switch (name)
        {
            case "ghost":
                throw new WorkflowNotFoundException("找不到 Skill：" + name);
            case "forbidden":
                throw new WorkflowForbiddenException("權限不足，無法執行 Skill：" + name);
            case "badinput":
                throw new WorkflowBadInputException("Skill 輸入不符合規範：缺少 query");
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
            : Json("""{"valid":true,"errors":[],"skill":{"name":"quarterly_qa","description":"季報問答","required_role":"USER"}}"""));
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
            """[{"name":"kb_query","description":"知識查詢","required_role":"USER","source":"builtin","revision":null},"""
            + """{"name":"quarterly_qa","description":"季報問答","required_role":"USER","source":"custom","revision":3}]"""));
    }

    public Task<JsonElement> GetNodeCatalogAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("nodes");
        return Task.FromResult(Json(
            """[{"name":"query_intake","version":"1.0","description":"輸入正規化","reads":[],"writes":["original_query"],"requires_tools":[]}]"""));
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}

/// <summary>文件服務 fake:ghost → NotFound。建立回受理狀態(202 語意)。</summary>
public sealed class FakeDocumentService : IDocumentService
{
    public Task<DocumentAccepted> CreateAsync(DocumentCreateRequest request, UserContext ctx, CancellationToken ct = default)
        => Task.FromResult(new DocumentAccepted("doc-1", request.Title ?? string.Empty, "processing"));

    public Task<IReadOnlyList<DocumentInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DocumentInfo>>(new List<DocumentInfo>
        {
            new("doc-1", "標題", 3, "2026-07-11T00:00:00Z", "ready"),
        });

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
/// definition 內含 dup_skill → 409、名稱 ghost → 404、定義含 __invalid__ → 422(引擎錯誤碼)。
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
        new(name, "季報問答", definition, "USER", true, 1, "2026-07-14T00:00:00Z", "2026-07-14T00:00:00Z");

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

    public Task<IReadOnlyList<SkillInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("list");
        return Task.FromResult<IReadOnlyList<SkillInfo>>(new List<SkillInfo>
        {
            new("echo_skill", "季報問答", "USER", true, 1, "2026-07-14T00:00:00Z", "2026-07-14T00:00:00Z"),
        });
    }

    public Task<Skill> GetAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("get:" + name);
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        return Task.FromResult(Make(name, $"name: {name}\nflow:\n  - node: query_intake\n"));
    }

    public Task<IReadOnlyList<SkillRevisionInfo>> GetRevisionsAsync(
        string name, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("revisions:" + name);
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到 Skill：" + name);
        }

        return Task.FromResult<IReadOnlyList<SkillRevisionInfo>>(new List<SkillRevisionInfo>
        {
            new(2, "name: " + name + "\n# r2\n", "sha2", "admin-a", "2026-07-14T00:00:00Z"),
            new(1, "name: " + name + "\n# r1\n", "sha1", "admin-a", "2026-07-13T00:00:00Z"),
        });
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

    public Task<Skill> CreateAsync(SkillUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        var name = NameOf(request.Definition!);
        Calls.Add("create:" + name);
        ThrowIfInvalid(request.Definition!);

        if (name == "dup_skill")
        {
            // 重現 backend 的 409(同名衝突):訊息原樣往上拋,platform 不改寫。
            throw new DownstreamConflictException("Skill 名稱已存在：" + name);
        }

        if (name == "bad_field")
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

    public Task<IReadOnlyList<ConfigurationSetInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("list");
        return Task.FromResult<IReadOnlyList<ConfigurationSetInfo>>(new List<ConfigurationSetInfo>
        {
            new(ExistingId, "prod", true, "2026-07-14T00:00:00Z"),
        });
    }

    public Task<ConfigurationSet> GetAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        Calls.Add("get:" + id);
        if (id == GhostId)
        {
            throw new WorkflowNotFoundException("找不到 Configuration Set");
        }

        return Task.FromResult(Make(id, "prod", true));
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

/// <summary>組態服務 fake:PUT 非 ADMIN → Forbidden(對外 403),重現 backend 的角色把關。</summary>
public sealed class FakeConfigService : IConfigService
{
    public Task<IReadOnlyList<ConfigItem>> ListAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConfigItem>>(new List<ConfigItem>
        {
            new("chat_model", "gpt-4o-mini", DateTime.UtcNow),
        });

    public Task<ConfigItem> UpdateAsync(string key, ConfigUpdateRequest request, UserContext ctx, CancellationToken ct = default)
    {
        if (ctx.Role != "ADMIN")
        {
            throw new WorkflowForbiddenException("權限不足，無法修改系統組態");
        }

        return Task.FromResult(new ConfigItem(key, request.Value!, DateTime.UtcNow));
    }
}
