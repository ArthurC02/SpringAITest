namespace Backend.Api.Conversations;

/// <summary>對話紀錄資料存取(薄介面,供測試換 fake)。以 (tenant_id, user_id) 隔離。</summary>
public interface IConversationRepository
{
    Task<ConversationCreated> AddAsync(string tenantId, string userId, string prompt, string reply, CancellationToken ct);

    /// <summary>
    /// W2-06 上限:deprecated 全量歷史單次最多回傳這麼多筆(最新的在前)。兩個實作共用同一個常數,
    /// 抄兩份就會悄悄漂移。決策記錄:plans/wave2-decisions-2026-08-10.md。
    /// </summary>
    public const int MaxHistoryItems = 500;

    /// <summary>
    /// 依 created_at DESC(最新在前),同時間戳以 id DESC 作穩定平手判準;只回同租戶同使用者的紀錄。
    /// **Deprecated**:回應封頂為最新 <see cref="MaxHistoryItems"/> 筆,更舊的訊息不再回傳;
    /// 需要完整歷史請改用 <see cref="ListPageDescAsync"/>。
    /// </summary>
    Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct);

    /// <summary>Keyset query in the same stable order; <paramref name="take"/> is already limit + 1.</summary>
    Task<IReadOnlyList<ConversationItem>> ListPageDescAsync(
        string tenantId,
        string userId,
        ConversationPosition? before,
        int take,
        CancellationToken ct);
}
