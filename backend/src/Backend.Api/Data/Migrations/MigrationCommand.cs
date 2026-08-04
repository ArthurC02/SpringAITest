using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Data.Migrations;

/// <summary>
/// 專用 migration 行程(<c>dotnet run --project src/Backend.Api -- migrate-db …</c>)。
/// 這是唯一接受 <c>ALLOW_DESTRUCTIVE_MIGRATION</c> 的地方,而且必須連同命令列確認一起出現;
/// Backend 一般啟動完全不讀這個變數,也不會執行 <see cref="DbMigrationRunner"/>(02-spec §6)。
/// </summary>
public static class MigrationCommand
{
    public const string CommandName = "migrate-db";
    public const string DestructiveEnvName = "ALLOW_DESTRUCTIVE_MIGRATION";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            var options = ParseArgs(args);
            var connectionString = options.GetValueOrDefault("connection")
                ?? Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            var target = MigrationTarget.Parse(connectionString);

            // 先印出解析後的精確目標,再談確認 —— 確認的是眼前這一行。
            Console.WriteLine($"migration 目標:{target.Describe(options.GetValueOrDefault("compose-project"))}");

            var destructiveEnv = string.Equals(
                Environment.GetEnvironmentVariable(DestructiveEnvName), "true", StringComparison.OrdinalIgnoreCase);
            var mode = ResolveMode(target, options, destructiveEnv);
            if (mode == MigrationMode.Destructive)
            {
                Console.WriteLine("確認通過,以專用破壞模式執行。");
            }

            var runner = new DbMigrationRunner(
                MigrationManifest.Production, NullLogger.Instance, ParseLockTimeout(options));
            var result = await runner.RunAsync(connectionString!, mode, ct);

            Console.WriteLine(result.AppliedVersions.Count == 0
                ? $"資料庫分類:{result.Classification};沒有待套用的 migration。"
                : $"資料庫分類:{result.Classification};已套用版本 {string.Join(",", result.AppliedVersions)}。");
            return 0;
        }
        catch (DbMigrationException ex)
        {
            Console.Error.WriteLine($"migrate-db 失敗 [{ex.Code}]:{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 模式選擇。破壞意圖由「<c>--confirm</c> 是否出現」判斷,而不是它有沒有值 ——
    /// <c>--confirm</c> 後面漏打確認詞是打錯字,不是「沒有要求破壞」,必須落到 confirmation_required
    /// 而不是靜默降級成啟動模式。
    /// </summary>
    internal static MigrationMode ResolveMode(
        MigrationTarget target,
        IReadOnlyDictionary<string, string?> options,
        bool destructiveEnv)
    {
        if (!destructiveEnv && !options.ContainsKey("confirm"))
        {
            return MigrationMode.Startup;
        }

        if (!destructiveEnv)
        {
            throw new DbMigrationException(
                "destructive_env_required",
                $"破壞性 migration 需要同時設定 {DestructiveEnvName}=true 與命令列確認詞。");
        }

        target.RequireDestructiveConfirmation(
            options.GetValueOrDefault("confirm"),
            options.ContainsKey("allow-remote"),
            options.GetValueOrDefault("confirm-remote"));
        return MigrationMode.Destructive;
    }

    /// <summary>
    /// <c>--lock-timeout</c> 未給就用預設;給了就必須是 runner 接受的範圍內整數。
    /// 不可解析或超界一律是穩定診斷,不是未捕捉的 ArgumentOutOfRangeException。
    /// </summary>
    internal static int ParseLockTimeout(IReadOnlyDictionary<string, string?> options)
    {
        if (!options.TryGetValue("lock-timeout", out var raw))
        {
            return DbMigrationRunner.DefaultLockTimeoutSeconds;
        }

        if (!int.TryParse(raw, out var parsed)
            || parsed < DbMigrationRunner.MinLockTimeoutSeconds
            || parsed > DbMigrationRunner.MaxLockTimeoutSeconds)
        {
            throw new DbMigrationException(
                "invalid_lock_timeout",
                $"--lock-timeout 只允許 {DbMigrationRunner.MinLockTimeoutSeconds}–{DbMigrationRunner.MaxLockTimeoutSeconds}"
                + $" 之間的整數秒,收到 `{raw ?? "(未給值)"}`。");
        }

        return parsed;
    }

    /// <summary>`--flag` 與 `--key value` 兩種形式;第一個 token 是命令名,略過。</summary>
    internal static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new DbMigrationException("invalid_argument", $"無法辨識的參數:{args[i]}");
            }

            var key = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[key] = hasValue ? args[++i] : null;
        }

        return options;
    }
}
