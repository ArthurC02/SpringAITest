namespace Backend.Api.Conversations;

/// <summary>對話紀錄資料存取(薄介面,供測試換 fake)。聊天歷史是全域的,不分租戶/使用者。</summary>
public interface IConversationRepository
{
    Task<ConversationCreated> AddAsync(string prompt, string reply, CancellationToken ct);

    /// <summary>依 created_at DESC(最新在前),同時間戳以 id DESC 作穩定平手判準。</summary>
    Task<IReadOnlyList<ConversationItem>> ListDescAsync(CancellationToken ct);
}
