using Dapper;
using Npgsql;

namespace Backend.Api.Conversations;

/// <summary>以 Dapper + Npgsql 實作對話紀錄存取。</summary>
public sealed class ConversationRepository : IConversationRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ConversationRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<ConversationCreated> AddAsync(string prompt, string reply, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<ConversationCreated>(
            new CommandDefinition(
                "INSERT INTO conversations (prompt, reply) VALUES (@prompt, @reply)"
                + " RETURNING id, created_at AS CreatedAt",
                new { prompt, reply }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConversationItem>> ListDescAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConversationItem>(
            new CommandDefinition(
                "SELECT id, reply, created_at AS CreatedAt FROM conversations"
                + " ORDER BY created_at DESC, id DESC",
                cancellationToken: ct));
        return rows.AsList();
    }
}
