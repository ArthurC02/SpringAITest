using Backend.Api.Data.Migrations;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// P2 遷移矩陣(04-acceptance-tests P2)。全部針對「生成式命名的一次性資料庫」執行,
/// 只借用 appdb 的伺服器座標,永不碰 <c>springaitest</c> 本體。
/// 這裡驗的是 runner 機制,不是生產目標 schema —— manifest 全是 test-only fixture。
/// </summary>
[Collection("Postgres")]
public sealed class DbMigrationRunnerTests(PostgresFixture fixture)
{
    private static DbMigrationRunner Runner(MigrationManifest manifest, int lockTimeoutSeconds = 60)
        => new(manifest, NullLogger.Instance, lockTimeoutSeconds);

    private async Task<DisposableDatabase> NewDatabaseAsync()
    {
        fixture.SkipIfUnavailable();
        return await DisposableDatabase.CreateAsync(fixture.ConnectionString);
    }

    /// <summary>seed 失敗時要把一次性資料庫收掉,否則測試庫會留在伺服器上。</summary>
    private async Task<DisposableDatabase> NewLegacyDatabaseAsync(string schema = MigrationFixtures.SyntheticLegacySchema)
    {
        var db = await NewDatabaseAsync();
        try
        {
            await db.ExecuteAsync(schema);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private static Task<bool> MetaAbsentAsync(DisposableDatabase db)
        => db.QueryScalarAsync<bool>("SELECT to_regclass('springaitest_meta.schema_migration') IS NULL");

    private static Task<long> LedgerCountAsync(DisposableDatabase db)
        => db.QueryScalarAsync<long>("SELECT count(*) FROM springaitest_meta.schema_migration");

    // ---------------------------------------------------------------- P2-01
    [SkippableFact]
    public async Task P2_01_EmptyDatabaseInitializesFixtureMigrations()
    {
        await using var db = await NewDatabaseAsync();

        var result = await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);

        Assert.Equal(DatabaseClassification.Empty, result.Classification);
        Assert.Equal(new[] { 1, 2, 3 }, result.AppliedVersions);
        Assert.Equal(3, await LedgerCountAsync(db));
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_widget') IS NOT NULL"));
    }

    // ---------------------------------------------------------------- P2-02
    [SkippableFact]
    public async Task P2_02_LegacyDatabaseWithoutConfirmationFailsBeforeAnyMutation()
    {
        await using var db = await NewLegacyDatabaseAsync();

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal("legacy_schema_requires_migration", ex.Code);
        // 沒有 DDL、沒有刪除、連 metadata schema 都沒建。
        Assert.True(await MetaAbsentAsync(db));
        Assert.Equal(7, await db.QueryScalarAsync<long>("SELECT count(*) FROM fx_legacy_a"));
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_widget') IS NULL"));
    }

    // ------------------------------------------------------------ P2-03/P2-12
    [SkippableFact]
    public async Task P2_03_And_P2_12_ConfirmedLegacyResetCommitsSchemaAuditAndLedgerAtomically()
    {
        await using var db = await NewLegacyDatabaseAsync();

        var result = await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Destructive);

        Assert.Equal(DatabaseClassification.KnownLegacy, result.Classification);
        Assert.Equal(new[] { 1, 2, 3 }, result.AppliedVersions);
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_legacy_a') IS NULL"));
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_widget_item') IS NOT NULL"));
        Assert.Equal(3, await LedgerCountAsync(db));

        // P2-12:稽核列數必須與清理前盤點一致,而且欄位只有識別與計數 —— 永不存 row 內容。
        var audit = await db.QueryAsync<(string TableName, long DeletedRowCount)>(
            "SELECT table_name, deleted_row_count FROM springaitest_meta.migration_cleanup_audit ORDER BY table_name");
        Assert.Equal([("fx_legacy_a", 7L), ("fx_legacy_b", 3L)], audit);

        var auditColumns = await db.QueryAsync<string>(
            "SELECT column_name FROM information_schema.columns"
            + " WHERE table_schema='springaitest_meta' AND table_name='migration_cleanup_audit'"
            + " ORDER BY column_name");
        Assert.Equal(
            ["deleted_row_count", "execution_id", "migration_version", "reset_at", "table_name"],
            auditColumns);

        var executions = await db.QueryAsync<Guid>(
            "SELECT DISTINCT execution_id FROM springaitest_meta.migration_cleanup_audit");
        Assert.Equal([result.ExecutionId], executions);
    }

