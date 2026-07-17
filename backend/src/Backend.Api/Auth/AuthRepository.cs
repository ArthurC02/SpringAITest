using Backend.Api.Common;
using Dapper;
using Npgsql;

namespace Backend.Api.Auth;

/// <summary>以 Dapper + Npgsql 實作認證資料存取。</summary>
public sealed class AuthRepository : IAuthRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public AuthRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<TenantRow?> FindTenantByCodeAsync(string code, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TenantRow>(
            new CommandDefinition(
                "SELECT id, code, name, invite_code AS InviteCode FROM tenants WHERE code = @code",
                new { code }, cancellationToken: ct));
    }

    public async Task<bool> UsernameExistsAsync(string username, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM users WHERE username = @username)",
                new { username }, cancellationToken: ct));
    }

    public async Task AddUserAsync(string username, string passwordHash, string role, long tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO users (username, password_hash, role, tenant_id)"
                    + " VALUES (@username, @passwordHash, @role, @tenantId)",
                    new { username, passwordHash, role, tenantId }, cancellationToken: ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 併發同名註冊的 TOCTOU 兜底:UsernameExistsAsync 預檢通過後、INSERT 前被搶註冊。
            throw new ApiException(StatusCodes.Status409Conflict, "使用者名稱已存在：" + username);
        }
    }

    public async Task<UserRow?> FindUserByUsernameAsync(string username, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                "SELECT u.username, u.password_hash AS PasswordHash, u.role, t.code AS TenantCode"
                + " FROM users u JOIN tenants t ON t.id = u.tenant_id WHERE u.username = @username",
                new { username }, cancellationToken: ct));
    }
}
