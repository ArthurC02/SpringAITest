using System.Runtime.CompilerServices;
using Platform.Service;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service.Tests;

/// <summary>建 BackendClient(套在 StubHttpMessageHandler 上)的共用工廠。</summary>
internal static class TestBackend
{
    public static BackendClient Client(StubHttpMessageHandler stub, string baseUrl = "http://backend", string token = "tok")
        => new(new HttpClient(stub), new BackendOptions { BaseUrl = baseUrl, InternalToken = token });
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
    public List<ChatResponse> Items { get; } = new();
    public bool ThrowOnAdd { get; set; }
    private long _nextId = 1;

    public Task<ChatResponse> AddAsync(string prompt, string reply, CancellationToken ct = default)
    {
        if (ThrowOnAdd)
        {
            throw new BackendCallException("模擬持久化失敗");
        }

        Saved.Add((prompt, reply));
        return Task.FromResult(new ChatResponse(_nextId++, reply, DateTime.UtcNow));
    }

    public Task<IReadOnlyList<ChatResponse>> ListDescAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ChatResponse>>(
            Items.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToList());
}

/// <summary>可控回覆的 LLM 代理 fake;會記下最後一次收到的訊息列與工具列以供斷言。</summary>
public sealed class FakeLlmAgent : ILlmAgent
{
    public string Response { get; set; } = "測試回覆";
    public IReadOnlyList<string> Chunks { get; set; } = new[] { "你好", "世界" };
    public IReadOnlyList<LlmMessage>? LastMessages { get; private set; }
    public IReadOnlyList<LlmTool>? LastTools { get; private set; }

    /// <summary>非 null 時:吐出第 N 塊後擲例外(模擬串流中途失敗),用來驗半截回覆不持久化。</summary>
    public int? ThrowAfterChunks { get; set; }

    public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, CancellationToken ct)
    {
        LastMessages = messages;
        LastTools = tools;
        return Task.FromResult(Response);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<LlmMessage> messages, IReadOnlyList<LlmTool>? tools, [EnumeratorCancellation] CancellationToken ct)
    {
        LastMessages = messages;
        LastTools = tools;
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

/// <summary>工作流 fake:可控 answer、可模擬失敗、可覆寫 kb_query 輸出;記下呼叫序供斷言。</summary>
public sealed class FakeWorkflowService : IWorkflowService
{
    public string Answer { get; set; } = "知識庫答案";
    public Exception? ThrowOnInvoke { get; set; }

    /// <summary>非 null 時:kb_query 回這包輸出(其餘工作流照舊回 answer),用來測棄答兜底。</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? KbQueryOutput { get; set; }

    public List<(string Name, Dictionary<string, System.Text.Json.JsonElement> Input, UserContext Ctx)> Invokes { get; } = new();
    public (string Name, Dictionary<string, System.Text.Json.JsonElement> Input, UserContext Ctx)? LastInvoke
        => Invokes.Count > 0 ? Invokes[^1] : null;

    public Task<IReadOnlyList<WorkflowInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkflowInfo>>(Array.Empty<WorkflowInfo>());

    public Task<WorkflowInvokeResponse> InvokeAsync(
        string name, Dictionary<string, System.Text.Json.JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        if (ThrowOnInvoke is not null)
        {
            throw ThrowOnInvoke;
        }

        Invokes.Add((name, input, ctx));
        if (name == "kb_query" && KbQueryOutput is not null)
        {
            return Task.FromResult(new WorkflowInvokeResponse(name, KbQueryOutput));
        }

        var output = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["answer"] = System.Text.Json.JsonSerializer.SerializeToElement(Answer),
        };
        return Task.FromResult(new WorkflowInvokeResponse(name, output));
    }
}

/// <summary>mem0 client fake:可設定 recall 回傳、記錄 remember 呼叫。</summary>
public sealed class FakeMem0Client : IMem0Client
{
    public string RecallResult { get; set; } = string.Empty;
    public List<(string UserId, string UserMessage, string AiReply)> Remembered { get; } = new();

    public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
        => Task.FromResult(RecallResult);

    public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
    {
        Remembered.Add((userId, userMessage, aiReply));
        return Task.CompletedTask;
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
