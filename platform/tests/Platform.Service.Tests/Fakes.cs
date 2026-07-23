using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
// Microsoft.Extensions.AI 也定義 ChatResponse;此檔的 ChatResponse 一律指 Dtos 版(對外 DTO / fake store)。
using ChatResponse = Platform.Service.Dtos.ChatResponse;

namespace Platform.Service.Tests;

/// <summary>建 BackendClient(套在 StubHttpMessageHandler 上)的共用工廠。</summary>
internal static class TestBackend
{
    public static BackendClient Client(StubHttpMessageHandler stub, string baseUrl = "http://backend", string token = "tok")
        => new(new HttpClient(stub), new BackendOptions { BaseUrl = baseUrl, InternalToken = token });
}

/// <summary>建 stub HttpResponseMessage 的共用輔助:任意 JSON body、ApiError 形狀的錯誤 body。</summary>
internal static class TestHttp
{
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        Json(status, $"{{\"timestamp\":\"2026-07-12T00:00:00Z\",\"status\":{(int)status},\"message\":{JsonSerializer.Serialize(message)},\"fieldErrors\":{{}}}}");
}

/// <summary>
/// 文件佇列 fake:記錄最後一則訊息;可設定發佈拋例外。真實 RabbitDocumentQueue 開了 publisher
/// confirmations,發佈失敗(broker 不可達或被 broker nack)都會拋例外 → DocumentService 映射 502;
/// 此 fake 以 ThrowOnPublish 一律模擬該「發佈拋例外」行為。
/// </summary>
public sealed class FakeDocumentQueue : IDocumentQueue
{
    public DocumentMessage? Last { get; private set; }
    public bool ThrowOnPublish { get; set; }

    public Task PublishAsync(DocumentMessage message, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
        {
            throw new InvalidOperationException("broker 不可達或被 nack");
        }

        Last = message;
        return Task.CompletedTask;
    }
}

/// <summary>記憶體版聊天歷史 store fake:記錄新增、可模擬新增失敗、清單依時間 DESC。</summary>
public sealed class FakeConversationStore : IConversationStore
{
    public List<(string Prompt, string Reply)> Saved { get; } = new();
    public List<UserContext> AddCalledWith { get; } = new();
    public List<ChatResponse> Items { get; } = new();
    public bool ThrowOnAdd { get; set; }
    private long _nextId = 1;

    public Task<ChatResponse> AddAsync(string prompt, string reply, UserContext ctx, CancellationToken ct = default)
    {
        if (ThrowOnAdd)
        {
            throw new BackendCallException("模擬持久化失敗");
        }

        Saved.Add((prompt, reply));
        AddCalledWith.Add(ctx);
        return Task.FromResult(new ChatResponse(_nextId++, reply, DateTime.UtcNow));
    }

    public Task<IReadOnlyList<ChatResponse>> ListDescAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatResponse>>(
            Items.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToList());
}

/// <summary>
/// 可控回覆的 LLM 代理 fake;會記下最後一次收到的訊息列與工具列以供斷言。
/// 路由 → 摘要多次呼叫:以 Responses 佇列腳本化「連續 CompleteAsync」的回覆(用盡後退回 Response);
/// CompleteCalls 逐次記下每一次 CompleteAsync 收到的訊息列,供斷言路由/摘要各自的內容。
/// </summary>
public sealed class FakeLlmAgent : ILlmAgent
{
    public string Response { get; set; } = "測試回覆";
    public IReadOnlyList<string> Chunks { get; set; } = new[] { "你好", "世界" };
    public IReadOnlyList<LlmMessage>? LastMessages { get; private set; }

    /// <summary>腳本化的連續 CompleteAsync 回覆(第 1 次=路由、第 2 次=摘要…);空或用盡後回退 Response。</summary>
    public Queue<string> Responses { get; } = new();

    /// <summary>每一次 CompleteAsync 收到的訊息列(索引 0 = 第一次呼叫);用來分別斷言路由與摘要訊息。</summary>
    public List<IReadOnlyList<LlmMessage>> CompleteCalls { get; } = new();

    /// <summary>非 null 時:吐出第 N 塊後擲例外(模擬串流中途失敗),用來驗半截回覆不持久化。</summary>
    public int? ThrowAfterChunks { get; set; }

