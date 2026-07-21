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
/// <c>AGUIChatMessageExtensions.AsChatMessages</c> 證實)。若前端 echo 回來的純文字 assistant id 與伺服器
/// 原先送出的不一致，才用 role + 完整文字作為保守 fallback。使用者永遠只按 id 去重，故合法的「同內容、
/// 不同 id」重複發話不會遺失。含 function call/result 的訊息絕不走文字 fallback，避免把工具配對拆散；
/// 它們仍以 id 原樣處理。
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
        var knownAssistantTextCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stored in _chatHistoryProvider.GetMessages(session))
        {
            if (!string.IsNullOrEmpty(stored.MessageId))
            {
                knownIds.Add(stored.MessageId);
            }

            var fingerprint = GetAssistantTextFingerprint(stored);
            if (fingerprint is not null)
            {
                knownAssistantTextCounts[fingerprint] = knownAssistantTextCounts.GetValueOrDefault(fingerprint) + 1;
            }
        }

        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (!string.IsNullOrEmpty(message.MessageId) && knownIds.Contains(message.MessageId))
            {
                continue;
            }

            var fingerprint = GetAssistantTextFingerprint(message);
            if (fingerprint is not null && knownAssistantTextCounts.TryGetValue(fingerprint, out var remaining)
                && remaining > 0)
            {
                // A mismatched assistant id can still echo an old turn. Consume exactly one stored occurrence;
                // do not use a set, because a later legitimate assistant message may have identical text.
                knownAssistantTextCounts[fingerprint] = remaining - 1;
                continue;
            }

            result.Add(message);
        }

        return result;
    }

    private static string? GetAssistantTextFingerprint(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant || string.IsNullOrEmpty(message.Text))
        {
            return null;
        }

        // A plain text assistant message is safe to compare by text. Any non-text content can be a tool call/result;
        // require its original id so a partial fallback can never create an orphaned tool exchange.
        return message.Contents is { Count: > 0 } && message.Contents.All(content => content is TextContent)
            ? message.Text
            : null;
    }
}
