using Backend.Api.Conversations;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// 對話儲存庫的行程記憶體實作(Lite 模式)。以 (tenant_id, user_id) 忠實模擬隔離;
/// 遞增 id 作穩定平手判準。執行緒安全:寫入與列舉皆以 lock 保護。
/// </summary>
public sealed class InMemoryConversationRepository : IConversationRepository
{
    private readonly List<(string Tenant, string User, ConversationItem Item)> _items = new();
    private long _seq;
    private readonly Lock _lockObj = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryConversationRepository(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<ConversationCreated> AddAsync(string tenantId, string userId, string prompt, string reply, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var id = ++_seq;
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            _items.Add((tenantId, userId, new ConversationItem(id, reply, now)));
            return Task.FromResult(new ConversationCreated(id, now));
        }
    }

    public Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct)
    {
        lock (_lockObj)
        {
            return Task.FromResult<IReadOnlyList<ConversationItem>>(
                // W2-06:與 Dapper 的 LIMIT 500 同一個常數,兩邊都封頂在最新 500 筆。
                _items.Where(e => e.Tenant == tenantId && e.User == userId).Select(e => e.Item)
                    .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
                    .Take(IConversationRepository.MaxHistoryItems).ToList());
        }
    }

    public Task<IReadOnlyList<ConversationItem>> ListPageDescAsync(
        string tenantId,
        string userId,
        ConversationPosition? before,
        int take,
        CancellationToken ct)
    {
        lock (_lockObj)
        {
            var query = _items
                .Where(e => e.Tenant == tenantId && e.User == userId)
                .Select(e => e.Item);
            if (before is not null)
            {
                query = query.Where(i => i.CreatedAt < before.CreatedAt
                    || i.CreatedAt == before.CreatedAt && i.Id < before.Id);
            }

            return Task.FromResult<IReadOnlyList<ConversationItem>>(query
                .OrderByDescending(i => i.CreatedAt)
                .ThenByDescending(i => i.Id)
                .Take(take)
                .ToList());
        }
    }
}