    /// <summary>true 時:第一次 CompleteAsync(即路由呼叫)擲例外,用來驗路由失敗退純聊天不炸。</summary>
    public bool ThrowOnFirstComplete { get; set; }

    public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
    {
        CompleteCalls.Add(messages);
        LastMessages = messages;

        if (ThrowOnFirstComplete && CompleteCalls.Count == 1)
        {
            throw new InvalidOperationException("路由呼叫失敗");
        }

        var reply = Responses.Count > 0 ? Responses.Dequeue() : Response;
        return Task.FromResult(reply);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        LastMessages = messages;
        var emitted = 0;
        foreach (var chunk in Chunks)
        {
            await Task.Yield();
            yield return chunk;
            emitted++;
            if (ThrowAfterChunks is int n && emitted >= n)
            {
                throw new InvalidOperationException("串流中途失敗");
            }
        }
    }
}

/// <summary>Skill 引擎 fake:可注入目錄、可模擬失敗、可覆寫輸出;記下呼叫序供斷言。</summary>
public sealed class FakeWorkflowService : IWorkflowService
{
    // ---- Skill 引擎(:8001):聊天 → Skill 路由用得到,可注入目錄與 skill invoke 行為並記錄呼叫。----

    /// <summary>非 null 時 GetSkillCatalogAsync 回這包目錄;預設回空陣列(等同「無 skill」)。</summary>
    public System.Text.Json.JsonElement? Catalog { get; set; }

    /// <summary>非 null 時 GetSkillCatalogAsync 擲此例外(模擬 workflow 502 / 逾時 / 壞 JSON)。</summary>
    public Exception? ThrowOnCatalog { get; set; }

    /// <summary>catalog 每次呼叫記下呼叫者身分;Count 即取目錄次數(驗匿名不讀、每輪一次)。</summary>
    public List<UserContext> CatalogContexts { get; } = new();

    /// <summary>非 null 時 InvokeSkillAsync 擲此例外(模擬 skill invoke 失敗)。</summary>
    public Exception? ThrowOnSkillInvoke { get; set; }

    /// <summary>非 null 時 InvokeSkillAsync 回這包輸出;預設回 {skill=name}。</summary>
    public System.Text.Json.JsonElement? SkillOutput { get; set; }

    /// <summary>非 null 時,依 skill 名個別覆寫回應(優先於 SkillOutput);用來模擬同一輪呼叫兩個不同 skill 各回不同輸出(例如 kb-query 棄答兜底打 rag-qa)。</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? SkillOutputByName { get; set; }

    /// <summary>skill invoke 呼叫序:name + input 字典 + 身分,供斷言。</summary>
    public List<(string Name, Dictionary<string, System.Text.Json.JsonElement> Input, UserContext Ctx)> SkillInvokes { get; } = new();

    public (string Name, Dictionary<string, System.Text.Json.JsonElement> Input, UserContext Ctx)? LastSkillInvoke
        => SkillInvokes.Count > 0 ? SkillInvokes[^1] : null;

    public Task<System.Text.Json.JsonElement> InvokeSkillAsync(
        string name, Dictionary<string, System.Text.Json.JsonElement> input, UserContext ctx,
        CancellationToken ct = default)
    {
        if (ThrowOnSkillInvoke is not null)
        {
            throw ThrowOnSkillInvoke;
        }

        SkillInvokes.Add((name, input, ctx));
        if (SkillOutputByName is not null && SkillOutputByName.TryGetValue(name, out var byName))
        {
            return Task.FromResult(byName);
        }

        return Task.FromResult(SkillOutput
            ?? System.Text.Json.JsonSerializer.SerializeToElement(new { skill = name }));
    }

    public Task<System.Text.Json.JsonElement> ValidateSkillAsync(
        string definition, UserContext ctx, CancellationToken ct = default)
        => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(new { valid = true }));

    public Task<System.Text.Json.JsonElement> GetSkillCatalogAsync(UserContext ctx, CancellationToken ct = default)
    {
        CatalogContexts.Add(ctx);
        if (ThrowOnCatalog is not null)
        {
            throw ThrowOnCatalog;
        }

        return Task.FromResult(Catalog
            ?? System.Text.Json.JsonSerializer.SerializeToElement(Array.Empty<object>()));
    }

    public Task<System.Text.Json.JsonElement> GetNodeCatalogAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(Array.Empty<object>()));
}