    // ---------------------------------------------------------------- P2-06
    [SkippableFact]
    public async Task P2_06_BundleStatementFailureRollsBackWholeBundleAndKeepsLegacyData()
    {
        await using var db = await NewLegacyDatabaseAsync();
        // 目標表名異常地已存在於 legacy 庫:0001 只清 legacy 表,0002 建表時撞名 → 整批必須回滾。
        await db.ExecuteAsync("CREATE TABLE fx_widget (id integer); INSERT INTO fx_widget VALUES (1), (2);");

        await Assert.ThrowsAsync<PostgresException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Destructive));

        Assert.True(await MetaAbsentAsync(db));
        Assert.Equal(7, await db.QueryScalarAsync<long>("SELECT count(*) FROM fx_legacy_a"));
        Assert.Equal(3, await db.QueryScalarAsync<long>("SELECT count(*) FROM fx_legacy_b"));
        Assert.Equal(2, await db.QueryScalarAsync<long>("SELECT count(*) FROM fx_widget"));
    }

    // ---------------------------------------------------------------- P2-07
    [SkippableFact]
    public async Task P2_07_ProcessKilledMidMigrationRollsBackReleasesLockAndRerunSucceeds()
    {
        await using var db = await NewDatabaseAsync();

        // 模擬「migration 進行到一半行程被砍」:持鎖 + 已做 DDL,然後直接砍掉該 backend。
        await using (var victim = new NpgsqlConnection(db.ConnectionString))
        {
            await victim.OpenAsync();
            var pid = await victim.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
            await using var tx = await victim.BeginTransactionAsync();
            await victim.ExecuteAsync(new CommandDefinition(
                "SELECT pg_try_advisory_xact_lock(@key)",
                new { key = DbMigrationRunner.AdvisoryLockKey },
                transaction: tx));
            await victim.ExecuteAsync(new CommandDefinition(
                "CREATE TABLE fx_half_written (id integer)", transaction: tx));

            await using var executioner = new NpgsqlConnection(db.ConnectionString);
            await executioner.OpenAsync();
            await executioner.ExecuteScalarAsync<bool>(
                "SELECT pg_terminate_backend(@pid)", new { pid });
        }

        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_half_written') IS NULL"));

        // 鎖隨連線關閉釋放,重跑順利完成。
        var result = await Runner(MigrationFixtures.Bundle, lockTimeoutSeconds: 5)
            .RunAsync(db.ConnectionString, MigrationMode.Startup);
        Assert.Equal(new[] { 1, 2, 3 }, result.AppliedVersions);
    }

    // ---------------------------------------------------------------- P2-08
    [SkippableFact]
    public async Task P2_08_RerunWithSameVersionAndChecksumIsNoOp()
    {
        await using var db = await NewLegacyDatabaseAsync();
        await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Destructive);

        var rerun = await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Destructive);

        Assert.Equal(DatabaseClassification.KnownCurrent, rerun.Classification);
        Assert.Empty(rerun.AppliedVersions);
        Assert.Equal(3, await LedgerCountAsync(db));
        // 沒有第二次清理:稽核仍然只有第一次那兩列。
        Assert.Equal(2, await db.QueryScalarAsync<long>(
            "SELECT count(*) FROM springaitest_meta.migration_cleanup_audit"));
    }

    // ---------------------------------------------------------------- P2-09
    [SkippableFact]
    public async Task P2_09_ChangedChecksumFailsClosedBeforeExecutingPendingSql()
    {
        await using var db = await NewDatabaseAsync();
        await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);

        // 回到「只套用過 0001,而它的內容已與 binary 不符」的狀態,讓 0002/0003 仍是 pending。
        await db.ExecuteAsync("""
            DROP TABLE fx_widget_item;
            DROP TABLE fx_widget;
            DELETE FROM springaitest_meta.schema_migration WHERE version > 1;
            UPDATE springaitest_meta.schema_migration SET checksum = repeat('0', 64) WHERE version = 1;
            CREATE TABLE fx_legacy_a (id integer);
            """);

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal("migration_checksum_drift", ex.Code);
        // pending 的 0002 沒被執行。
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_widget') IS NULL"));
        Assert.Equal(1, await LedgerCountAsync(db));
    }

    // ---------------------------------------------------------------- P2-10
    [SkippableFact]
    public async Task P2_10_ConcurrentRunnersSerializeAndApplyExactlyOnce()
    {
        await using var db = await NewDatabaseAsync();

        var first = Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);
        var second = Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(r => r.AppliedVersions.Count == 3));
        Assert.Equal(1, results.Count(r => r.AppliedVersions.Count == 0));
        Assert.Equal(3, await LedgerCountAsync(db));
        Assert.Equal(0, await db.QueryScalarAsync<long>(
            "SELECT count(*) FROM springaitest_meta.migration_cleanup_audit"));
    }

    // --------------------------------------------------------------- P2-10a
    [SkippableFact]
    public async Task P2_10a_LockTimeoutGivesStableDiagnosticWithoutMutation()
    {
        await using var db = await NewDatabaseAsync();
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)",
            new { key = DbMigrationRunner.AdvisoryLockKey },
            transaction: holderTx));

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle, lockTimeoutSeconds: DbMigrationRunner.MinLockTimeoutSeconds)
                .RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal("migration_lock_timeout", ex.Code);
        Assert.True(await MetaAbsentAsync(db));
    }

    // --------------------------------------------------------------- P2-10a
    [SkippableFact]
    public async Task P2_10a_CallerCancellationPropagatesWithoutHangingOrMutating()
    {
        await using var db = await NewDatabaseAsync();
        await using var holder = new NpgsqlConnection(db.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)",
            new { key = DbMigrationRunner.AdvisoryLockKey },
            transaction: holderTx));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner(MigrationFixtures.Bundle, lockTimeoutSeconds: DbMigrationRunner.MaxLockTimeoutSeconds)
                .RunAsync(db.ConnectionString, MigrationMode.Startup, cts.Token));

        Assert.True(await MetaAbsentAsync(db));
    }

    // ---------------------------------------------------------------- P2-11
    [SkippableFact]
    public async Task P2_11_PostconditionFailureRecordsNoCompletionRow()
    {
        await using var db = await NewDatabaseAsync();

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.BundleWithFailingPostcondition)
                .RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal("migration_postcondition_failed", ex.Code);
        Assert.True(await MetaAbsentAsync(db));
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_widget') IS NULL"));
    }

    /// <summary>
    /// pending = 0 不代表可以直接 no-op:ledger 完整但目標表被手動 drop 掉時,
    /// postcondition 必須在 commit 前失敗,而不是把一個壞掉的資料庫回報成最新。
    /// </summary>
    [SkippableFact]
    public async Task KnownCurrentWithMissingTargetTableFailsPostconditionInsteadOfNoOp()
    {
        await using var db = await NewDatabaseAsync();
        await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);
        // 只 drop 其中一張目標表:public 仍有應用物件,所以分類還是 KnownCurrent。
        await db.ExecuteAsync("DROP TABLE fx_widget_item;");

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal("migration_postcondition_failed", ex.Code);
        Assert.Equal(3, await LedgerCountAsync(db));
    }

    /// <summary>
    /// LangGraph AsyncPostgresSaver 的四張 checkpoint 表建在同一個庫的 public,
    /// legacy 庫裡出現它們是預期的 SpringAITest 物件,不是 unknown。
    /// </summary>
    [SkippableFact]
    public async Task LangGraphCheckpointTablesAreKnownLegacyObjectsNotUnknown()
    {
        await using var db = await NewLegacyDatabaseAsync("""
            CREATE TABLE tenants (id text PRIMARY KEY);
            CREATE TABLE checkpoints (thread_id text NOT NULL, checkpoint_id text NOT NULL);
            CREATE TABLE checkpoint_blobs (thread_id text NOT NULL, channel text NOT NULL);
            CREATE TABLE checkpoint_writes (thread_id text NOT NULL, task_id text NOT NULL);
            CREATE TABLE checkpoint_migrations (v integer PRIMARY KEY);
            """);

        var manifest = MigrationManifest.FromAssembly(
            typeof(MigrationFixtures).Assembly,
            MigrationFixtures.ResourcePrefix("Sql."),
            MigrationFixtures.ResourcePrefix("Postconditions."),
            bundleThroughVersion: 3,
            MigrationManifest.SpringAITestLegacyObjects,
            ["plpgsql", "vector"]);

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(manifest).RunAsync(db.ConnectionString, MigrationMode.Startup));

        // known legacy 才會走到「需要 migrate-db」;被當成 unknown 的話會是 unknown_database。
        Assert.Equal("legacy_schema_requires_migration", ex.Code);
    }

    /// <summary>
    /// manifest 顯式宣告 bundle 上界 = 2 而檔案有三個:第一批(1–2)是一個交易,
    /// 第 3 檔自成一個交易。第 3 檔失敗只回滾它自己,已 commit 的 bundle 留著。
    /// </summary>
    [SkippableFact]
    public async Task DeclaredBundleCommitsAtomicallyAndLaterMigrationFailsInItsOwnTransaction()
    {
        await using var db = await NewDatabaseAsync();

        await Assert.ThrowsAsync<PostgresException>(() =>
            Runner(MigrationFixtures.BundleBoundary).RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal([1, 2], await db.QueryAsync<int>(
            "SELECT version FROM springaitest_meta.schema_migration ORDER BY version"));
        Assert.True(await db.QueryScalarAsync<bool>("SELECT to_regclass('public.fx_widget') IS NOT NULL"));
        Assert.Equal(0, await db.QueryScalarAsync<long>("SELECT count(*) FROM fx_widget_item"));
    }

    // ---------------------------------------------------------------- P2-15
    [SkippableTheory]
    [InlineData("DELETE FROM springaitest_meta.schema_migration WHERE version = 2", "migration_ledger_gap")]
    [InlineData(
        "INSERT INTO springaitest_meta.schema_migration (version, name, checksum, duration_ms)"
        + " VALUES (4, 'future', repeat('a', 64), 1)",
        "migration_version_ahead_of_binary")]
    [InlineData(
        "UPDATE springaitest_meta.schema_migration SET name = 'renamed' WHERE version = 1",
        "migration_checksum_drift")]
    public async Task P2_15_AppliedManifestDriftGapOrFutureVersionFailsClosed(string corruption, string expectedCode)
    {
        await using var db = await NewDatabaseAsync();
        await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);
        await db.ExecuteAsync(corruption);

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal(expectedCode, ex.Code);
    }

    /// <summary>
    /// P2-15 的 manifest 側:同版本重複(以及任何不連續)在 manifest 建構期就炸,
    /// 根本進不了資料庫。
    /// </summary>
    [Fact]
    public void P2_15_DuplicateVersionInManifestIsRejectedAtBuildTime()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MigrationFixtures.Build("InvalidSql.", "Postconditions.", bundleThroughVersion: 1));

        Assert.Contains("連續", ex.Message);
    }

    /// <summary>
    /// 03-design §1.2:migration SQL 不得含 CREATE INDEX CONCURRENTLY / VACUUM 這類非交易操作,
    /// 也不得自己下 COMMIT / ROLLBACK / SAVEPOINT 另開交易邊界 ——
    /// 兩者都會讓「整批回滾」的保證靜默消失。這條在 manifest 建構期擋掉。
    /// </summary>
    [Theory]
    [InlineData("NonTransactionalSql.")]
    [InlineData("TransactionControlSql.")]
    public void NonTransactionalOrTransactionControlSqlIsRejectedAtBuildTime(string scriptFolder)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MigrationFixtures.Build(scriptFolder, "Postconditions.", bundleThroughVersion: 1));

        Assert.Contains("非交易操作", ex.Message);
    }

    /// <summary>
    /// 黑名單刻意不含 <c>BEGIN</c>:fixture 0001 是 <c>DO $$ ... BEGIN ... END $$</c>,
    /// 加了 BEGIN 會把所有 PL/pgSQL migration 一起誤殺。
    /// </summary>
    [Fact]
    public void PlpgsqlDoBlockWithBeginIsNotTreatedAsTransactionControl()
    {
        Assert.Contains(MigrationFixtures.Bundle.Scripts, s => s.Sql.Contains("BEGIN", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- P2-16
    [SkippableFact]
    public async Task P2_16_ExtensionOwnedObjectsInPublicAreNotUnknownObjects()
    {
        await using var db = await NewLegacyDatabaseAsync();
        // pgvector 因為 CREATE EXTENSION 不帶 SCHEMA 子句而裝進 public;
        // pg_stat_statements 更進一步:它在 public 留下兩個 extension-owned 的 view。
        await db.ExecuteAsync("CREATE EXTENSION vector; CREATE EXTENSION pg_stat_statements;");

        var manifest = MigrationManifest.FromAssembly(
            typeof(MigrationFixtures).Assembly,
            MigrationFixtures.ResourcePrefix("Sql."),
            MigrationFixtures.ResourcePrefix("Postconditions."),
            bundleThroughVersion: 3,
            ["fx_legacy_a", "fx_legacy_b", "fx_widget", "fx_widget_item"],
            ["vector", "pg_stat_statements"]);

        var result = await Runner(manifest).RunAsync(db.ConnectionString, MigrationMode.Destructive);

        Assert.Equal(DatabaseClassification.KnownLegacy, result.Classification);
    }

    [SkippableFact]
    public async Task P2_16_ObjectsOwnedByNonAllowlistedExtensionAreUnknownObjects()
    {
        await using var db = await NewLegacyDatabaseAsync();
        await db.ExecuteAsync("CREATE EXTENSION vector; CREATE EXTENSION pg_stat_statements;");

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Destructive));

        Assert.Equal("unknown_database", ex.Code);
        Assert.Contains("pg_stat_statements", ex.Message);
        Assert.True(await MetaAbsentAsync(db));
    }

    [SkippableFact]
    public async Task P2_16_UnexpectedObjectAbortsResetAndNamesIt()
    {
        await using var db = await NewLegacyDatabaseAsync();
        await db.ExecuteAsync("CREATE TABLE operator_scratch (id integer);");

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Destructive));

        Assert.Equal("unknown_database", ex.Code);
        Assert.Contains("operator_scratch", ex.Message);
        // 不刪任何東西。
        Assert.Equal(7, await db.QueryScalarAsync<long>("SELECT count(*) FROM fx_legacy_a"));
    }

    /// <summary>
    /// ledger 有紀錄但 public 沒有應用物件 = 不一致,fail closed 且不自動修復(02-spec §6)。
    /// </summary>
    [SkippableFact]
    public async Task LedgerWithoutSchemaFailsClosed()
    {
        await using var db = await NewDatabaseAsync();
        await Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup);
        await db.ExecuteAsync("DROP TABLE fx_widget_item; DROP TABLE fx_widget;");

        var ex = await Assert.ThrowsAsync<DbMigrationException>(() =>
            Runner(MigrationFixtures.Bundle).RunAsync(db.ConnectionString, MigrationMode.Startup));

        Assert.Equal("ledger_schema_mismatch", ex.Code);
    }

    /// <summary>lock timeout 只允許 5–300 秒;on-point 通過、off-point 拒絕(03-design §1.2)。</summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(4, false)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void LockTimeoutIsBoundedToTheConfigurableRange(int seconds, bool accepted)
    {
        if (accepted)
        {
            _ = new DbMigrationRunner(MigrationFixtures.Bundle, NullLogger.Instance, seconds);
            return;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DbMigrationRunner(MigrationFixtures.Bundle, NullLogger.Instance, seconds));
    }
}
