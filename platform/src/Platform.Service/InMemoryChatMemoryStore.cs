using System.Collections.Concurrent;
using Platform.Service.Abstractions;

namespace Platform.Service;

/// <summary>
/// 短期記憶的記憶體實作(對應原 MessageWindowChatMemory):
/// 依 conversationId 保存最近 20 則訊息(user + assistant 都算一則),超過從最舊裁掉。
/// 以 per-list lock 保證執行緒安全;註冊為 Singleton。
/// </summary>
public sealed class InMemoryChatMemoryStore : IChatMemoryStore
{
    private const int MaxMessages = 20;

    private readonly ConcurrentDictionary<string, List<LlmMessage>> _store = new();

    public IReadOnlyList<LlmMessage> GetRecent(string conversationId)
    {
        if (!_store.TryGetValue(conversationId, out var list))
        {
            return Array.Empty<LlmMessage>();
        }

        // 回傳快照:避免呼叫端列舉期間,另一個執行緒同時 Append 造成例外。
        lock (list)
        {
            return list.ToArray();
        }
    }

    public void Append(string conversationId, LlmMessage message)
    {
        var list = _store.GetOrAdd(conversationId, _ => new List<LlmMessage>());
        lock (list)
        {
            list.Add(message);
            // 滑動視窗:只留最近 20 則。
            while (list.Count > MaxMessages)
            {
                list.RemoveAt(0);
            }
        }
    }
}
