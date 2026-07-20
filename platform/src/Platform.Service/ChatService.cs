using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Options;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.Logging;
// Microsoft.Extensions.AI 也定義 ChatResponse;本檔的 ChatResponse 一律指 Dtos 版(對外 DTO)。
using ChatResponse = Platform.Service.Dtos.ChatResponse;

namespace Platform.Service;

/// <summary>
/// 聊天服務(P4 收斂後,copilot-shared-core plans/copilot-shared-core/03-design.md §7):skill 路由
/// (<see cref="SkillRoutingAgent"/>)、護欄 + mem0 recall(<see cref="ChatContextProvider"/>)、
/// mem0 remember + 對話持久化(<see cref="ChatTurnRecorder"/>)、短期記憶視窗(<see cref="InMemoryChatHistoryProvider"/>)
/// 全部收斂進 <see cref="AIHostAgent"/> 的 pipeline(外→內:ChatTurnRecorder → SkillRoutingAgent →
/// ChatClientAgent)。本類只剩傳輸層職責:SetRequestKeys → 開 session → 跑 host agent → 存 session →
/// PersistFailure 升級(僅阻塞路徑,§9.4)。
///
/// 持久化與歷史改走 backend(<see cref="IConversationStore"/>),以 (tenant_id, user_id) 隔離;匿名
/// (userCtx null)不持久化、歷史直接回空清單。mem0 uid/cid 的推導、已登入時忽略 body userId 的防 IDOR
/// 語意,皆由 <see cref="IChatIdentityAccessor"/> 提供(<see cref="ChatMemoryKeyDerivation"/> 為其底層純函式)。
/// </summary>
public sealed class ChatService : IChatService
{
    // 業務 span 的 ActivitySource;名稱要與 OTel 註冊的 source 名一致。
    private static readonly ActivitySource ActivitySource = new("chat.service");

    private readonly AIHostAgent _hostAgent;
    private readonly IConversationStore _conversations;
    private readonly IChatIdentityAccessor _identity;
    private readonly LlmOptions _llmOptions;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        AIHostAgent hostAgent,
        IConversationStore conversations,
        IChatIdentityAccessor identity,
        LlmOptions llmOptions,
        ILogger<ChatService> logger)
    {
        _hostAgent = hostAgent;
        _conversations = conversations;
        _identity = identity;
        _llmOptions = llmOptions;
        _logger = logger;
    }

    public async Task<ChatResponse> ChatAsync(string message, string? userId, string? conversationId, UserContext? userCtx = null, CancellationToken ct = default)
    {
        _identity.SetRequestKeys(userId, conversationId, userCtx);
        var (_, cid) = _identity.DeriveMemoryKeys();

        using var activity = StartSpan(message);
        try
        {
            var session = await _hostAgent.GetOrCreateSessionAsync(cid, ct);

            // 路由(命中/未命中)、護欄+mem0 recall、mem0 remember+持久化皆由 hosted agent pipeline 接手
            // (SkillRoutingAgent / ChatContextProvider / ChatTurnRecorder)。
            var response = await _hostAgent.RunAsync(message, session, cancellationToken: ct);
            var reply = response.Text ?? string.Empty;

            activity?.SetTag("completion.length", reply.Length);

            await _hostAgent.SaveSessionAsync(cid, session, ct);

            // 阻塞路徑的持久化承諾「回 200 就代表存到了」;ChatTurnRecorder 一律 best-effort,
            // 失敗訊號留在 PersistFailure,由本端點決定升級成 500(現行語意,§9.4)。
            if (_identity.PersistFailure is { } persistEx)
            {
                ExceptionDispatchInfo.Capture(persistEx).Throw();
            }

            return _identity.PersistedResponse ?? new ChatResponse(0, reply, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string message, string? userId, string? conversationId, UserContext? userCtx = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _identity.SetRequestKeys(userId, conversationId, userCtx);
        var (_, cid) = _identity.DeriveMemoryKeys();

        // 刻意手動管理 span 生命週期(對應原 Java streamChat 不是 @Transactional、手動 start/stop):
        // 串流在訂閱時才執行,持久化發生在串流結束後、仍在本請求範圍內。
        var activity = StartSpan(message);
        var accumulated = new StringBuilder();
        try
        {
            // GetOrCreateSessionAsync 的 await 放在建 enumerator 之前(A-17 釘住:例外語意不變)。
            var session = await _hostAgent.GetOrCreateSessionAsync(cid, ct);

            // 手動列舉以便在串流出錯時替 span 記 error(yield 不能放在 try/catch 內)。
            var enumerator = _hostAgent.RunStreamingAsync(message, session, cancellationToken: ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    string? text;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }
                        text = enumerator.Current.Text;
                    }
                    catch (Exception ex)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        throw;
                    }

                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    accumulated.Append(text);
                    yield return text;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            // 串流正常結束:記 completion.length、存回 session。remember/持久化已由 ChatTurnRecorder 在
            // await foreach 之後(pipeline 內部)best-effort 處理,本類不重複執行、也不檢查 PersistFailure
            // (串流路徑不升級,A-16)。
            activity?.SetTag("completion.length", accumulated.Length);

            await _hostAgent.SaveSessionAsync(cid, session, ct);
        }
        finally
        {
            // 無論成功或失敗,最後都要停止 span。
            activity?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ChatResponse>> HistoryAsync(UserContext? userCtx = null, CancellationToken ct = default)
    {
        // 匿名沒有身分可歸屬,直接回空清單,不打 backend。
        if (userCtx is null)
        {
            return Array.Empty<ChatResponse>();
        }

        return await _conversations.ListDescAsync(userCtx, ct);
    }

    private Activity? StartSpan(string message)
    {
        // 沒有任何 OTel listener 時 StartActivity 回 null(例如單元測試),此時 span 為 no-op,不影響流程。
        var activity = ActivitySource.StartActivity("chat-service", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.SetTag("gen_ai.system", "openai");
            activity.SetTag("gen_ai.request.model", _llmOptions.ChatModel);
            activity.SetTag("prompt.length", message.Length);
        }

        return activity;
    }
}
