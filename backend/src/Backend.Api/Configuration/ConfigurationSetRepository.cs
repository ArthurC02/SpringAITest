using System.Text.Json;
using Backend.Api.Common;
using Dapper;
using Npgsql;

namespace Backend.Api.Configuration;

/// <summary>
/// 以 Dapper + Npgsql 實作 configuration_set 存取。snake_case 欄位以 SQL alias 對映(比照 SkillRepository)。
/// values 以單一 jsonb 欄承載,寫入時序列化字串 → @valuesJson::jsonb,讀取時 values::text → 反序列化回
/// Dictionary&lt;string,object&gt;(值為 JsonElement,回傳時維持原生 JSON 型別)。
/// </summary>
public sealed class ConfigurationSetRepository : IConfigurationSetRepository
{
    private const string InfoCols =
        "id AS Id, name AS Name, is_active AS IsActive, created_at AS CreatedAt, updated_at AS UpdatedAt";

    private const string FullCols =
        "id AS Id, name AS Name, is_active AS IsActive, values::text AS ValuesJson,"
        + " created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt";

    private readonly NpgsqlDataSource _dataSource;

    public ConfigurationSetRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<IReadOnlyList<ConfigurationSetInfo>> ListAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<ConfigurationSetInfo>(new CommandDefinition(
            $"SELECT {InfoCols} FROM configuration_set WHERE tenant_id = @tenantId ORDER BY name",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<ConfigurationSet?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {FullCols} FROM configuration_set WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
        return row?.ToDto();
    }

    public async Task<ConfigurationSet?> GetActiveAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        // 部分唯一索引 uq_confset_active 保證至多一列 → QuerySingleOrDefault 安全。
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {FullCols} FROM configuration_set WHERE tenant_id = @tenantId AND is_active",
            new { tenantId }, cancellationToken: ct));
        return row?.ToDto();
    }

    public async Task<ConfigurationSet?> CreateAsync(
        string tenantId, string name, IReadOnlyDictionary<string, object> values, string createdBy, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // ON CONFLICT (tenant_id, name) DO NOTHING → 同名已存在則 0 列 → null → controller 409。
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "INSERT INTO configuration_set (tenant_id, name, values, created_by)"
            + " VALUES (@tenantId, @name, @valuesJson::jsonb, @createdBy)"
            + " ON CONFLICT (tenant_id, name) DO NOTHING"
            + $" RETURNING {FullCols}",
            new { tenantId, name, valuesJson = Serialize(values), createdBy }, cancellationToken: ct));
        return row?.ToDto();
    }

    public async Task<ConfigurationSet?> UpdateAsync(
        string tenantId, Guid id, string name, IReadOnlyDictionary<string, object> values, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                "UPDATE configuration_set SET name = @name, values = @valuesJson::jsonb, updated_at = now()"
                + " WHERE tenant_id = @tenantId AND id = @id"
                + $" RETURNING {FullCols}",
                new { tenantId, id, name, valuesJson = Serialize(values) }, cancellationToken: ct));
            return row?.ToDto();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 改名撞同租戶既有 name。
            throw new ApiException(StatusCodes.Status409Conflict, "Configuration Set 名稱已存在：" + name);
        }
    }

    public async Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM configuration_set WHERE tenant_id = @tenantId AND id = @id",
            new { tenantId, id }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<ConfigurationSet?> ActivateAsync(string tenantId, Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // ponytail: 用「per-tenant advisory lock + 兩條 UPDATE」而非單一 CTE。
        // 單一 data-modifying CTE 對「先關舊 active、再開新 active」有暫態暴露:兩個 UPDATE 的實際套用順序
        // 不保證,非 deferrable 的部分唯一索引 uq_confset_active 可能在中途看到兩列 is_active=true 而 500。
        // 先關(語句一整個套完 → 索引已無多餘 active)再開(語句二)才無暫態違反;advisory lock 讓同租戶的
        // 並發 activate 序列化(SSR-P4-004:皆 2xx、恰一 active、不洩 constraint)。DB 部分索引仍是最終兜底。
        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@tenantId))",
            new { tenantId }, transaction: tx, cancellationToken: ct));

        // 目標不存在於本租戶就別動任何 UPDATE:否則第一條會把現有 active 關掉、第二條 0 列,
        // commit 後該租戶變無 active(靜默資料損毀)。存在才「先關後開」。FOR UPDATE 鎖住目標列。
        var exists = await conn.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT true FROM configuration_set WHERE tenant_id = @tenantId AND id = @id FOR UPDATE",
            new { tenantId, id }, transaction: tx, cancellationToken: ct));
        if (exists is not true)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE configuration_set SET is_active = false, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND is_active AND id <> @id",
            new { tenantId, id }, transaction: tx, cancellationToken: ct));

        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "UPDATE configuration_set SET is_active = true, updated_at = now()"
            + " WHERE tenant_id = @tenantId AND id = @id"
            + $" RETURNING {FullCols}",
            new { tenantId, id }, transaction: tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
        return row?.ToDto();
    }

    private static string Serialize(IReadOnlyDictionary<string, object> values)
        => JsonSerializer.Serialize(values);

    /// <summary>DB 列投影;ValuesJson 是 jsonb 的文字形式,ToDto 反序列化回鍵值表。</summary>
    private sealed record Row(
        Guid Id, string Name, bool IsActive, string ValuesJson,
        string CreatedBy, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public ConfigurationSet ToDto() => new(
            Id, Name, IsActive,
            JsonSerializer.Deserialize<Dictionary<string, object>>(ValuesJson) ?? new(),
            CreatedBy, CreatedAt, UpdatedAt);
    }
}
