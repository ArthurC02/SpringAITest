using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Service.Abstractions;

namespace Platform.Service;

/// <summary>
/// 兩條鏈路共用的「一輪結束後」副作用:mem0 remember + 對話持久化。掛在 <see cref="SkillRoutingAgent"/>
/// middleware 之外、<c>ChatClientAgent</c> 之外(P4,03-design.md §3.3),讓路由命中而短路的那一輪
/// 也會被記住——路由/摘要那一輪走裸 <see cref="ILlmAgent"/>、完全不經過 <c>ChatClientAgent</c>,
/// 若本類掛在 <see cref="SkillRoutingAgent"/> 之內就會漏記那一輪,故必須在其外側。
///
/// persist(<see cref="IConversationStore.AddAsync"/>)先跑、remember(mem0)後跑;persist 失敗時是否仍
/// remember,阻塞與串流兩條路徑逐字對齊 P3 前 <c>ChatService</c> 基線(git 16aa253)——P3 review 發現先前
/// 版本誤把「remember 無條件先於 persist 執行」套用到兩條路徑,已修正回基線順序:
/// - 阻塞(<see cref="RunCoreAsync"/>):基線的 AddAsync 沒有 try/catch,拋出時直接中止該次呼叫、
///   RememberAsync 永遠不會執行。本類以「訊號式」<see cref="IChatIdentityAccessor.PersistFailure"/>
///   取代例外傳播,但語意對齊:persist 失敗 → 記警告、寫入 PersistFailure、直接 return,不 remember;
///   只有 persist 成功才 remember。
/// - 串流(<see cref="RunCoreStreamingAsync"/>):基線的 AddAsync 包一層 try/catch(best-effort,只記
///   warning),RememberAsync 緊接在 try/catch 之後、不受 persist 結果影響、一律執行。
///
/// 匿名(<see cref="IChatIdentityAccessor.CurrentUser"/> 為 null)兩條路徑都不做副作用:保留短期 session
/// continuity，但不持久化、也不讀寫 mem0，避免 caller-controlled anonymous uid 造成跨訪客資料污染。
///
/// 阻塞路徑要不要升級成 500 不是本類的職責:本類把失敗訊號留在
/// <see cref="IChatIdentityAccessor.PersistFailure"/>,由呼叫阻塞端點的 <c>ChatService.ChatAsync</c>
/// 決定(§9.4)。成功時把 backend 產生的 <see cref="Dtos.ChatResponse"/>(含 Id/CreatedAt)寫進
/// <see cref="IChatIdentityAccessor.PersistedResponse"/>——本類跑在 agent pipeline 內部,
/// <c>ChatService.ChatAsync</c> 本身拿不到 <see cref="IConversationStore.AddAsync"/> 的回傳值,只能靠這個
/// 管道取回持久化後的 Id 組回傳給呼叫端。
///
/// 兩顆 hosted agent 是啟動期建立的 Singleton,本類不可在建構時捕捉 Scoped 服務(<see cref="IMem0Client"/>、
/// <see cref="IConversationStore"/>、<see cref="IChatIdentityAccessor"/>)——改持
/// <see cref="IServiceScopeFactory"/>,每次呼叫時開新 scope、用完即棄。
/// </summary>
public sealed class ChatTurnRecorder : DelegatingAIAgent
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatTurnRecorder> _logger;

    public ChatTurnRecorder(AIAgent innerAgent, IServiceScopeFactory scopeFactory, ILogger<ChatTurnRecorder> logger)
        : base(innerAgent)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        var lastUser = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

        // 委派完整回覆(含工具融合結果)不包在 try/catch 內——例外必須原樣傳播到 ChatController,
        // 補一個 event:error 終止幀(§9.3,本類絕不吞掉會導致該幀產生的例外)。
        var response = await InnerAgent.RunAsync(messageList, session, options, cancellationToken);

        await RecordTurnAsync(lastUser, response.Text ?? string.Empty, rememberEvenIfPersistFails: false, cancellationToken);

        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messageList = messages.ToList();
        var lastUser = messageList.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var accumulated = new StringBuilder();

        await foreach (var update in InnerAgent.RunStreamingAsync(messageList, session, options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                accumulated.Append(update.Text);
            }

            yield return update;
        }

        // 只有在 await foreach 正常結束(沒有例外冒出)才會執行到這裡——串流中途失敗時例外直接穿透
        // 這個方法,以下副作用完全不會執行(半截回覆不得持久化,A-17/T-P4-4;§9.3:try/catch 只包
        // AddAsync,絕不包 await foreach 或委派呼叫本身)。
        await RecordTurnAsync(lastUser, accumulated.ToString(), rememberEvenIfPersistFails: true, cancellationToken);
    }

    /// <summary>
    /// persist(AddAsync)+ remember(mem0)。<paramref name="rememberEvenIfPersistFails"/> 是阻塞/串流兩條
    /// 路徑唯一的行為差異點,逐字對齊 P3 前 ChatService 基線(見類別頂端文件):
    /// false(阻塞,<see cref="RunCoreAsync"/>呼叫):persist 失敗 → 記警告 + 寫入 PersistFailure,直接
    /// return,不 remember。true(串流,<see cref="RunCoreStreamingAsync"/>呼叫):persist 失敗一樣記警告 +
    /// 寫入 PersistFailure,但接著仍 remember(best-effort,不受影響)。
    /// </summary>
    private async Task RecordTurnAsync(string userMessage, string reply, bool rememberEvenIfPersistFails, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IChatIdentityAccessor>();
        var userCtx = identity.CurrentUser;
        if (userCtx is null)
        {
            // 匿名只保留 ChatService 的短期 session，沒有可安全歸屬的長期 identity。
            return;
        }

        var (uid, _) = identity.DeriveMemoryKeys();
        var mem0 = scope.ServiceProvider.GetRequiredService<IMem0Client>();

        var conversations = scope.ServiceProvider.GetRequiredService<IConversationStore>();

        if (rememberEvenIfPersistFails)
        {
            // 串流路徑基線:persist best-effort(失敗只記 warning),remember 不受 persist 結果影響、
            // 一律接著執行。
            try
            {
                identity.PersistedResponse = await conversations.AddAsync(userMessage, reply, userCtx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "串流聊天持久化失敗（串流已送出,略過）：{訊息}", ex.Message);
                identity.PersistFailure = ex;
            }

            await RememberBestEffortAsync(mem0, uid, userMessage, reply, ct);
            return;
        }

        // 阻塞路徑基線:AddAsync 沒有 try/catch,拋出時 RememberAsync 永遠不會執行——本類以訊號式
        // PersistFailure 取代例外傳播,語意對齊:persist 失敗 → 記警告、寫入 PersistFailure、直接
        // return,不 remember;只有 persist 成功才 remember。
        try
        {
            identity.PersistedResponse = await conversations.AddAsync(userMessage, reply, userCtx, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "聊天持久化失敗（阻塞路徑由呼叫端決定是否升級為 500）：{訊息}", ex.Message);
            identity.PersistFailure = ex;
            return;
        }

        await RememberBestEffortAsync(mem0, uid, userMessage, reply, ct);
    }

    private async Task RememberBestEffortAsync(
        IMem0Client mem0, string uid, string userMessage, string reply, CancellationToken ct)
    {
        try
        {
            await mem0.RememberAsync(uid, userMessage, reply, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "mem0 remember 失敗，略過長期記憶：{Message}", ex.Message);
        }
    }
}
