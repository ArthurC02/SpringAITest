using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Web.Tests;

/// <summary>Web 整合測試用的 LLM 代理 fake:阻塞回固定字串、串流吐「你好」「世界」。記下最後一次工具列供斷言。</summary>
public sealed class FakeLlmAgent : ILlmAgent
{
    public IReadOnlyList<LlmTool>? LastTools { get; private set; }

    public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, CancellationToken ct)
    {
        LastTools = tools;
        return Task.FromResult("測試回覆");
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        LastTools = tools;
        await Task.Yield();
        // 訊息為「多行」時,吐一塊含換行的 chunk,驗 SSE 把單一 chunk 拆成多個 data: 行。
        if (messages.Count > 0 && messages[^1].Content == "多行")
        {
            yield return "甲\n乙";
            yield break;
        }

        yield return "你好";
        yield return "世界";
    }
}

/// <summary>不做事的 mem0 fake(記憶最佳努力,不影響聊天)。</summary>
public sealed class FakeMem0Client : IMem0Client
{
    public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>聊天歷史 store fake:AddAsync 回遞增 id;歷史空清單。</summary>
public sealed class FakeConversationStore : IConversationStore
{
    private long _nextId = 1;

    public Task<ChatResponse> AddAsync(string prompt, string reply, CancellationToken ct = default)
        => Task.FromResult(new ChatResponse(_nextId++, reply, DateTime.UtcNow));

    public Task<IReadOnlyList<ChatResponse>> ListDescAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatResponse>>(new List<ChatResponse>());
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

/// <summary>工作流服務 fake:依名稱決定行為(ghost → NotFound),用來測 controller/認證/序列化/例外映射。</summary>
public sealed class FakeWorkflowService : IWorkflowService
{
    public Task<IReadOnlyList<WorkflowInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkflowInfo>>(new List<WorkflowInfo>
        {
            new("rag_qa", "檢索式問答", "USER"),
        });

    public Task<WorkflowInvokeResponse> InvokeAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        // 特殊名稱觸發各類下游/服務例外,驗全域例外→狀態碼映射(對外 404/400/502/500)。
        switch (name)
        {
            case "ghost":
                throw new WorkflowNotFoundException("找不到工作流：" + name);
            case "boom":
                throw new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 500");
            case "badinput":
                throw new WorkflowBadInputException("工作流輸入不符合規範：欄位錯誤");
            case "explode":
                throw new InvalidOperationException("非預期錯誤");
        }

        var output = new Dictionary<string, JsonElement> { ["ok"] = JsonSerializer.SerializeToElement(true) };
        return Task.FromResult(new WorkflowInvokeResponse(name, output));
    }

    // ---- Skill 引擎(:8001)----

    /// <summary>記錄引擎端的呼叫,用來斷言「catalog 走引擎、不走 backend CRUD」。</summary>
    public static readonly List<string> EngineCalls = new();

    public Task<JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("invoke:" + name);

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

    public Task<JsonElement> GetSkillCatalogAsync(UserContext ctx, CancellationToken ct = default)
    {
        EngineCalls.Add("catalog");
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
