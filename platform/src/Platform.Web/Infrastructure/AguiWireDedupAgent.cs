using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Platform.Web.Infrastructure;

/// <summary>
/// P2 修復 B-P2-04(AG-UI 多輪訊息重複累加,copilot-shared-core):真實 @ag-ui/client 每輪必定重送「完整」
/// messages 陣列(client 端維護的全歷史;反編譯 <c>prepareRunAgentInput</c> 證實)。而
/// <see cref="ChatHistoryProvider"/> 的預設合併語意(反編譯 <c>ChatHistoryProvider.InvokingCoreAsync</c>
/// 證實)是把 wire 傳入的 messages 整段疊在 session 已存歷史後面,框架本身完全不做去重。兩者相乘,
/// 送進模型的訊息數隨輪次複合暴增(1 → 5 → 11……,同一句話第三輪出現 3 次)。
/// 只包在 AG-UI 這顆 "OperationsAssistant" agent 外層(ChatController→ChatService 的 "ChatAssistant"
/// 鏈路 wire 上只送單一新訊息,不受影響,不需要也不應該套這層)。對 wire 傳入的 messages 做一次
/// 「已存在於 session 歷史」的過濾,只留真正新訊息再委派給真正的 ChatClientAgent——它自己的
/// ChatHistoryProvider 疊加/sliding-window 邏輯完全不動。
/// 比對規則:優先用 MessageId(AGUI wire 的 id 逐輪原樣寫進 <see cref="ChatMessage.MessageId"/>,反編譯
/// <c>AGUIChatMessageExtensions.AsChatMessages</c> 證實)。MessageId 為 null/空一律保留、不比對內容——
/// 避免使用者連續發送兩則內容相同但 id 不同的訊息被誤判為重複而遺失。若前端echo回的 assistant id
/// 與伺服器原先送出的不一致(e2e 觀察到的另一種真實情況),該則 assistant 訊息就不會被濾掉而重覆一次,
/// 但不影響 user 訊息去重,增長仍是線性而非複合暴增。
/// </summary>
public sealed class AguiWireDedupAgent : DelegatingAIAgent
{
    private readonly InMemoryChatHistoryProvider _chatHistoryProvider;

    public AguiWireDedupAgent(AIAgent innerAgent, InMemoryChatHistoryProvider chatHistoryProvider)
        : base(innerAgent)
        => _chatHistoryProvider = chatHistoryProvider;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunAsync(DropAlreadyKnownMessages(messages, session), session, options, cancellationToken);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunStreamingAsync(DropAlreadyKnownMessages(messages, session), session, options, cancellationToken);

    private List<ChatMessage> DropAlreadyKnownMessages(IEnumerable<ChatMessage> messages, AgentSession? session)
    {
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stored in _chatHistoryProvider.GetMessages(session))
        {
            if (!string.IsNullOrEmpty(stored.MessageId))
            {
                knownIds.Add(stored.MessageId);
            }
        }

        return messages
            .Where(m => string.IsNullOrEmpty(m.MessageId) || !knownIds.Contains(m.MessageId))
            .ToList();
    }
}
