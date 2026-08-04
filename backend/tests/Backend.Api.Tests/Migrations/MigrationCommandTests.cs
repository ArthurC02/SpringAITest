using Backend.Api.Data.Migrations;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// migrate-db 的參數層決策:模式選擇與 lock-timeout 解析。
/// 兩者都在任何連線之前決定,所以這裡不需要資料庫。
/// </summary>
public sealed class MigrationCommandTests
{
    private const string LocalTarget =
        "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest_mig_test_0011223344556677";

    private static readonly MigrationTarget Target = MigrationTarget.Parse(LocalTarget);

    private static MigrationMode Resolve(bool destructiveEnv, params string[] args)
        => MigrationCommand.ResolveMode(
            Target, MigrationCommand.ParseArgs(["migrate-db", .. args]), destructiveEnv);

    /// <summary>env × confirm 四象限:只有兩者同時成立且確認詞精確時才是破壞模式。</summary>
    [Fact]
    public void NeitherEnvNorConfirmationStaysInStartupMode()
        => Assert.Equal(MigrationMode.Startup, Resolve(destructiveEnv: false));

    [Fact]
    public void ConfirmationWithoutEnvIsRefused()
    {
        var ex = Assert.Throws<DbMigrationException>(() =>
            Resolve(destructiveEnv: false, "--confirm", Target.ConfirmationToken));

        Assert.Equal("destructive_env_required", ex.Code);
    }

    /// <summary>
    /// <c>--confirm</c> 出現但沒帶值是打錯字,不是「沒有要求破壞」:
    /// 必須落到 confirmation_required,不得靜默降級成啟動模式。
    /// </summary>
    [Theory]
    [InlineData("")]                                // 完全沒有 --confirm
    [InlineData("--confirm")]                       // --confirm 出現但沒帶值
    [InlineData("--confirm|RESET some-other-db")]   // 確認詞不是這個目標的
    public void EnvWithoutExactConfirmationIsRefused(string pipeSeparatedArgs)
    {
        string[] args = pipeSeparatedArgs.Length == 0 ? [] : pipeSeparatedArgs.Split('|');

        var ex = Assert.Throws<DbMigrationException>(() => Resolve(destructiveEnv: true, args));

        Assert.Equal("confirmation_required", ex.Code);
    }

    [Fact]
    public void EnvWithExactConfirmationSelectsDestructiveMode()
        => Assert.Equal(
            MigrationMode.Destructive,
            Resolve(destructiveEnv: true, "--confirm", Target.ConfirmationToken));

    [Fact]
    public void LockTimeoutDefaultsWhenTheOptionIsAbsent()
        => Assert.Equal(
            DbMigrationRunner.DefaultLockTimeoutSeconds,
            MigrationCommand.ParseLockTimeout(MigrationCommand.ParseArgs(["migrate-db"])));

    /// <summary>5–300 秒:on-point 接受,off-point 與不可解析都是穩定診斷而非未捕捉例外。</summary>
    [Theory]
    [InlineData("5", 5)]
    [InlineData("300", 300)]
    [InlineData("4", null)]
    [InlineData("301", null)]
    [InlineData("abc", null)]
    public void LockTimeoutIsBoundedAndFailsWithAStableDiagnostic(string raw, int? expected)
    {
        var options = MigrationCommand.ParseArgs(["migrate-db", "--lock-timeout", raw]);

        if (expected is not null)
        {
            Assert.Equal(expected, MigrationCommand.ParseLockTimeout(options));
            return;
        }

        var ex = Assert.Throws<DbMigrationException>(() => MigrationCommand.ParseLockTimeout(options));
        Assert.Equal("invalid_lock_timeout", ex.Code);
    }

    [Fact]
    public void LockTimeoutWithoutAValueIsRefused()
    {
        var ex = Assert.Throws<DbMigrationException>(() =>
            MigrationCommand.ParseLockTimeout(MigrationCommand.ParseArgs(["migrate-db", "--lock-timeout"])));

        Assert.Equal("invalid_lock_timeout", ex.Code);
    }
}