/// <summary>
/// mem0 client fake:可設定 recall 回傳、記錄 remember 呼叫。
/// 補 G1:ThrowOnRecall / ThrowOnRememberAsync 各自可設定擲例外,用來驗「mem0 best-effort,錯誤吞掉聊天不中斷」
/// 這條安全語義(A-14)——production 的 IMem0Client 實作(Mem0Client)本身把例外吞掉回空字串/no-op,
/// 但 ChatService 對 IMem0Client 的呼叫並未再包一層 try/catch,故此 fake 直接擲出以驗證呼叫端行為。
/// </summary>
public sealed class FakeMem0Client : IMem0Client
{
    public string RecallResult { get; set; } = string.Empty;
    public List<(string UserId, string Query)> Recalled { get; } = new();
    public List<(string UserId, string UserMessage, string AiReply)> Remembered { get; } = new();

    /// <summary>非 null 時 RecallAsync 擲此例外。</summary>
    public Exception? ThrowOnRecall { get; set; }

    /// <summary>在 RecallAsync 內、檢查 ThrowOnRecall 前執行，供取消傳播測試在呼叫中取消同一 token。</summary>
    public Action<CancellationToken>? OnRecall { get; set; }

    /// <summary>非 null 時 RememberAsync 擲此例外。</summary>
    public Exception? ThrowOnRemember { get; set; }

    /// <summary>在 RememberAsync 內、檢查 ThrowOnRemember 前執行，供取消傳播測試在呼叫中取消同一 token。</summary>
    public Action<CancellationToken>? OnRemember { get; set; }

    public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
    {
        OnRecall?.Invoke(ct);
        if (ThrowOnRecall is not null)
        {
            throw ThrowOnRecall;
        }

        Recalled.Add((userId, query));
        return Task.FromResult(RecallResult);
    }

    public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
    {
        OnRemember?.Invoke(ct);
        if (ThrowOnRemember is not null)
        {
            throw ThrowOnRemember;
        }

        Remembered.Add((userId, userMessage, aiReply));
        return Task.CompletedTask;
    }
}

