using Dapper;
using Npgsql;

namespace Backend.Api.Config;

/// <summary>以 Dapper + Npgsql 實作系統組態存取。</summary>
public sealed class ConfigRepository : IConfigRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ConfigRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<ConfigItem>> ListAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConfigItem>(new CommandDefinition(
            "SELECT key AS Key, value AS Value, updated_at AS UpdatedAt FROM app_config ORDER BY key",
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ConfigItem> UpsertAsync(string key, string value, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<ConfigItem>(new CommandDefinition(
            "INSERT INTO app_config (key, value) VALUES (@key, @value)"
            + " ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()"
            + " RETURNING key AS Key, value AS Value, updated_at AS UpdatedAt",
            new { key, value }, cancellationToken: ct));
    }
}
