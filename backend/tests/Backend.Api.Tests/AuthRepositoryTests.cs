using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Backend.Api.Agents;
using Backend.Api.Auth;
using Backend.Api.Common;
using Dapper;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// AuthRepository 的**真 PostgreSQL**驗收:AddUserAsync 的 TOCTOU 兜底(unique violation → 409)。
/// 手寫 fake(FakeAuthRepository,行程記憶體 Dictionary)無法產生真的 PostgresException,
/// 只有真 DB 唯一約束能背書這條 catch 分支 —— 比照 ConfigurationSetRepositoryTests 加入
/// "Postgres" collection 共用同一個 PostgresFixture 並序列化執行(appdb 不可達則
/// SkipIfUnavailable 略過,不假綠)。用 IClassFixture 自建一份 fixture 會讓本類與 collection
/// 內的其他 DB 測試類**平行**跑 DbBootstrap 的 DDL,實測會打壞
/// ConfigurationSetRepositoryTests.ConcurrentActivate_*(PostgresCollection.cs 註解描述的失效模式)。
/// </summary>
[Collection("Postgres")]
public sealed class AuthRepositoryTests
{
    private readonly PostgresFixture _fx;

    public AuthRepositoryTests(PostgresFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task AddUserAsync_DuplicateUsername_Throws409_NotUnhandled500()
    {
        _fx.SkipIfUnavailable();

        var repo = new AuthRepository(_fx.DataSource!);
        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);
        Assert.NotNull(tenant);

        var username = $"authrepo-toctou-{Guid.NewGuid():N}";
        try
        {
            // 第一次寫入成功(模擬預檢通過後的正常路徑)。
            await repo.AddUserAsync(username, "hash1", "USER", tenant!.Id, default);

            // 第二次寫入同名(模擬併發競態:預檢當下不存在,寫入前被搶註冊)→ 唯一約束觸發,
            // 轉成與既有「username 已存在」相同的 409 ApiException,而非未捕捉的 PostgresException(500)。
            var ex = await Assert.ThrowsAsync<ApiException>(
                () => repo.AddUserAsync(username, "hash2", "USER", tenant.Id, default));

            Assert.Equal(409, ex.Status);
            Assert.Equal("使用者名稱已存在：" + username, ex.Message);
        }
        finally
        {
            await using var conn = await _fx.DataSource!.OpenConnectionAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE username = @username", new { username });
        }
    }

