namespace Platform.Service.Abstractions;

/// <summary>
/// mem0 長期記憶 client。所有錯誤都吞掉(記憶是加分項,絕不讓聊天失敗):
/// <see cref="RecallAsync"/> 出錯回空字串、<see cref="RememberAsync"/> 出錯直接返回。
/// </summary>
public interface IMem0Client
{
    /// <summary>依 userId 與查詢字串取回長期記憶,組成 "- memory\n" 串接字串;無記憶或出錯回 ""。</summary>
    Task<string> RecallAsync(string userId, string query, CancellationToken ct = default);

    /// <summary>把本輪 user 訊息與 AI 回覆寫入長期記憶;出錯直接跳過。</summary>
    Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default);
}
