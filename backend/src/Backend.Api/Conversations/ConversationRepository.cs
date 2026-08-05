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

    public async Task<IReadOnlyList<ConversationItem>> ListDescAsync(string tenantId, string userId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationItem>(
            new CommandDefinition(
                "SELECT id, reply, created_at AS CreatedAt FROM conversations"
                + " WHERE tenant_id = @tenantId AND user_id = @userId"
                + " ORDER BY created_at DESC, id DESC",
                new { tenantId, userId }, cancellationToken: ct));
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
