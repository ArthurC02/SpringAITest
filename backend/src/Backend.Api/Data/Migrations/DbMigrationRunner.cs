using System.Diagnostics;
using Dapper;
using Npgsql;

namespace Backend.Api.Data.Migrations;

/// <summary>執行模式。破壞性分支只有 <see cref="Destructive"/> 走得到,而它只由 migrate-db 指令建構。</summary>
public enum MigrationMode
{
    /// <summary>啟動模式:驗 checksum、套用已註冊的安全 pending;遇到 legacy schema 一律以操作員指示失敗。</summary>
    Startup,

    /// <summary>專用破壞模式:migrate-db 以精確 confirmation 觸發後才成立。</summary>
    Destructive,
}

/// <summary>鎖內分類結果(03-design §1.3 步驟 1)。</summary>
public enum DatabaseClassification
{
    Empty,
    KnownLegacy,
    KnownCurrent,
    UnknownNonempty,
}

/// <summary>穩定診斷:<see cref="Code"/> 是機器可讀代碼,訊息面向操作員。</summary>
public sealed class DbMigrationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record MigrationRunResult(
    DatabaseClassification Classification,
    IReadOnlyList<int> AppliedVersions,
    Guid ExecutionId);

/// <summary>
/// 唯一的 SQL migration 引擎(03-design §1.1–1.2)。
///
/// **P2 期間刻意不掛在正常啟動流程上**:<c>Program.cs</c> 只在 <c>migrate-db</c> 專用行程分支呼叫它,
/// 一般啟動仍由 <c>DbBootstrap</c> 負責 appdb 與共用測試庫的 schema(02-spec §6.1)。
/// 加上 <see cref="MigrationManifest.Production"/> 在 P2 不含任何 SQL,舊 binary 不可能提早建出目標 schema。
///
/// 交易演算法:單一連線 → begin → bounded lock timeout → advisory transaction lock →
/// 鎖內分類 → 驗每一 applied row → 套用 pending SQL → postconditions → runner 寫 completion rows → 一次 commit。
/// 批次邊界由 manifest 顯式宣告的 <see cref="MigrationManifest.BundleThroughVersion"/> 決定:
/// 到該版本為止是一個交易,之後的普通 migration 一檔一交易。
/// </summary>
public sealed class DbMigrationRunner
{
    /// <summary>
    /// 固定的 advisory transaction lock key。所有 migration runner(啟動模式與 migrate-db)共用這一把,
    /// 序列化整段「分類 → 驗證 → SQL → 斷言 → 稽核 → ledger commit」。
    /// 與 <c>DbBootstrap.BootstrapAdvisoryLockId</c>(823746291)刻意不同:兩者在 P2 針對不同資料庫,
    /// 共用同一把鎖只會在 P3 切換時製造無謂的互等。
    /// </summary>
    public const long AdvisoryLockKey = 823746292;

    public const int DefaultLockTimeoutSeconds = 60;
    public const int MinLockTimeoutSeconds = 5;
    public const int MaxLockTimeoutSeconds = 300;

    /// <summary>0001 之類的 reset SQL 用 <c>current_setting</c> 讀這個 GUC 寫稽核列,避免對 SQL 原文做替換。</summary>
    public const string ExecutionIdSetting = "springaitest.migration_execution_id";

    private readonly MigrationManifest _manifest;
    private readonly ILogger _logger;
    private readonly int _lockTimeoutSeconds;

