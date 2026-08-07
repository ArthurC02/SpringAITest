using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Backend.Api.Data.Migrations;
using Dapper;
using Npgsql;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// P2 遷移測試共用資產:test-only manifest 與一次性資料庫。
///
/// 一次性資料庫的名稱走生成式白名單 <c>springaitest_mig_test_&lt;16 hex&gt;</c>,
/// 由 localhost:5433 的伺服器連線建立/銷毀 —— 遷移測試絕不碰 <c>springaitest</c> 本體
/// (04-acceptance P2 節末尾的硬性限制)。
/// </summary>
internal static class MigrationFixtures
{
    private const string ResourceRoot = "Backend.Api.Tests.Migrations.Fixtures.";

    /// <summary>
    /// fixture 的 legacy 分類白名單。<c>fx_widget</c>/<c>fx_widget_item</c> 是目標表名,
    /// 但同樣列在 legacy 白名單裡:目標名稱出現在 legacy 庫是可辨識的 SpringAITest 物件,
    /// 不該被當成 unknown;而 0001 只清 legacy 表,所以那種異常會在 0002 撞名時整批中止(P2-06)。
    /// </summary>
    private static readonly string[] LegacyAllowlist =
        ["fx_legacy_a", "fx_legacy_b", "fx_widget", "fx_widget_item"];

    /// <summary>正常的三檔 fixture bundle + 通過的 postcondition;三檔都在 bundle 內(同一交易)。</summary>
    public static MigrationManifest Bundle { get; } = Build("Sql.", "Postconditions.", bundleThroughVersion: 3);

    /// <summary>同一份 SQL,但 postcondition 永遠不成立(P2-11)。</summary>
    public static MigrationManifest BundleWithFailingPostcondition { get; } =
        Build("Sql.", "FailingPostconditions.", bundleThroughVersion: 3);

    /// <summary>
    /// bundle 上界 = 2,第 3 檔在 bundle 之外且執行期必定失敗:
    /// 用來證明「bundle 原子 + 其後一檔一交易」確實是兩個交易批次。
    /// </summary>
    public static MigrationManifest BundleBoundary { get; } =
        Build("BundleBoundarySql.", "Postconditions.", bundleThroughVersion: 2);

    public static MigrationManifest Build(string scriptFolder, string postconditionFolder, int bundleThroughVersion)
        => MigrationManifest.FromAssembly(
            typeof(MigrationFixtures).Assembly,
            ResourceRoot + scriptFolder,
            ResourceRoot + postconditionFolder,
            bundleThroughVersion,
            LegacyAllowlist,
            ["plpgsql", "vector"]);

    public static string ResourcePrefix(string folder) => ResourceRoot + folder;

    /// <summary>合成的 legacy schema:分類為 known legacy,且帶可清點的列數。</summary>
    public const string SyntheticLegacySchema = """
        CREATE TABLE fx_legacy_a (id integer PRIMARY KEY, payload text NOT NULL);
        CREATE TABLE fx_legacy_b (id integer PRIMARY KEY, note text);
        INSERT INTO fx_legacy_a SELECT g, 'row-' || g FROM generate_series(1, 7) g;
        INSERT INTO fx_legacy_b SELECT g, 'note-' || g FROM generate_series(1, 3) g;
        """;
}

/// <summary>建立/銷毀一次性資料庫。名稱不符生成式白名單即拒絕。</summary>
internal sealed class DisposableDatabase : IAsyncDisposable
{
    private static readonly Regex AllowedName = new("^springaitest_mig_test_[0-9a-f]{16}$", RegexOptions.Compiled);

    private readonly string _serverConnectionString;

    private DisposableDatabase(string name, string connectionString, string serverConnectionString)
    {
        Name = name;
        ConnectionString = connectionString;
        _serverConnectionString = serverConnectionString;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public static Task<DisposableDatabase> CreateAsync(string appdbConnectionString)
        => CreateAsync(appdbConnectionString, $"springaitest_mig_test_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");

    public static async Task<DisposableDatabase> CreateAsync(string appdbConnectionString, string name)
    {
        if (!AllowedName.IsMatch(name))
        {
            throw new InvalidOperationException($"一次性資料庫名稱不在生成式白名單內:{name}");
        }

        // 只借用 appdb 的「伺服器」座標(localhost:5433),資料庫換成一次性的那一個。
        var serverBuilder = new NpgsqlConnectionStringBuilder(appdbConnectionString) { Database = "postgres" };
        var server = serverBuilder.ConnectionString;

        await using (var conn = new NpgsqlConnection(server))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync($"CREATE DATABASE \"{name}\"");
        }

        var targetBuilder = new NpgsqlConnectionStringBuilder(appdbConnectionString) { Database = name };
        return new DisposableDatabase(name, targetBuilder.ConnectionString, server);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql);
    }

    public async Task<T> QueryScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return (await conn.ExecuteScalarAsync<T>(sql))!;
    }

    public async Task<List<T>> QueryAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return (await conn.QueryAsync<T>(sql)).AsList();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_serverConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }
}
