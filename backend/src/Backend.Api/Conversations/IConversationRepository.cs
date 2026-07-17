namespace Backend.Api.Conversations;

/// <summary>對話紀錄資料存取(薄介面,供測試換 fake)。以 (tenant_id, user_id) 隔離。</summary>
public interface IConversationRepository
{
    Task<ConversationCreated> AddAsync(string tenantId, string userId, string prompt, string reply, CancellationToken ct);

    /// <summary>依 created_at DESC(最新在前),同時間戳以 id DESC 作穩定平手判準;只回同租戶同使用者的紀錄。</summary>
    Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct);
}
