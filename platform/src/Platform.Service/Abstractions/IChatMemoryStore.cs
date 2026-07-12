namespace Platform.Service.Abstractions;

/// <summary>
/// 短期記憶(對應原 Spring AI 的 MessageWindowChatMemory):依 conversationId 保存最近 20 則訊息,
/// 存在記憶體、重啟即清空。
/// </summary>
public interface IChatMemoryStore
{
    /// <summary>取得某對話目前的訊息視窗快照(不含本輪尚未寫入的訊息)。</summary>
    IReadOnlyList<LlmMessage> GetRecent(string conversationId);

    /// <summary>追加一則訊息;超過 20 則時從最舊裁掉。</summary>
    void Append(string conversationId, LlmMessage message);
}
