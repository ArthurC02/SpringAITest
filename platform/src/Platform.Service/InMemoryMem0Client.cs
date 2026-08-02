using System.Text;
using Platform.Service.Abstractions;

namespace Platform.Service;

/// <summary>
/// Lite 模式長期記憶實作:per-user 訊息對列表 (userMessage, aiReply),每個 user 最多 100 對。
/// 執行緒安全:所有讀寫皆在單一 lock 下(規模小,無需 reader/writer 分離)。
/// 錯誤契約:零異常(Recall 無結果回 "",Remember 無效輸入為 no-op),與 Mem0Client 一致。
/// 重啟即失憶(記憶體回收),符合 Lite 演示定位。
/// </summary>
public sealed class InMemoryMem0Client : IMem0Client
{
    private const int MaxMemoriesPerUser = 100;

    // ponytail: 全部存取都在 _lock 下,普通 Dictionary 即執行緒安全,不需 ConcurrentDictionary。
    private readonly Dictionary<string, List<(string UserMsg, string AiReply)>> _memories = new();
    private readonly Lock _lock = new();

    public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(string.Empty);
        }

        lock (_lock)
        {
            if (!_memories.TryGetValue(userId, out var pairs) || pairs.Count == 0)
            {
                return Task.FromResult(string.Empty);
            }

            // 最新優先:由尾向前掃描,關鍵字 case-insensitive 比對 userMsg 或 aiReply。
            var sb = new StringBuilder();
            for (var i = pairs.Count - 1; i >= 0; i--)
            {
                var (userMsg, aiReply) = pairs[i];
                if (userMsg.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || aiReply.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("- ").Append(userMsg).Append('\n');
                    sb.Append("- ").Append(aiReply).Append('\n');
                }
            }

            // 格式:"- userMsg1\n- aiReply1\n- userMsg2\n- aiReply2"(無尾端換行);無符合項回 ""。
            return Task.FromResult(sb.ToString().TrimEnd('\n'));
        }
    }

    public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        // 兩者皆空白才 no-op(規格 §3.1:userMessage/aiReply 皆為 null 或空);單邊有內容仍記錄。
        if (string.IsNullOrWhiteSpace(userMessage) && string.IsNullOrWhiteSpace(aiReply))
        {
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            if (!_memories.TryGetValue(userId, out var pairs))
            {
                pairs = new List<(string, string)>();
                _memories[userId] = pairs;
            }

            pairs.Add((userMessage, aiReply));
            if (pairs.Count > MaxMemoriesPerUser)
            {
                pairs.RemoveAt(0); // 移除最舊
            }
        }

        return Task.CompletedTask;
    }
}
