namespace Platform.Service.Abstractions;

/// <summary>
/// mem0 長期記憶 client。正式實作應自行採 best-effort（recall 回空、remember no-op）；
/// shared chat pipeline 另有最後一道防線，確保替換實作擲例外也不會中斷聊天。
/// </summary>
public interface IMem0Client
{
    /// <summary>依 userId 與查詢字串取回長期記憶,組成 "- memory\n" 串接字串;無記憶或出錯回 ""。</summary>
    Task<string> RecallAsync(string userId, string query, CancellationToken ct = default);

    /// <summary>把本輪 user 訊息與 AI 回覆寫入長期記憶;出錯直接跳過。</summary>
    Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default);
}
