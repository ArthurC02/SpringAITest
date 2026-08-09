using Backend.Api.Data;
using Backend.Api.Tests.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests;

/// <summary>
/// 「一次性」遷移必須真的收斂:遷移完成後的每一次啟動,對 skill / skill_revision 都必須產生
/// **0 次 UPDATE**。以 <c>xmin</c> 為證 —— PostgreSQL 連 no-op UPDATE(SET name = name)都會寫出
/// 新的 tuple 版本並換掉 xmin,所以 xmin 不變即代表沒有任何寫入與 dead tuple。
/// 全部跑在一次性資料庫上,絕不碰 springaitest 本體。
/// </summary>
[Collection("Postgres")]
public sealed class DbBootstrapConvergenceTests(PostgresFixture fixture)
{
    private const string LegacyDefinition = "name: legacy_flow\nsteps: []\n";

    [SkippableFact]
    public async Task RepeatedBootstrapRewritesNoSkillRowOnceMigrated()
    {
        fixture.SkipIfUnavailable();

        await using var db = await DisposableDatabase.CreateAsync(fixture.ConnectionString);
        await using (var dataSource = NpgsqlDataSource.Create(db.ConnectionString))
        {
            await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);
        }

        // legacy 形狀:底線名稱 + 與名稱一致但非標準的 definition。下一次啟動才會被遷移。
        await db.ExecuteAsync(
            "INSERT INTO skill (id, tenant_id, name, description, definition, kind, current_revision)"
            + " VALUES ('11111111-1111-1111-1111-111111111111','demo-a','legacy_flow','d',"
            + $" {Literal(LegacyDefinition)}, 'flow', 1);"
            + " INSERT INTO skill_revision (skill_id, revision, definition, definition_sha256, created_by, kind)"
            + " VALUES ('11111111-1111-1111-1111-111111111111', 1,"
            + $" {Literal(LegacyDefinition)}, repeat('0', 64), 'seed', 'flow');");

        var beforeMigration = await SkillVersionsAsync(db);
        await using (var dataSource = NpgsqlDataSource.Create(db.ConnectionString))
        {
            await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);
        }

        var afterMigration = await SkillVersionsAsync(db);
        Assert.NotEqual(beforeMigration, afterMigration);
        Assert.Equal(
            ["legacy-flow"],
            await db.QueryAsync<string>("SELECT name FROM skill ORDER BY name"));

        // 第二次(遷移已完成後的)啟動:一列都不得再被寫。
        await using (var dataSource = NpgsqlDataSource.Create(db.ConnectionString))
        {
            await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);
        }

        Assert.Equal(afterMigration, await SkillVersionsAsync(db));
    }

    [SkippableFact]
    public async Task FreshDatabaseStillTightensCommandInputHashToNotNull()
    {
        fixture.SkipIfUnavailable();

        await using var db = await DisposableDatabase.CreateAsync(fixture.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);

        Assert.Equal(["NO"], await IsNullableAsync(db));

        // 再跑一次仍是 NOT NULL(guard 不得把已收斂的欄位放鬆)。
        await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);
        Assert.Equal(["NO"], await IsNullableAsync(db));
    }

    [SkippableFact]
    public async Task LegacyNullableCommandInputHashIsTightenedBackToNotNull()
    {
        fixture.SkipIfUnavailable();

        await using var db = await DisposableDatabase.CreateAsync(fixture.ConnectionString);
        await using var dataSource = NpgsqlDataSource.Create(db.ConnectionString);
        await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);

        // 重現 legacy 形狀:把已收斂的欄位手動放鬆回 nullable,證明 guard 真的會把它收緊回去
        // (而不是只在「本來就是 NOT NULL」的分支下空轉綠燈)。
        await db.ExecuteAsync(
            "ALTER TABLE agent_run_command ALTER COLUMN command_input_sha256 DROP NOT NULL");
        Assert.Equal(["YES"], await IsNullableAsync(db));

        await DbBootstrap.RunAsync(dataSource, NullLogger.Instance);

        Assert.Equal(["NO"], await IsNullableAsync(db));
    }

    private static Task<List<string>> IsNullableAsync(DisposableDatabase db)
        => db.QueryAsync<string>(
            "SELECT is_nullable FROM information_schema.columns"
            + " WHERE table_schema='public' AND table_name='agent_run_command'"
            + " AND column_name='command_input_sha256'");

    private static async Task<List<string>> SkillVersionsAsync(DisposableDatabase db)
        => await db.QueryAsync<string>(
            "SELECT 'skill:' || id::text || '@' || xmin::text FROM skill"
            + " UNION ALL"
            + " SELECT 'revision:' || id::text || '@' || xmin::text FROM skill_revision"
            + " ORDER BY 1");

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
