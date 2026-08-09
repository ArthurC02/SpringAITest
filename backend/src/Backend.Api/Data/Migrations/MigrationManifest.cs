using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Backend.Api.Skills;

namespace Backend.Api.Data.Migrations;

/// <summary>
/// 一份已註冊的 migration SQL。<see cref="Sql"/> 是「正規化後的 canonical 內容」——
/// 換行一次正規化為 LF、不做任何環境變數替換,所以同一份 embedded resource 在
/// Windows/Linux、開發/CI 算出的 <see cref="Checksum"/> 必定逐字元相同。
/// </summary>
public sealed record MigrationScript(int Version, string Name, string Sql, string Checksum);

/// <summary>
/// bundle postcondition:一句必須回傳 <c>true</c> 的 scalar 查詢。與 migration SQL 同交易執行,
/// 失敗即整批回滾且不寫任何 completion row(04-acceptance P2-11)。
/// </summary>
public sealed record MigrationPostcondition(string Name, string Sql);

/// <summary>
/// runner 唯一認得的世界:已註冊的 SQL、bundle postconditions、legacy 物件白名單、
/// 允許安裝在 <c>public</c> 的 extension 白名單。
///
/// **P2 鐵律**:<see cref="Production"/> 目前不含任何 SQL。生產 0001–0003 與其消費端在 P3
/// 一起上車(01-plan §5、02-spec §6.1),否則舊 binary 會提早建出目標 schema。
/// </summary>
public sealed class MigrationManifest
{
    /// <summary>SQL 檔名格式 <c>NNNN_name.sql</c>;版本取前綴數字,名稱取其後。</summary>
    private static readonly Regex ScriptFileName = new(@"^(\d{4})_([a-z0-9_]+)\.sql$", RegexOptions.Compiled);

