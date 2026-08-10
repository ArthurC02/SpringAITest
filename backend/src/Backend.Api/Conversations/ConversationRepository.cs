using Dapper;
using Npgsql;

namespace Backend.Api.Conversations;

/// <summary>以 Dapper + Npgsql 實作對話紀錄存取。</summary>
public sealed class ConversationRepository : IConversationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ConversationRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ConversationCreated> AddAsync(string tenantId, string userId, string prompt, string reply, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<ConversationCreated>(
            new CommandDefinition(
                "INSERT INTO conversations (tenant_id, user_id, prompt, reply) VALUES (@tenantId, @userId, @prompt, @reply)"
                + " RETURNING id, created_at AS CreatedAt",
                new { tenantId, userId, prompt, reply }, cancellationToken: ct));
    }

    /// <summary>
    /// Deprecated 全量歷史(<c>GET /api/chat/history</c>)。W2-06 決策:回應上限封頂為最新
    /// <see cref="MaxHistoryItems"/> 筆——這是**刻意的語意變更**,超過上限的舊訊息不再回傳,單次
    /// 成本因此有界。新呼叫方請改用 keyset 分頁版 <see cref="ListPageDescAsync"/>
    /// (<c>GET /api/chat/history/page</c>);此方法保留只為既有呼叫方相容,不發
    /// <c>Deprecation</c>/<c>Sunset</c> header(移除日期是產品承諾,不由工程單方面寫進 wire 契約)。
    /// 決策記錄:plans/wave2-decisions-2026-08-10.md。
    /// </summary>
    public async Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationItem>(
            new CommandDefinition(
                "SELECT id, reply, created_at AS CreatedAt FROM conversations"
                + " WHERE tenant_id = @tenantId AND user_id = @userId"
                + " ORDER BY created_at DESC, id DESC LIMIT @limit",
                new { tenantId, userId, limit = IConversationRepository.MaxHistoryItems }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ConversationItem>> ListPageDescAsync(
        string tenantId,
        string userId,
        ConversationPosition? before,
        int take,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var sql = "SELECT id, reply, created_at AS CreatedAt FROM conversations"
            + " WHERE tenant_id = @tenantId AND user_id = @userId";
        if (before is not null)
        {
            sql += " AND (created_at, id) < (@beforeCreatedAt, @beforeId)";
        }
        sql += " ORDER BY created_at DESC, id DESC LIMIT @take";
        var rows = await conn.QueryAsync<ConversationItem>(
            new CommandDefinition(
                sql,
                new
                {
                    tenantId,
                    userId,
                    beforeCreatedAt = before?.CreatedAt,
                    beforeId = before?.Id,
                    take,
                },
                cancellationToken: ct));
        return rows.AsList();
    }
}
