using Backend.Api.Auth;
using Backend.Api.Common;
using Dapper;

namespace Backend.Api.Tests;

/// <summary>
/// AuthRepository 的**真 PostgreSQL**驗收:AddUserAsync 的 TOCTOU 兜底(unique violation → 409)。
/// 手寫 fake(FakeAuthRepository,行程記憶體 Dictionary)無法產生真的 PostgresException,
/// 只有真 DB 唯一約束能背書這條 catch 分支 —— 比照 ConfigurationSetRepositoryTests 共用同一個
/// PostgresFixture(appdb 不可達則 SkipIfUnavailable 略過,不假綠)。
/// </summary>
public sealed class AuthRepositoryTests : IClassFixture<PostgresFixture>
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

        var manager = await repo.FindUserByUsernameAsync("admin-a", default);
        Assert.NotNull(manager);
        Assert.Contains("workflow.manage", manager!.Capabilities!);

        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);
        var username = $"authrepo-plain-admin-{Guid.NewGuid():N}";
        try
        {
            await repo.AddUserAsync(username, "hash", "ADMIN", tenant!.Id, default);
            var plainAdmin = await repo.FindUserByUsernameAsync(username, default);

            Assert.NotNull(plainAdmin);
            Assert.Equal("ADMIN", plainAdmin!.Role);
            Assert.Empty(plainAdmin.Capabilities!);
        }
        finally
        {
            await using var conn = await _fx.DataSource!.OpenConnectionAsync();
            await conn.ExecuteAsync("DELETE FROM users WHERE username = @username", new { username });
        }
    }
}
