using System.Text.Json;
using Backend.Api.Configuration;
using Backend.Api.Data;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// 真 PostgreSQL fixture:連 appdb(env DB_CONNECTION_STRING,預設 localhost:5433/springaitest),
/// 冪等跑 DbBootstrap 確保 configuration_set 表存在。DB 不可達 → Available=false,測試以 SkipException
/// 標記為 skipped(非 passed,不假綠)。所有測試租戶以 "p4repo-" 前綴,DisposeAsync 一次清乾淨,不汙染既有資料。
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public NpgsqlDataSource? DataSource { get; private set; }

    public bool Available { get; private set; }

    public string? Unavailable { get; private set; }

    public ConfigurationSetRepository Repo => new(DataSource!);

    /// <summary>原始連線字串(含密碼)。NpgsqlDataSource.ConnectionString 會遮蔽密碼,
    /// 需要衍生連線(例如 migration 測試建臨時資料庫)時必須用這一份。</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest";
        ConnectionString = connString;
        try
        {
            DataSource = NpgsqlDataSource.Create(connString);
            await DbBootstrap.RunAsync(DataSource, NullLogger.Instance);
            Available = true;
        }
        catch (Exception ex)
        {
            Unavailable = ex.Message;
            Available = false;
        }
    }

    /// <summary>
    /// DB 不可達時把「宣稱驗 DB 不變量」的案標記為 **skipped**(非 passed)—— 讓無 DB 環境的「綠」
    /// 對這幾條保持誠實,不假綠。呼叫端測試方法須標 [SkippableFact](而非 [Fact]):xUnit
    /// 2.9.3 + runner.visualstudio 3.1.4 不認得舊版 SkipException.ForSkip 的 $XunitDynamicSkip$ 動態
    /// skip magic string(顯示為 Failed 而非 Skipped),改用 Xunit.SkippableFact 套件的
    /// Skip.If/SkipException,由其專屬 discoverer 攔截、正確回報 Skipped。
    /// </summary>
    public void SkipIfUnavailable()
    {
        Skip.If(!Available, $"appdb 不可達,略過真 DB 不變量案:{Unavailable}");
    }

    public async Task<int> ActiveCountAsync(string tenantId)
    {
        await using var conn = await DataSource!.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM configuration_set WHERE tenant_id = @tenantId AND is_active",
            new { tenantId });
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            try
            {
                await using var conn = await DataSource.OpenConnectionAsync();
                await conn.ExecuteAsync("DELETE FROM configuration_set WHERE tenant_id LIKE 'p4repo-%'");
                await conn.ExecuteAsync("DELETE FROM app_config WHERE tenant_id LIKE 'cfgrepo-%'");
            }
            catch
            {
                // 清理失敗不讓測試結果變紅(下次跑仍以前綴隔離)。
            }

            await DataSource.DisposeAsync();
        }
    }
}

/// <summary>
/// Configuration Set repository 的**真 PostgreSQL**驗收(SSR-P4-001/003/004/005/007 的 DB 級不變量)。
/// 這些是手寫 fake 無法背書的部分:部分唯一索引 uq_confset_active、原子 activate 的並發正確性、
/// jsonb 往返、跨租戶查詢過濾。每測用自己的 "p4repo-<case>-" 租戶,互不干擾。
/// </summary>
[Collection("Postgres")]
public sealed class ConfigurationSetRepositoryTests
{
    private readonly PostgresFixture _fx;

    public ConfigurationSetRepositoryTests(PostgresFixture fx) => _fx = fx;

