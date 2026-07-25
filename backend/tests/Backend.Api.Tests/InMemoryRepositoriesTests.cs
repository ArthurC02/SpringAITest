using Backend.Api.Data.InMemory;

namespace Backend.Api.Tests;

/// <summary>
/// 升格後 InMemory 儲存庫(DB_PROVIDER=inmemory / Lite 模式)的單元驗收。
/// 打生產類別本身,釘住「與 DbBootstrap 一致的種子 + BCrypt」、租戶隔離、真 cosine、
/// 「一租戶至多一 active」這四條升格時最容易改壞的不變量。
/// </summary>
public sealed class InMemoryRepositoriesTests
{
    // ---- Auth:種子帳號與 DbBootstrap 一致,且密碼走 BCrypt.Verify(不是 hash 字面值比對) ----

    [Theory]
    [InlineData("admin-a", "ADMIN", "demo-a")]
    [InlineData("user-a", "USER", "demo-a")]
    [InlineData("user-b", "USER", "demo-b")]
    public async Task Auth_SeedUser_MatchesRoleTenant_AndPasswordVerifies(string username, string role, string tenant)
    {
        var repo = new InMemoryAuthRepository();

        var user = await repo.FindUserByUsernameAsync(username, default);

        Assert.NotNull(user);
        Assert.Equal(role, user!.Role);
        Assert.Equal(tenant, user.TenantCode);
        // 登入語義:BCrypt.Verify 通過(hash 是 BCrypt.HashPassword("password123"),與 DbBootstrap 同一套邏輯)。
        Assert.True(BCrypt.Net.BCrypt.Verify("password123", user.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-password", user.PasswordHash));
    }

    [Fact]
    public async Task Auth_SeedTenant_HasInviteCode()
    {
        var repo = new InMemoryAuthRepository();

        var tenant = await repo.FindTenantByCodeAsync("demo-a", default);

        Assert.NotNull(tenant);
        Assert.Equal("demo-a-invite", tenant!.InviteCode);
    }

    [Fact]
    public async Task Auth_UnknownUser_ReturnsNull()
    {
        var repo = new InMemoryAuthRepository();
        Assert.Null(await repo.FindUserByUsernameAsync("nobody", default));
    }

    // ---- Conversation:依 (tenant, user) 隔離 — 別的租戶/使用者看不到 ----

    [Fact]
    public async Task Conversation_ListDesc_IsScopedByTenantAndUser()
    {
        var repo = new InMemoryConversationRepository();
        await repo.AddAsync("demo-a", "user-a", "Q1", "A1", default);
        await repo.AddAsync("demo-b", "user-b", "Q2", "A2", default);
        await repo.AddAsync("demo-a", "other-user", "Q3", "A3", default);

        var mine = await repo.ListDescAsync("demo-a", "user-a", default);

        var item = Assert.Single(mine);
        Assert.Equal("A1", item.Reply);
    }

    [Fact]
    public async Task Conversation_ListDesc_NewestFirst_TieBrokenByIdDesc()
    {
        var repo = new InMemoryConversationRepository();
        var first = await repo.AddAsync("demo-a", "user-a", "Q1", "A1", default);
        var second = await repo.AddAsync("demo-a", "user-a", "Q2", "A2", default);

        var list = await repo.ListDescAsync("demo-a", "user-a", default);

        Assert.Equal(new[] { second.Id, first.Id }, list.Select(i => i.Id).ToArray());
    }

    // ---- Rag:真 cosine — 相同向量 → 1.0,正交向量 → 0.0,依相似度遞減排序,跨租戶不可見 ----

    private static float[] Vec(params float[] v) => v;

    private static async Task SeedReadyAsync(
        InMemoryRagRepository repo, string tenant, string docId, string title, (string, float[])[] chunks)
    {
        await repo.InsertProcessingDocumentAsync(docId, tenant, title, default);
        await repo.CompleteDocumentAsync(
            docId, tenant, chunks.Select(c => c.Item1).ToList(), chunks.Select(c => c.Item2).ToList(), default);
    }

    [Fact]
    public async Task Rag_Search_ComputesCosine_IdenticalIsOne_OrthogonalIsZero()
    {
        var repo = new InMemoryRagRepository();
        var docId = Guid.NewGuid().ToString();
        await SeedReadyAsync(repo, "demo-a", docId, "doc", new[]
        {
            ("same", Vec(1f, 0f, 0f)),         // 與 query 相同 → cosine 1.0
            ("orthogonal", Vec(0f, 1f, 0f)),   // 與 query 正交 → cosine 0.0
        });

        var results = await repo.SearchAsync("demo-a", Vec(1f, 0f, 0f), 10, default);

        Assert.Equal(2, results.Count);
        // 手算驗證:相同向量 cosine=1、正交 cosine=0,且高分在前。
        Assert.Equal("same", results[0].Content);
        Assert.Equal(1.0, results[0].Score, 5);
        Assert.Equal("orthogonal", results[1].Content);
        Assert.Equal(0.0, results[1].Score, 5);
    }

    [Fact]
    public async Task Rag_Search_ExcludesOtherTenants()
    {
        var repo = new InMemoryRagRepository();
        await SeedReadyAsync(repo, "demo-a", Guid.NewGuid().ToString(), "a", new[] { ("A-secret", Vec(1f, 0f)) });
        await SeedReadyAsync(repo, "demo-b", Guid.NewGuid().ToString(), "b", new[] { ("B-note", Vec(1f, 0f)) });

        var results = await repo.SearchAsync("demo-b", Vec(1f, 0f), 10, default);

        Assert.All(results, r => Assert.StartsWith("B-", r.Content));
    }

    [Fact]
    public async Task Rag_ScopedSearch_EnforcesTenantAndExactDocumentIds()
    {
        var repo = new InMemoryRagRepository();
        var allowed = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await SeedReadyAsync(
            repo,
            "demo-a",
            allowed.ToString("D"),
            "allowed",
            new[] { ("allowed", Vec(1f, 0f)) });
        await SeedReadyAsync(
            repo,
            "demo-a",
            denied.ToString("D"),
            "denied",
            new[] { ("denied", Vec(1f, 0f)) });
        await SeedReadyAsync(
            repo,
            "demo-b",
            otherTenant.ToString("D"),
            "other",
            new[] { ("other", Vec(1f, 0f)) });

        var scoped = await repo.SearchScopedAsync(
            "demo-a",
            Vec(1f, 0f),
            10,
            new[] { allowed, otherTenant },
            default);
        var empty = await repo.SearchScopedAsync(
            "demo-a",
            Vec(1f, 0f),
            10,
            Array.Empty<Guid>(),
            default);

        Assert.All(scoped, item => Assert.Equal(allowed.ToString("D"), item.DocumentId));
        Assert.NotEmpty(scoped);
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Rag_Search_DimensionMismatch_SkipsChunk_DoesNotThrow()
    {
        var repo = new InMemoryRagRepository();
        var docId = Guid.NewGuid().ToString();
        await SeedReadyAsync(repo, "demo-a", docId, "doc", new[]
        {
            ("wrong-dim", Vec(1f, 0f, 0f)), // query 只有 2 維 → 維度不匹配 → 略過
            ("ok", Vec(1f, 0f)),
        });

        var results = await repo.SearchAsync("demo-a", Vec(1f, 0f), 10, default);

        var hit = Assert.Single(results);
        Assert.Equal("ok", hit.Content);
    }

    // ---- ConfigurationSet:一租戶至多一 active(activate 先關其餘再開目標) ----

    [Fact]
    public async Task ConfigurationSet_Activate_LeavesExactlyOneActivePerTenant()
    {
        var repo = new InMemoryConfigurationSetRepository();
        var empty = new Dictionary<string, object>();
        var a = (await repo.CreateAsync("demo-a", "set-a", empty, "admin-a", default))!;
        var b = (await repo.CreateAsync("demo-a", "set-b", empty, "admin-a", default))!;

        await repo.ActivateAsync("demo-a", a.Id, default);
        await repo.ActivateAsync("demo-a", b.Id, default);

        // 啟用 b 後只剩 b 為 active(a 被自動關閉)。
        var active = await repo.GetActiveAsync("demo-a", default);
        Assert.NotNull(active);
        Assert.Equal(b.Id, active!.Id);

        var all = await repo.ListAsync("demo-a", default);
        Assert.Single(all, s => s.IsActive);
    }

    [Fact]
    public async Task ConfigurationSet_ActiveIsPerTenant_NotShared()
    {
        var repo = new InMemoryConfigurationSetRepository();
        var empty = new Dictionary<string, object>();
        var a = (await repo.CreateAsync("demo-a", "set", empty, "admin-a", default))!;
        await repo.ActivateAsync("demo-a", a.Id, default);

        // demo-b 沒有任何 active(不受 demo-a 影響)。
        Assert.Null(await repo.GetActiveAsync("demo-b", default));
    }
}
