using Backend.Api.Config;
using Backend.Api.Data;
using Backend.Api.Data.InMemory;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// app_config 租戶隔離的**真 PostgreSQL**驗收:複合主鍵 (tenant_id, key) 是隔離邊界本身,
/// 手寫 fake 背書不了(fake 的 tuple 鍵永遠「對」,錯的是 DB 的主鍵沒換)。
/// 測試租戶一律 "cfgrepo-" 前綴,由 PostgresFixture.DisposeAsync 統一清理。
/// </summary>
[Collection("Postgres")]
public sealed class ConfigRepositoryTests
{
    private readonly PostgresFixture _fx;

    public ConfigRepositoryTests(PostgresFixture fx) => _fx = fx;

    private ConfigRepository Repo => new(_fx.DataSource!);

    // A 寫的 key 對 B 不可見;兩租戶可有同名 key 且值互不覆蓋(單欄 key 主鍵時第二次 upsert
    // 會改掉 A 那列 → 本測轉紅)。
    [SkippableFact]
    public async Task CrossTenant_SameKey_IsolatedValues_AndListFiltered()
    {
        _fx.SkipIfUnavailable();
        const string a = "cfgrepo-iso-a";
        const string b = "cfgrepo-iso-b";
        const string shared = "agent.defaults.system_prompt";

        await Repo.UpsertAsync(a, "only-a", "a-value", default);
        await Repo.UpsertAsync(a, shared, "prompt-a", default);
        await Repo.UpsertAsync(b, shared, "prompt-b", default);

        var listA = await Repo.ListAsync(a, default);
        var listB = await Repo.ListAsync(b, default);

        Assert.Equal(new[] { shared, "only-a" }, listA.Select(i => i.Key).ToArray());
        Assert.Equal("prompt-a", Assert.Single(listA, i => i.Key == shared).Value);

        Assert.Equal(new[] { shared }, listB.Select(i => i.Key).ToArray()); // 看不到 only-a
        Assert.Equal("prompt-b", Assert.Single(listB).Value);
    }

    // Upsert 的 UPDATE 分支只動自己租戶那一列(ON CONFLICT 目標必須是 (tenant_id, key))。
    [SkippableFact]
    public async Task Upsert_Update_TouchesOnlyOwnTenantRow()
    {
        _fx.SkipIfUnavailable();
        const string a = "cfgrepo-upd-a";
        const string b = "cfgrepo-upd-b";

        await Repo.UpsertAsync(a, "theme", "light", default);
        await Repo.UpsertAsync(b, "theme", "dark", default);

        var updated = await Repo.UpsertAsync(a, "theme", "solar", default);

        Assert.Equal("solar", updated.Value);
        Assert.Equal("solar", Assert.Single(await Repo.ListAsync(a, default)).Value);
        Assert.Equal("dark", Assert.Single(await Repo.ListAsync(b, default)).Value);
    }

    // in-memory 實作(Lite 模式 = 測試的 fake)必須與 Dapper 同行為。tuple 複合鍵 vs
    // 字串串接的差別只有在 key 含分隔符時才看得出來 — 這裡就用含 ':' 的 key 釘住。
    [Fact]
    public async Task InMemory_Parity_CrossTenantIsolation_EvenWhenKeyContainsSeparator()
    {
        var repo = new InMemoryConfigRepository();

        await repo.UpsertAsync("t", "a:b", "one", default);   // 串接後 "t:a:b"
        await repo.UpsertAsync("t:a", "b", "two", default);   // 串接後也是 "t:a:b"

        Assert.Equal("one", Assert.Single(await repo.ListAsync("t", default)).Value);
        Assert.Equal("two", Assert.Single(await repo.ListAsync("t:a", default)).Value);
        Assert.Empty(await repo.ListAsync("other", default));
    }
}

/// <summary>
/// app_config 舊 shape(單欄 key 主鍵、全平台共用)→ 租戶隔離的**就地 migration** 驗收。
/// 跑在一個臨時建立的資料庫上(而非共用 appdb):既不動開發者既有的 app_config 資料,
/// 也還原真實升級情境 — 完整 DbBootstrap 從零建到底。fake 完全背書不了這段。
/// </summary>
[Collection("Postgres")]
public sealed class AppConfigMigrationTests
{
    private readonly PostgresFixture _fx;

    public AppConfigMigrationTests(PostgresFixture fx) => _fx = fx;