    private static Dictionary<string, object> Values(params (string Key, object Value)[] kv)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in kv)
        {
            // 以 JsonElement 承載(比照 controller 反序列化後餵給 repo 的型別)。
            d[k] = JsonSerializer.SerializeToElement(v);
        }

        return d;
    }

    // ---- SSR-P4-001:DbBootstrap 冪等 + 兩個唯一約束/索引存在 ----

    [SkippableFact]
    public async Task Bootstrap_Idempotent_AndUniqueIndexesExist()
    {
        _fx.SkipIfUnavailable();

        // 再跑一次(fixture 已跑過一次)→ 不得拋。
        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        var indexes = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE tablename = 'configuration_set'")).ToList();

        Assert.Contains("uq_confset_tenant_name", indexes); // UNIQUE 約束的底層索引
        Assert.Contains("uq_confset_active", indexes);      // 部分唯一索引

        // 部分索引確實帶 WHERE is_active 述詞(否則會擋掉多筆 inactive)。
        var predicate = await conn.ExecuteScalarAsync<string>(
            "SELECT pg_get_expr(indpred, indrelid) FROM pg_index"
            + " WHERE indexrelid = 'uq_confset_active'::regclass");
        Assert.Contains("is_active", predicate ?? string.Empty);
    }

    // ---- SSR-P4-003:同租戶同名 DB 級拒收;不同租戶可同名 ----

    [SkippableFact]
    public async Task Create_DuplicateName_SameTenant_ReturnsNull_DbEnforced()
    {
        _fx.SkipIfUnavailable();
        const string t = "p4repo-003-a";

        var first = await _fx.Repo.CreateAsync(t, "dup", Values(), "u", default);
        Assert.NotNull(first);

        var second = await _fx.Repo.CreateAsync(t, "dup", Values(), "u", default);
        Assert.Null(second); // ON CONFLICT DO NOTHING → 0 列

        // 另一租戶可建同名。
        var other = await _fx.Repo.CreateAsync("p4repo-003-b", "dup", Values(), "u", default);
        Assert.NotNull(other);
    }

    // ---- SSR-P4-004:並發 activate 後至多一 active(部分索引兜底) ----

    [SkippableFact]
    public async Task ConcurrentActivate_LeavesExactlyOneActive_NoError_OtherTenantUnaffected()
    {
        _fx.SkipIfUnavailable();
        const string t = "p4repo-004-a";
        const string other = "p4repo-004-b";

        var a = await _fx.Repo.CreateAsync(t, "A", Values(), "u", default);
        var b = await _fx.Repo.CreateAsync(t, "B", Values(), "u", default);
        await _fx.Repo.ActivateAsync(t, a!.Id, default);

        // 另一租戶先有自己的 active,驗證不被本租戶並發污染。
        var c = await _fx.Repo.CreateAsync(other, "C", Values(), "u", default);
        await _fx.Repo.ActivateAsync(other, c!.Id, default);

        // 同步閘門:兩個 activate 同時放行。
        using var barrier = new Barrier(2);
        async Task<ConfigurationSet?> Fire(Guid id)
        {
            barrier.SignalAndWait();
            return await _fx.Repo.ActivateAsync(t, id, default);
        }

        var results = await Task.WhenAll(Task.Run(() => Fire(a.Id)), Task.Run(() => Fire(b!.Id)));

        // 兩個都成功(非 null = 非 500/未洩 constraint)。
        Assert.All(results, r => Assert.NotNull(r));

        // 恰好一個 active。
        Assert.Equal(1, await _fx.ActiveCountAsync(t));

        // 另一租戶仍恰一 active,且仍是 C。
        Assert.Equal(1, await _fx.ActiveCountAsync(other));
        Assert.True((await _fx.Repo.GetAsync(other, c.Id, default))!.IsActive);
    }

    // ---- 回歸(HIGH):activate 不存在的 id 不得清空該租戶現有 active(靜默資料損毀) ----

    [SkippableFact]
    public async Task Activate_NonexistentId_ReturnsNull_AndLeavesExistingActiveUntouched()
    {
        _fx.SkipIfUnavailable();
        const string t = "p4repo-ghost-a";

        var a = await _fx.Repo.CreateAsync(t, "A", Values(), "u", default);
        await _fx.Repo.ActivateAsync(t, a!.Id, default);
        Assert.Equal(1, await _fx.ActiveCountAsync(t));

        // 本租戶不存在的 id(用另一個 Guid;甚至可能是別租戶的)。
        var result = await _fx.Repo.ActivateAsync(t, Guid.NewGuid(), default);

        Assert.Null(result);
        Assert.Equal(1, await _fx.ActiveCountAsync(t));                    // active 未被清空
        Assert.True((await _fx.Repo.GetAsync(t, a.Id, default))!.IsActive); // 仍是原本那組
    }

    // ---- SSR-P4-005:刪 active 後該租戶變無 active、不自動選另一組 ----

    [SkippableFact]
    public async Task DeleteActive_TenantHasNoActive_DoesNotAutoSelect()
    {
        _fx.SkipIfUnavailable();
        const string t = "p4repo-005-a";

        var a = await _fx.Repo.CreateAsync(t, "A", Values(), "u", default);
        var b = await _fx.Repo.CreateAsync(t, "B", Values(), "u", default);
        await _fx.Repo.ActivateAsync(t, a!.Id, default);

        Assert.True(await _fx.Repo.DeleteAsync(t, a.Id, default));

        Assert.Equal(0, await _fx.ActiveCountAsync(t));
        Assert.Null(await _fx.Repo.GetActiveAsync(t, default));
        Assert.False((await _fx.Repo.GetAsync(t, b!.Id, default))!.IsActive); // B 未被自動啟用
    }

    // ---- SSR-P4-007:跨租戶查詢全部過濾;tenant-b 不受影響 ----

    [SkippableFact]
    public async Task CrossTenant_AllOperations_Filtered_OtherTenantIntact()
    {
        _fx.SkipIfUnavailable();
        const string a = "p4repo-007-a";
        const string b = "p4repo-007-b";

        var aSet = await _fx.Repo.CreateAsync(a, "only-a", Values(("kb_query.top_k", 7)), "u", default);
        await _fx.Repo.ActivateAsync(a, aSet!.Id, default);

        // tenant-b 對 a 的 id:讀不到、更不動、刪不掉、啟不動。
        Assert.Null(await _fx.Repo.GetAsync(b, aSet.Id, default));
        Assert.Null(await _fx.Repo.UpdateAsync(b, aSet.Id, "hijack", Values(), default));
        Assert.False(await _fx.Repo.DeleteAsync(b, aSet.Id, default));
        Assert.Null(await _fx.Repo.ActivateAsync(b, aSet.Id, default));

        // tenant-b 的 list 看不到 a 的組。
        Assert.DoesNotContain(await _fx.Repo.ListAsync(b, default), i => i.Id == aSet.Id);

        // tenant-a 完好:名稱、values、active 皆未變。
        var still = await _fx.Repo.GetAsync(a, aSet.Id, default);
        Assert.NotNull(still);
        Assert.Equal("only-a", still!.Name);
        Assert.True(still.IsActive);
        Assert.Equal(7, ((JsonElement)still.Values["kb_query.top_k"]).GetInt32());
    }

    // ---- jsonb 往返:values 存進去取回來型別/內容不失真(fake 掩蓋不了的真序列化) ----

    [SkippableFact]
    public async Task Values_RoundTrip_ThroughJsonb_PreservesTypes()
    {
        _fx.SkipIfUnavailable();
        const string t = "p4repo-json-a";

        var created = await _fx.Repo.CreateAsync(
            t, "roundtrip",
            Values(("retrieval.top_k", 15), ("llm.model", "gpt-4o-mini"), ("llm.temperature", 0.5)),
            "u", default);

        var fetched = await _fx.Repo.GetAsync(t, created!.Id, default);
        Assert.NotNull(fetched);
        Assert.Equal(15, ((JsonElement)fetched!.Values["retrieval.top_k"]).GetInt32());
        Assert.Equal("gpt-4o-mini", ((JsonElement)fetched.Values["llm.model"]).GetString());
        Assert.Equal(0.5, ((JsonElement)fetched.Values["llm.temperature"]).GetDouble());
    }
}