/// <summary>
/// P2:「未命中」純聊天改跑共用的 hosted agent(框架管理短期歷史);此 fake 取代該路徑上的真實
/// <see cref="IChatClient"/>(LiteLLM/OpenAI)。可控回應(阻塞)/串流塊,並記錄每次呼叫收到的
/// messages + <see cref="ChatOptions"/>(供斷言 Instructions 與訊息內容,例如護欄/mem0/租戶隔離)。
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    public string Response { get; set; } = "測試回覆";
    public IReadOnlyList<string> Chunks { get; set; } = new[] { "你好", "世界" };

    /// <summary>非 null 時:串流吐出第 N 塊後擲例外(模擬串流中途失敗)。</summary>
    public int? ThrowAfterChunks { get; set; }

    /// <summary>每一次呼叫收到的 messages + options(索引 0 = 第一次呼叫)。</summary>
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

    public IReadOnlyList<ChatMessage>? LastMessages => Calls.Count > 0 ? Calls[^1].Messages : null;

    public ChatOptions? LastOptions => Calls.Count > 0 ? Calls[^1].Options : null;

    public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToList(), options));
        return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(new ChatMessage(ChatRole.Assistant, Response)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToList(), options));
        var emitted = 0;
        foreach (var chunk in Chunks)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            emitted++;
            if (ThrowAfterChunks is int n && emitted >= n)
            {
                throw new InvalidOperationException("串流中途失敗");
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>
/// IChatIdentityAccessor fake:沒有真實 HttpContext 可讀,CurrentUser 由呼叫端經
/// <see cref="SetRequestKeys"/> 傳入的 userCtx 決定(模擬單一 fake 實例跨多輪呼叫時的身分切換,
/// 例如 A20/LoggedIn_MemoryKeys 這類同一個 ChatService 實例、不同 userCtx 連續呼叫的既有測試)。
/// DeriveMemoryKeys() 邏輯與 Web 層 HttpChatIdentityAccessor 共用同一個純函式(<see cref="ChatMemoryKeyDerivation"/>),
/// 不重複維護。
/// </summary>
public sealed class FakeChatIdentityAccessor : IChatIdentityAccessor
{
    private string? _userId;
    private string? _conversationId;

    public UserContext? CurrentUser { get; private set; }
    public Exception? PersistFailure { get; set; }
    public ChatResponse? PersistedResponse { get; set; }

    public void SetRequestKeys(string? userId, string? conversationId, UserContext? userCtx)
    {
        _userId = userId;
        _conversationId = conversationId;
        CurrentUser = userCtx;
        PersistFailure = null;
        PersistedResponse = null;
    }

    public (string Uid, string Cid) DeriveMemoryKeys()
        => ChatMemoryKeyDerivation.Derive(_userId, _conversationId, CurrentUser);
}

/// <summary>
/// 組出 P4 的共用「ChatAssistant」hosted agent(無 copilotInstructions,見 ChatContextProvider 拓樸注記):
/// 一顆 <see cref="InMemoryChatHistoryProvider"/>(20 則視窗,MessagesExceed(20) 觸發,
/// minimumPreservedTurns=1)+ 一顆 <see cref="InMemoryAgentSessionStore"/>(service 層單元測試直接以
/// 顯式 UserContext 驅動不同 cid,不需要 IsolationKeyScopedAgentSessionStore 這層——租戶/使用者隔離
/// 已经在 DeriveMemoryKeys 產生的 cid 字串裡)。
/// pipeline(外→內):ChatTurnRecorder → SkillRoutingAgent → ChatClientAgent(掛 ChatContextProvider)。
/// 三者皆需要 IServiceScopeFactory 解析 per-call 的 IMem0Client/IConversationStore/IChatIdentityAccessor/
/// IWorkflowService(生產環境兩顆 hosted agent 是啟動期 Singleton,不可在建構時捕捉 Scoped 服務);
/// 測試以最小 ServiceCollection 組一個真正的 scope factory,讓傳入的 mem0/convos/identity/workflows
/// fake 實例可被解析到。llmAgent 是 SkillRoutingAgent 路由/摘要用的「裸」<see cref="ILlmAgent"/>
/// (P4 前由 ChatService 持有,P4 後搬進 SkillRoutingAgent 建構時直接傳入——它是 Singleton,不需經 scope)。
/// </summary>
internal static class TestChatAgent
{
    public static (AIHostAgent HostAgent, InMemoryChatHistoryProvider HistoryProvider, SkillRoutingAgent Routing) Build(
        FakeChatClient? chatClient = null,
        FakeMem0Client? mem0 = null,
        FakeConversationStore? convos = null,
        FakeChatIdentityAccessor? identity = null,
        FakeLlmAgent? llmAgent = null,
        FakeWorkflowService? workflows = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMem0Client>(mem0 ?? new FakeMem0Client());
        services.AddSingleton<IConversationStore>(convos ?? new FakeConversationStore());
        services.AddSingleton<IChatIdentityAccessor>(identity ?? new FakeChatIdentityAccessor());
        services.AddSingleton<IWorkflowService>(workflows ?? new FakeWorkflowService());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new SlidingWindowCompactionStrategy(
                    trigger: CompactionTriggers.MessagesExceed(20),
                    minimumPreservedTurns: 1)
                .AsChatReducer(),
        });
        var chatClientAgent = (chatClient ?? new FakeChatClient()).AsAIAgent(new ChatClientAgentOptions
        {
            Name = "ChatAssistant",
            ChatHistoryProvider = historyProvider,
            AIContextProviders = new AIContextProvider[]
            {
                new ChatContextProvider(scopeFactory, NullLogger<ChatContextProvider>.Instance),
            },
        });
        var routing = new SkillRoutingAgent(
            chatClientAgent, llmAgent ?? new FakeLlmAgent(), historyProvider, scopeFactory,
            NullLogger<SkillRoutingAgent>.Instance);
        var recorder = new ChatTurnRecorder(routing, scopeFactory, NullLogger<ChatTurnRecorder>.Instance);
        var hostAgent = new AIHostAgent(recorder, new InMemoryAgentSessionStore());
        return (hostAgent, historyProvider, routing);
    }
}

/// <summary>可控回應、可捕捉最後一次請求(含 body)的 HttpMessageHandler,用來測下游 client。</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => _responder = responder;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _responder(request);
    }

    public string Header(string name) => LastRequest!.Headers.GetValues(name).Single();

    public bool HasHeader(string name) => LastRequest!.Headers.Contains(name);
}