    [SkippableFact]
    public async Task Capabilities_ArePersistedPerUser_AndAdminRoleDoesNotGrantThem()
    {
        _fx.SkipIfUnavailable();
        var repo = new AuthRepository(_fx.DataSource!);
        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);
        var managerUsername = $"authrepo-manager-{Guid.NewGuid():N}";
        var username = $"authrepo-plain-admin-{Guid.NewGuid():N}";
        try
        {
            await using (var connection = await _fx.DataSource!.OpenConnectionAsync())
            {
                await connection.ExecuteAsync(
                    "INSERT INTO users"
                    + " (username,password_hash,role,tenant_id,capabilities)"
                    + " VALUES (@managerUsername,'hash','USER',@tenantId,"
                    + " ARRAY['workflow.manage']::text[])",
                    new { managerUsername, tenantId = tenant!.Id });
            }
            var manager = await repo.FindUserByUsernameAsync(managerUsername, default);
            Assert.NotNull(manager);
            Assert.Contains("workflow.manage", manager!.Capabilities);

            await repo.AddUserAsync(username, "hash", "ADMIN", tenant!.Id, default);
            var plainAdmin = await repo.FindUserByUsernameAsync(username, default);

            Assert.NotNull(plainAdmin);
            Assert.Equal("ADMIN", plainAdmin!.Role);
            Assert.Empty(plainAdmin.Capabilities!);
        }
        finally
        {
            await using var conn = await _fx.DataSource!.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "DELETE FROM users WHERE username IN (@username,@managerUsername)",
                new { username, managerUsername });
        }
    }

    [SkippableFact]
    public async Task Groups_AreLoadedFromSameTenantAndCompositeForeignKeyRejectsCrossTenantGrant()
    {
        _fx.SkipIfUnavailable();
        var repo = new AuthRepository(_fx.DataSource!);

        var admin = await repo.FindUserByUsernameAsync("admin-a", default);
        Assert.NotNull(admin);
        Assert.Equal(new[] { "operations" }, admin!.Groups);

        await using var connection = await _fx.DataSource!.OpenConnectionAsync();
        var userId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM users WHERE username='admin-a'");
        var otherTenantId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM tenants WHERE code='demo-b'");
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => connection.ExecuteAsync(
                "INSERT INTO user_group_membership (tenant_id,user_id,group_id)"
                + " VALUES (@OtherTenantId,@UserId,'cross-tenant')",
                new { OtherTenantId = otherTenantId, UserId = userId }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);

        var unchanged = await repo.FindUserByUsernameAsync("admin-a", default);
        Assert.Equal(new[] { "operations" }, unchanged!.Groups);
    }

    [SkippableFact]
    public async Task Login_OverAggregatePersistedGroupSetFailsClosedAtJwtIssuance()
    {
        _fx.SkipIfUnavailable();
        var repo = new AuthRepository(_fx.DataSource!);
        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);
        var username = $"authrepo-groups-overbound-{Guid.NewGuid():N}";
        try
        {
            await repo.AddUserAsync(
                username,
                BCrypt.Net.BCrypt.HashPassword("password123"),
                "ADMIN",
                tenant!.Id,
                default);
            await using (var connection = await _fx.DataSource!.OpenConnectionAsync())
            {
                await connection.ExecuteAsync(
                    "INSERT INTO user_group_membership (tenant_id,user_id,group_id)"
                    + " SELECT @tenantId,u.id,membership.group_id"
                    + " FROM users u CROSS JOIN unnest(@groups::text[])"
                    + " AS membership(group_id)"
                    + " WHERE u.username=@username",
                    new
                    {
                        tenantId = tenant.Id,
                        username,
                        groups = GroupSet(exceedByOneByte: true),
                    });
            }

            var auth = new AuthService(repo);
            var result = await auth.LoginAsync(
                new LoginRequest(username, "password123"),
                default);
            var jwt = new JwtService(
                "dev-jwt-secret-change-me-0123456789abcdef",
                TimeSpan.FromHours(24));
            Assert.Throws<InvalidOperationException>(() => jwt.Issue(
                result.Username,
                result.Role,
                result.TenantCode,
                result.Capabilities,
                result.Groups));
        }
        finally
        {
            await using var connection = await _fx.DataSource!.OpenConnectionAsync();
            await connection.ExecuteAsync(
                "DELETE FROM users WHERE username=@username",
                new { username });
        }
    }

    /// <summary>
    /// 上一個測試的 on-point 另一半:持久化的群組集**剛好**等於
    /// AgentAudience.MaxGroupsWireUtf8Bytes(2048 bytes)時,DB 聚合 → login → 簽 JWT 這條路必須成功,
    /// 而且 16 個 group 一個不漏地進到 token(否則「多一位元組就擋」只證明了會擋,沒證明擋在正確的位置)。
    /// </summary>
    [SkippableFact]
    public async Task Login_ExactBoundPersistedGroupSetIssuesJwtCarryingEveryGroup()
    {
        _fx.SkipIfUnavailable();
        var repo = new AuthRepository(_fx.DataSource!);
        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);
        var username = $"authrepo-groups-atbound-{Guid.NewGuid():N}";
        var groups = GroupSet(exceedByOneByte: false);
        Assert.Equal(
            AgentAudience.MaxGroupsWireUtf8Bytes,
            Encoding.UTF8.GetByteCount(string.Join(' ', groups)));
        try
        {
            await repo.AddUserAsync(
                username,
                BCrypt.Net.BCrypt.HashPassword("password123"),
                "ADMIN",
                tenant!.Id,
                default);
            await using (var connection = await _fx.DataSource!.OpenConnectionAsync())
            {
                await connection.ExecuteAsync(
                    "INSERT INTO user_group_membership (tenant_id,user_id,group_id)"
                    + " SELECT @tenantId,u.id,membership.group_id"
                    + " FROM users u CROSS JOIN unnest(@groups::text[])"
                    + " AS membership(group_id)"
                    + " WHERE u.username=@username",
                    new
                    {
                        tenantId = tenant.Id,
                        username,
                        groups,
                    });
            }

            var expected = groups.OrderBy(group => group, StringComparer.Ordinal).ToArray();
            var auth = new AuthService(repo);
            var result = await auth.LoginAsync(
                new LoginRequest(username, "password123"),
                default);
            Assert.Equal(expected, result.Groups!);

            var jwt = new JwtService(
                "dev-jwt-secret-change-me-0123456789abcdef",
                TimeSpan.FromHours(24));
            var token = jwt.Issue(
                result.Username,
                result.Role,
                result.TenantCode,
                result.Capabilities,
                result.Groups);

            Assert.Equal(
                expected,
                new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
                    .Where(claim => claim.Type == "groups")
                    .Select(claim => claim.Value)
                    .ToArray());
            Assert.True(
                Encoding.ASCII.GetByteCount("Bearer " + token)
                < JwtService.MaxAuthorizationValueBytes);
        }
        finally
        {
            await using var connection = await _fx.DataSource!.OpenConnectionAsync();
            await connection.ExecuteAsync(
                "DELETE FROM users WHERE username=@username",
                new { username });
        }
    }

    private static string[] GroupSet(bool exceedByOneByte)
        => Enumerable.Range(0, 16)
            .Select(index =>
            {
                var length = index == 0 || exceedByOneByte && index == 1
                    ? 128
                    : 127;
                var prefix = $"g{index:D2}";
                return prefix + new string('a', length - prefix.Length);
            })
            .ToArray();
}