    public DbMigrationRunner(MigrationManifest manifest, ILogger logger, int lockTimeoutSeconds = DefaultLockTimeoutSeconds)
    {
        if (lockTimeoutSeconds < MinLockTimeoutSeconds || lockTimeoutSeconds > MaxLockTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lockTimeoutSeconds),
                lockTimeoutSeconds,
                $"lock timeout 只允許 {MinLockTimeoutSeconds}–{MaxLockTimeoutSeconds} 秒");
        }

        _manifest = manifest;
        _logger = logger;
        _lockTimeoutSeconds = lockTimeoutSeconds;
    }

    public async Task<MigrationRunResult> RunAsync(
        string connectionString,
        MigrationMode mode,
        CancellationToken ct = default)
    {
        var executionId = Guid.NewGuid();
        var applied = new List<int>();
        // 回報的是「開跑當下」的分類。後續交易一定看到 KnownCurrent,那不是呼叫端要知道的事。
        DatabaseClassification? initial = null;

        // 初始 bundle 是一個交易;之後每個普通 migration 各自一個交易 —— 迴圈每一圈就是一個交易。
        while (true)
        {
            var batch = await RunTransactionAsync(connectionString, mode, executionId, ct);
            initial ??= batch.Classification;
            applied.AddRange(batch.AppliedVersions);
            if (batch.AppliedVersions.Count == 0)
            {
                break;
            }
        }

        return new MigrationRunResult(initial.Value, applied, executionId);
    }

    private async Task<MigrationRunResult> RunTransactionAsync(
        string connectionString,
        MigrationMode mode,
        Guid executionId,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // SET LOCAL 只影響本交易:DDL 等表鎖時不會無限期掛住。
        await conn.ExecuteAsync(new CommandDefinition(
            $"SET LOCAL lock_timeout = '{_lockTimeoutSeconds}s'", transaction: tx, cancellationToken: ct));
        await AcquireLockAsync(conn, tx, ct);

        var classification = await ClassifyAsync(conn, tx, ct);
        GuardClassification(classification, mode);

        await EnsureMetadataAsync(conn, tx, ct);
        var applied = await ReadAppliedAsync(conn, tx, ct);
        VerifyApplied(applied);

        var pending = _manifest.Scripts.Where(s => !applied.ContainsKey(s.Version)).ToList();
        if (pending.Count == 0)
        {
            // 沒有 pending 不等於 schema 還在:ledger 完整但目標表被手動 drop 掉時,
            // 沉默 no-op 會讓一個壞掉的資料庫看起來是最新的。ledger 已涵蓋 manifest 全部版本
            // (含 bundle 上界)才有「已宣告的目標狀態」可驗;ledger 為空時不驗。
            if (ShouldVerifyTargetState(_manifest.MaxVersion))
            {
                await RunPostconditionsAsync(conn, tx, ct);
            }

            await tx.CommitAsync(ct);
            return new MigrationRunResult(classification, [], executionId);
        }

        // 初始 bundle(manifest 顯式宣告的上界)為一個交易;其後一律一檔一交易。
        var batch = pending.TakeWhile(s => s.Version <= _manifest.BundleThroughVersion).ToList();
        if (batch.Count == 0)
        {
            batch = pending.Take(1).ToList();
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "SELECT set_config(@key, @value, true)",
            new { key = ExecutionIdSetting, value = executionId.ToString() },
            transaction: tx,
            cancellationToken: ct));

        foreach (var script in batch)
        {
            var stopwatch = Stopwatch.StartNew();
            await conn.ExecuteAsync(new CommandDefinition(script.Sql, transaction: tx, cancellationToken: ct));
            stopwatch.Stop();

            // completion row 由 runner 寫,不由 SQL 自己寫(02-spec §6)。
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO springaitest_meta.schema_migration (version, name, checksum, duration_ms)"
                + " VALUES (@version, @name, @checksum, @durationMs)",
                new
                {
                    version = script.Version,
                    name = script.Name,
                    checksum = script.Checksum,
                    durationMs = (int)stopwatch.ElapsedMilliseconds,
                },
                transaction: tx,
                cancellationToken: ct));
        }

        if (ShouldVerifyTargetState(batch[^1].Version))
        {
            await RunPostconditionsAsync(conn, tx, ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "migration 已套用版本 {Versions}(分類 {Classification}、執行 {ExecutionId})",
            string.Join(",", batch.Select(s => s.Version)),
            classification,
            executionId);

        return new MigrationRunResult(classification, batch.Select(s => s.Version).ToList(), executionId);
    }

    /// <summary>
    /// postcondition 描述的是 bundle 完成後的目標狀態,bundle 中間狀態本來就不成立。
    /// 只有推進到(或已經在)manifest 宣告的 bundle 上界之後才要求它成立。
    /// </summary>
    private bool ShouldVerifyTargetState(int highestVersion)
        => highestVersion > 0 && highestVersion >= _manifest.BundleThroughVersion;

    /// <summary>
    /// 以 try-lock 輪詢取鎖:<c>pg_advisory_xact_lock</c> 的等待不吃 CancellationToken,
    /// 輪詢版同時給出「有界 timeout」與「呼叫端取消立即傳播」,兩者都零變更(P2-10a)。
    /// </summary>
    private async Task AcquireLockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_lockTimeoutSeconds);
        while (true)
        {
            var acquired = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_try_advisory_xact_lock(@key)",
                new { key = AdvisoryLockKey },
                transaction: tx,
                cancellationToken: ct));
            if (acquired)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new DbMigrationException(
                    "migration_lock_timeout",
                    $"等待 migration advisory lock 超過 {_lockTimeoutSeconds} 秒,已放棄且未做任何變更。"
                    + "請確認沒有其他 migration 行程正在執行。");
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// 分類只看 <c>public</c> 的 relation 與既有 <c>springaitest_meta</c> ledger。
    /// 排除兩類物件:被白名單 extension 擁有的(pgvector 正是如此裝進 public),
    /// 以及被某張表擁有的 identity/serial sequence(那是擁有者的實作細節)。
    /// </summary>
    private async Task<DatabaseClassification> ClassifyAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
    {
        var objects = (await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
              AND c.relkind IN ('r', 'v', 'm', 'S')
              AND NOT EXISTS (
                    SELECT 1 FROM pg_depend d
                    JOIN pg_extension e ON e.oid = d.refobjid
                    WHERE d.classid = 'pg_class'::regclass
                      AND d.objid = c.oid
                      AND d.deptype = 'e'
                      AND e.extname = ANY(@allowedExtensions))
              AND NOT EXISTS (
                    SELECT 1 FROM pg_depend d
                    WHERE d.classid = 'pg_class'::regclass
                      AND d.objid = c.oid
                      AND d.deptype = 'a'
                      AND d.refclassid = 'pg_class'::regclass)
            ORDER BY c.relname
            """,
            new { allowedExtensions = _manifest.AllowedExtensions.ToArray() },
            transaction: tx,
            cancellationToken: ct))).AsList();

        // 兩步驟:PostgreSQL 會在規劃期就解析整句,所以「表不存在時才短路」的 CASE 救不了,
        // 必須先確認 ledger 表存在再去 count。
        var ledgerExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('springaitest_meta.schema_migration') IS NOT NULL",
            transaction: tx,
            cancellationToken: ct));
        var ledgerRows = ledgerExists
            ? await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM springaitest_meta.schema_migration",
                transaction: tx,
                cancellationToken: ct))
            : 0L;

        if (ledgerRows > 0)
        {
            if (objects.Count == 0)
            {
                throw new DbMigrationException(
                    "ledger_schema_mismatch",
                    "migration ledger 有紀錄但 public 沒有任何應用物件,狀態不一致,已中止且不自動修復。");
            }

            return DatabaseClassification.KnownCurrent;
        }

        if (objects.Count == 0)
        {
            return DatabaseClassification.Empty;
        }

        var extras = objects.Where(o => !_manifest.LegacyObjectAllowlist.Contains(o)).ToList();
        if (extras.Count == 0)
        {
            return DatabaseClassification.KnownLegacy;
        }

        throw new DbMigrationException(
            "unknown_database",
            $"public 內存在白名單以外的物件,拒絕視為 SpringAITest 資料庫:{string.Join(", ", extras)}");
    }

    private static void GuardClassification(DatabaseClassification classification, MigrationMode mode)
    {
        if (classification == DatabaseClassification.KnownLegacy && mode != MigrationMode.Destructive)
        {
            throw new DbMigrationException(
                "legacy_schema_requires_migration",
                "偵測到 legacy SpringAITest schema 且沒有 hard-reset 標記。啟動流程永遠不執行破壞性分支;"
                + "請先在該資料庫執行 scripts/migrate-db.ps1 或 scripts/migrate-db.sh 並輸入精確確認詞。");
        }
    }

    private static async Task EnsureMetadataAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
        => await conn.ExecuteAsync(new CommandDefinition(
            """
            CREATE SCHEMA IF NOT EXISTS springaitest_meta;
            CREATE TABLE IF NOT EXISTS springaitest_meta.schema_migration (
              version integer PRIMARY KEY,
              name text NOT NULL,
              checksum text NOT NULL,
              applied_at timestamptz NOT NULL DEFAULT now(),
              duration_ms integer NOT NULL
            );
            -- 只存計數與識別,永不存 row 內容(02-spec §6)。
            CREATE TABLE IF NOT EXISTS springaitest_meta.migration_cleanup_audit (
              execution_id uuid NOT NULL,
              migration_version integer NOT NULL,
              table_name text NOT NULL,
              deleted_row_count bigint NOT NULL,
              reset_at timestamptz NOT NULL DEFAULT now()
            );
            """,
            transaction: tx,
            cancellationToken: ct));

    private static async Task<Dictionary<int, AppliedMigrationRow>> ReadAppliedAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        CancellationToken ct)
        => (await conn.QueryAsync<AppliedMigrationRow>(new CommandDefinition(
                "SELECT version AS Version, name AS Name, checksum AS Checksum"
                + " FROM springaitest_meta.schema_migration ORDER BY version",
                transaction: tx,
                cancellationToken: ct)))
            .ToDictionary(r => r.Version);

    /// <summary>套用任何 pending SQL 之前驗每一列:drift、重複/不連續、DB 版本新於 binary 全部 fail closed。</summary>
    private void VerifyApplied(Dictionary<int, AppliedMigrationRow> applied)
    {
        if (applied.Count == 0)
        {
            return;
        }

        var versions = applied.Keys.Order().ToList();
        for (var i = 0; i < versions.Count; i++)
        {
            if (versions[i] != i + 1)
            {
                throw new DbMigrationException(
                    "migration_ledger_gap",
                    $"migration ledger 版本不連續:預期 {i + 1},實際 {versions[i]}。");
            }
        }

        if (versions[^1] > _manifest.MaxVersion)
        {
            throw new DbMigrationException(
                "migration_version_ahead_of_binary",
                $"資料庫的 migration 版本 {versions[^1]} 新於本 binary 已註冊的最高版本 {_manifest.MaxVersion},拒絕降級執行。");
        }

        foreach (var script in _manifest.Scripts)
        {
            if (!applied.TryGetValue(script.Version, out var row))
            {
                continue;
            }

            if (row.Name != script.Name || row.Checksum != script.Checksum)
            {
                throw new DbMigrationException(
                    "migration_checksum_drift",
                    $"已套用的 migration {script.Version} 與 binary 內容不符"
                    + $"(ledger:{row.Name}/{row.Checksum};binary:{script.Name}/{script.Checksum})。"
                    + "已套用的 migration 不可變更。");
            }
        }
    }

    private async Task RunPostconditionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        foreach (var postcondition in _manifest.Postconditions)
        {
            bool satisfied;
            try
            {
                satisfied = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    postcondition.Sql, transaction: tx, cancellationToken: ct));
            }
            catch (PostgresException ex)
            {
                throw new DbMigrationException(
                    "migration_postcondition_failed",
                    $"migration postcondition `{postcondition.Name}` 執行失敗:{ex.MessageText}");
            }

            if (!satisfied)
            {
                throw new DbMigrationException(
                    "migration_postcondition_failed",
                    $"migration postcondition `{postcondition.Name}` 未通過,整批已回滾。");
            }
        }
    }

    private sealed record AppliedMigrationRow(int Version, string Name, string Checksum);
}