    /// <summary>
    /// 交易外操作黑名單。整批 migration 必須能在單一交易內回滾;
    /// <c>CONCURRENTLY</c>/<c>VACUUM</c> 這類語句會讓「全有全無」的保證靜默消失。
    /// 真的需要它們必須另設計持久化階段狀態機,不是在這裡開後門(03-design §1.2)。
    ///
    /// 後半段是交易控制語句:migration SQL 自己 <c>COMMIT</c>/<c>ROLLBACK</c>/開 savepoint,
    /// 等於在 runner 的交易邊界內另開邊界,「整批回滾」同樣靜默失效。
    /// <c>SAVEPOINT</c> 一項即涵蓋 <c>RELEASE SAVEPOINT</c>/<c>ROLLBACK TO SAVEPOINT</c>。
    /// **刻意不含 <c>BEGIN</c>**:PL/pgSQL 的 <c>DO $$ ... BEGIN ... END $$</c> 會被誤殺。
    /// </summary>
    private static readonly Regex NonTransactional = new(
        @"\b(CONCURRENTLY|VACUUM|REINDEX\s+DATABASE|CREATE\s+DATABASE|DROP\s+DATABASE|ALTER\s+SYSTEM|CREATE\s+TABLESPACE"
        + @"|COMMIT|ROLLBACK|SAVEPOINT|START\s+TRANSACTION)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 環境替換佔位符。canonical bytes 就是出貨 bytes;一旦允許 <c>${VAR}</c>,
    /// checksum 就不再等於「實際執行的 SQL」,drift 偵測會變成裝飾品。
    /// </summary>
    private static readonly Regex EnvironmentSubstitution = new(@"\$\{|\{\{", RegexOptions.Compiled);

    /// <summary>
    /// SpringAITest 自有物件白名單(05-ledger §1.1 的基線，加上現行 document_ingest intake 表)。
    /// 這是 hard reset 分類與 extras 比對的輸入,不是 SQL —— 生產 SQL 在 P3 才出貨。
    /// 表所擁有的 identity/serial sequence 不必列名:分類查詢會把「被表擁有的 sequence」
    /// 視為該表的實作細節排除掉,擁有者本身不在白名單時那張表已經先觸發中止。
    /// </summary>
    public static readonly string[] SpringAITestApplicationTables =
    [
        "tenants", "users", "user_group_membership", "conversations", "rag_documents", "document_ingest", "rag_chunks",
        "app_config", "skill", "skill_revision", "configuration_set", "agent", "agent_revision",
        "agent_revision_skill", "workflow", "workflow_revision", "orchestrator", "orchestrator_revision",
        "agent_run", "agent_run_approval", "agent_run_approval_decision", "agent_run_write_effect",
        "agent_run_approval_execute", "agent_run_write_outbox", "orchestrator_run", "tenant_runtime_binding",
        "operations_regression_result", "operations_regression_override", "operations_release_audit",
        "operations_execution_metric", "operations_run_evidence", "orchestrator_run_event",
        "orchestrator_run_command", "orchestrator_run_child", "agent_run_skill", "agent_run_event",
        "agent_run_command", "context_policy", "source_catalog", "metric_definition", "context_revision",
        "context_evidence", "context_view", "context_request", "context_delta", "eval_suite",
        "eval_suite_revision", "eval_run", "eval_case_result", "prompt_component_revision",
        "prompt_manifest_revision", "checkpoint_retention_ack", "agent_trigger", "agent_trigger_occurrence",
    ];

    public static readonly string[] OptionalWorkflowCheckpointTables =
    [
        // Workflow owns these five checkpoint tables. They are not Backend application tables, but
        // workflow/app/runtime/checkpoints.py creates them in the same springaitest public schema,
        // 所以 legacy 庫裡出現它們是預期而非 unknown。hard reset 一併刪除是刻意的:
        // 執行狀態(未完成的 run/interrupt)綁在被重置的 schema 上,留著只會指向已不存在的資料。
        "checkpoints", "checkpoint_blobs", "checkpoint_writes", "checkpoint_migrations",
        "workflow_root_context_checkpoint",
    ];

    public static readonly string[] SpringAITestLegacyObjects =
        [.. SpringAITestApplicationTables, .. OptionalWorkflowCheckpointTables];

    private MigrationManifest(
        IReadOnlyList<MigrationScript> scripts,
        IReadOnlyList<MigrationPostcondition> postconditions,
        int bundleThroughVersion,
        IReadOnlySet<string> legacyObjectAllowlist,
        IReadOnlySet<string> allowedExtensions)
    {
        Scripts = scripts;
        Postconditions = postconditions;
        BundleThroughVersion = bundleThroughVersion;
        LegacyObjectAllowlist = legacyObjectAllowlist;
        AllowedExtensions = allowedExtensions;
    }

    public IReadOnlyList<MigrationScript> Scripts { get; }

    public IReadOnlyList<MigrationPostcondition> Postconditions { get; }

    /// <summary>
    /// 初始 bundle 的最後一個版本(含)。到這個版本為止的 pending script 在同一個交易內全有全無;
    /// 之後一律一檔一交易。postcondition 描述的是 bundle 完成後的目標狀態,
    /// 所以只有在某一批推進到這個版本(或更高)之後才要求成立 —— bundle 中間狀態不驗。
    /// P2 的生產 manifest 沒有 SQL,值為 0;P3 隨 0001–0003 一起設為 3。
    /// </summary>
    public int BundleThroughVersion { get; }

    /// <summary>
    /// 「已知 legacy SpringAITest」分類的精確物件白名單(05-ledger §1.1)。
    /// <c>public</c> 內出現白名單以外的 relation 就是 unknown,一律中止而非默默 drop。
    /// </summary>
    public IReadOnlySet<string> LegacyObjectAllowlist { get; }

    /// <summary>
    /// 允許安裝到 <c>public</c> 的 extension。被這些 extension 擁有(<c>pg_depend.deptype='e'</c>)
    /// 的物件永遠不列入 unexpected —— pgvector 正是這樣裝進 public 的(03-design §1.3)。
    /// </summary>
    public IReadOnlySet<string> AllowedExtensions { get; }

    public int MaxVersion => Scripts.Count == 0 ? 0 : Scripts[^1].Version;

    /// <summary>
    /// 正常啟動的 binary 所註冊的 manifest。**P2 期間刻意為空**:沒有任何生產 SQL 出貨,
    /// 所以 <c>DbBootstrap</c> 仍是 appdb 與共用測試庫的唯一 schema 權威(02-spec §6.1)。
    /// </summary>
    public static MigrationManifest Production { get; } = FromAssembly(
        typeof(MigrationManifest).Assembly,
        scriptPrefix: "Backend.Api.Data.Migrations.Sql.",
        postconditionPrefix: "Backend.Api.Data.Migrations.Postconditions.",
        bundleThroughVersion: 0,
        legacyObjectAllowlist: SpringAITestLegacyObjects,
        allowedExtensions: new[] { "plpgsql", "vector" });

    /// <summary>
    /// 由 assembly 的 embedded resources 建 manifest。resource 名稱即檔名,
    /// 目錄分隔在編譯期已轉成點號。
    /// </summary>
    public static MigrationManifest FromAssembly(
        Assembly assembly,
        string scriptPrefix,
        string postconditionPrefix,
        int bundleThroughVersion,
        IEnumerable<string> legacyObjectAllowlist,
        IEnumerable<string> allowedExtensions)
    {
        var names = assembly.GetManifestResourceNames();

        var scripts = new List<MigrationScript>();
        foreach (var resource in names.Where(n => n.StartsWith(scriptPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var fileName = resource[scriptPrefix.Length..];
            var match = ScriptFileName.Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"migration resource 檔名不合格式 NNNN_name.sql:{fileName}");
            }

            var sql = ReadCanonical(assembly, resource);
            ValidateSql(fileName, sql);
            scripts.Add(new MigrationScript(
                int.Parse(match.Groups[1].Value),
                match.Groups[2].Value,
                sql,
                SkillHash.Sha256(sql)));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < scripts.Count; i++)
        {
            var expected = i + 1;
            if (scripts[i].Version != expected)
            {
                throw new InvalidOperationException(
                    $"migration 版本必須從 1 起連續且不重複,在第 {i + 1} 個檔案讀到版本 {scripts[i].Version}(預期 {expected})");
            }
        }

        var postconditions = names
            .Where(n => n.StartsWith(postconditionPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(resource => new MigrationPostcondition(
                resource[postconditionPrefix.Length..],
                ReadCanonical(assembly, resource)))
            .ToList();

        return new MigrationManifest(
            scripts,
            postconditions,
            bundleThroughVersion,
            legacyObjectAllowlist.ToHashSet(StringComparer.Ordinal),
            allowedExtensions.ToHashSet(StringComparer.Ordinal));
    }

    private static string ReadCanonical(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"讀不到 embedded resource:{resource}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // 換行只在這裡正規化一次,之後 checksum 與執行都用同一份字串。
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\r", "\n");
    }

    // ponytail: 黑名單直接掃原文,不剖析 SQL —— 註解裡寫到 COMMIT 之類的字也會被擋。
    // 誤判方向是「拒絕一份安全的檔案」(fail closed),改用註解剝離要先有真正的 dollar-quote 感知
    // tokenizer,值不回票價;真的撞到就改寫那句註解。
    private static void ValidateSql(string fileName, string sql)
    {
        var nonTransactional = NonTransactional.Match(sql);
        if (nonTransactional.Success)
        {
            throw new InvalidOperationException(
                $"{fileName} 含非交易操作 `{nonTransactional.Value}`,無法保證整批回滾");
        }

        if (EnvironmentSubstitution.IsMatch(sql))
        {
            throw new InvalidOperationException(
                $"{fileName} 含環境替換佔位符,migration SQL 必須是不可變的 canonical 內容");
        }
    }
}
