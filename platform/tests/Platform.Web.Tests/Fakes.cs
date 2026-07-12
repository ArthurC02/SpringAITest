using System.Runtime.CompilerServices;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Web.Tests;

/// <summary>Web 整合測試用的 LLM 代理 fake:阻塞回固定字串、串流吐「你好」「世界」。</summary>
public sealed class FakeLlmAgent : ILlmAgent
{
    public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
        => Task.FromResult("測試回覆");

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
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
        if (name == "ghost")
        {
            throw new WorkflowNotFoundException("找不到工作流：" + name);
        }

        var output = new Dictionary<string, JsonElement> { ["ok"] = JsonSerializer.SerializeToElement(true) };
        return Task.FromResult(new WorkflowInvokeResponse(name, output));
    }
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
