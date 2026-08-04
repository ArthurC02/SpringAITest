using Npgsql;

namespace Backend.Api.Data.Migrations;

/// <summary>
/// migrate-db 的目標解析與守門(03-design §1.3 末段)。
/// 「先解析並列印精確目標,再問確認」的順序是刻意的:操作員確認的是他看到的那一行,
/// 不是他以為的環境變數。空變數與系統資料庫在任何模式下都拒絕。
/// </summary>
public sealed record MigrationTarget(string Host, int Port, string Database, string User)
{
    /// <summary>永不接受的資料庫名。空變數推導出的預設目標多半就落在 <c>postgres</c>。</summary>
    private static readonly string[] ProtectedDatabases = ["postgres", "template0", "template1"];

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public bool IsLocal => LoopbackHosts.Contains(Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>破壞性確認詞必須含資料庫名,貼上別台機器的確認詞不會通過。</summary>
    public string ConfirmationToken => $"RESET {Database}";

    /// <summary>遠端第二道確認詞:精確 host/database。</summary>
    public string RemoteConfirmationToken => $"{Host}/{Database}";

    public string Describe(string? composeProject) =>
        $"host={Host} port={Port} database={Database} user={User}"
        + (string.IsNullOrWhiteSpace(composeProject) ? "" : $" composeProject={composeProject}");

    /// <summary>
    /// 由連線字串解析目標。任一必要欄位空白即拒絕 —— 空變數不得被推導成一個「預設」目標。
    /// </summary>
    public static MigrationTarget Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new DbMigrationException("invalid_migration_target", "連線字串為空,拒絕推導預設目標。");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new DbMigrationException("invalid_migration_target", $"連線字串無法解析:{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(builder.Host)
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new DbMigrationException(
                "invalid_migration_target",
                "連線字串缺少 Host/Database/Username 其中之一,拒絕推導預設目標。");
        }

        var target = new MigrationTarget(builder.Host, builder.Port, builder.Database, builder.Username);
        if (ProtectedDatabases.Contains(target.Database, StringComparer.OrdinalIgnoreCase))
        {
            throw new DbMigrationException(
                "protected_database",
                $"拒絕以受保護的系統資料庫 `{target.Database}` 為 migration 目標。");
        }

        return target;
    }

    /// <summary>
    /// 破壞性執行的完整守門。通過才允許 <see cref="MigrationMode.Destructive"/>。
    /// 遠端目標需要 <c>--allow-remote</c> 與第二次精確 host/database 確認,兩者缺一不可。
    /// </summary>
    public void RequireDestructiveConfirmation(string? confirmation, bool allowRemote, string? remoteConfirmation)
    {
        if (!string.Equals(confirmation, ConfirmationToken, StringComparison.Ordinal))
        {
            throw new DbMigrationException(
                "confirmation_required",
                $"破壞性 migration 需要精確確認詞 `{ConfirmationToken}`。");
        }

        if (IsLocal)
        {
            return;
        }

        if (!allowRemote)
        {
            throw new DbMigrationException(
                "remote_target_requires_flag",
                $"目標 `{Host}` 不是本機,需要 --allow-remote 才能對遠端資料庫執行破壞性 migration。");
        }

        if (!string.Equals(remoteConfirmation, RemoteConfirmationToken, StringComparison.Ordinal))
        {
            throw new DbMigrationException(
                "remote_confirmation_required",
                $"遠端目標需要第二次精確確認 `{RemoteConfirmationToken}`。");
        }
    }
}