    [SkippableFact]
    public async Task LegacyRows_AreCopiedToEveryTenant_PrimaryKeyBecomesComposite_RerunIsNoOp()
    {
        _fx.SkipIfUnavailable();

        await InTempDatabaseAsync("cfgmig_copy_test", async scoped =>
        {
            await DowngradeToLegacyShapeAsync(scoped);

            // migration。
            await DbBootstrap.RunAsync(scoped, NullLogger.Instance);

            await using (var conn = await scoped.OpenConnectionAsync())
            {
                var tenants = (await conn.QueryAsync<string>("SELECT code FROM tenants ORDER BY code")).ToList();
                Assert.NotEmpty(tenants);

                // 每個租戶都拿得到升級前的值 — 沒有任何 ADMIN 的設定被靜默丟掉。
                foreach (var tenant in tenants)
                {
                    var rows = (await new ConfigRepository(scoped).ListAsync(tenant, default))
                        .ToDictionary(i => i.Key, i => i.Value);
                    Assert.Equal("legacy prompt", rows["agent.defaults.system_prompt"]);
                    Assert.Equal("dark", rows["theme"]);
                }

                // 無主的舊列(tenant_id NULL)已清乾淨,總列數 = 租戶數 × 2 key。
                Assert.Equal(
                    tenants.Count * 2,
                    await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM app_config"));

                Assert.Equal(
                    new[] { "tenant_id", "key" },
                    (await PrimaryKeyColumnsAsync(conn)).ToArray());
            }

            // 重跑 = no-op:不重複回填、不報錯、主鍵不變。
            await DbBootstrap.RunAsync(scoped, NullLogger.Instance);

            await using (var conn = await scoped.OpenConnectionAsync())
            {
                var tenantCount = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tenants");
                Assert.Equal(
                    tenantCount * 2,
                    await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM app_config"));
                Assert.Equal(
                    new[] { "tenant_id", "key" },
                    (await PrimaryKeyColumnsAsync(conn)).ToArray());
            }
        });
    }

    /// <summary>
    /// fail-safe 分支:租戶表為空時舊列無人可歸屬。DbBootstrap 選擇**刪除**它們 —— 因為
    /// tenant_id NOT NULL 之後沒有 caller 讀得到,留著只會讓 SET NOT NULL 卡死啟動。
    /// 這是本次唯一會刻意銷毀資料的路徑,所以要有可執行的事實背書,不能只有註解。
    /// </summary>
    [SkippableFact]
    public async Task NoTenants_OrphanLegacyRowsAreDeleted_BootstrapStillCompletes()
    {
        _fx.SkipIfUnavailable();

        await InTempDatabaseAsync("cfgmig_orphan_test", async scoped =>
        {
            await DowngradeToLegacyShapeAsync(scoped);
            await using (var conn = await scoped.OpenConnectionAsync())
            {
                // users/user_group_membership 以 FK 指向 tenants,一併 CASCADE 清掉。
                await conn.ExecuteAsync("TRUNCATE tenants CASCADE");
            }

            // 不得拋(NOT NULL 不會被無主列卡住)。
            await DbBootstrap.RunAsync(scoped, NullLogger.Instance);

            await using (var conn = await scoped.OpenConnectionAsync())
            {
                Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM app_config"));
                Assert.Equal(
                    new[] { "tenant_id", "key" },
                    (await PrimaryKeyColumnsAsync(conn)).ToArray());
            }
        });
    }

    /// <summary>DbBootstrap 建好全新 schema 後,把 app_config 打回舊 shape(單欄 key 主鍵)並塞入
    /// 升級前既有的兩筆設定 —— 還原「既有 appdb」的起點。</summary>
    private static async Task DowngradeToLegacyShapeAsync(NpgsqlDataSource scoped)
    {
        // 先建出完整 schema(含 tenants 種子 demo-a/demo-b);此時 app_config 是新 shape 且為空。
        await DbBootstrap.RunAsync(scoped, NullLogger.Instance);

        await using var conn = await scoped.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "DROP TABLE app_config;"
            + " CREATE TABLE app_config (key text PRIMARY KEY, value text NOT NULL,"
            + "   updated_at timestamptz NOT NULL DEFAULT now());"
            + " INSERT INTO app_config (key, value) VALUES"
            + "   ('agent.defaults.system_prompt','legacy prompt'),('theme','dark')");
    }

    /// <summary>臨時資料庫的建立/清理樣板:每個 case 一個獨立 DB,絕不動共用 appdb 的 app_config。</summary>
    private async Task InTempDatabaseAsync(string database, Func<NpgsqlDataSource, Task> body)
    {
        await DropTempDatabaseAsync(database);
        await using (var admin = await _fx.DataSource!.OpenConnectionAsync())
        {
            await admin.ExecuteAsync($"CREATE DATABASE {database}");
        }

        var builder = new NpgsqlConnectionStringBuilder(_fx.ConnectionString) { Database = database };
        var scoped = NpgsqlDataSource.Create(builder.ConnectionString);
        try
        {
            await body(scoped);
        }
        finally
        {
            await scoped.DisposeAsync();
            await DropTempDatabaseAsync(database);
        }
    }

    private async Task DropTempDatabaseAsync(string database)
    {
        try
        {
            await using var admin = await _fx.DataSource!.OpenConnectionAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
        catch
        {
            // 清理失敗不讓測試變紅(下次跑會先 DROP)。
        }
    }

    private static async Task<IEnumerable<string>> PrimaryKeyColumnsAsync(NpgsqlConnection conn)
        => await conn.QueryAsync<string>(
            "SELECT a.attname FROM pg_index i"
            + " JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)"
            + " JOIN unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON k.attnum = a.attnum"
            + " WHERE i.indrelid = 'app_config'::regclass AND i.indisprimary"
            + " ORDER BY k.ord");
}
