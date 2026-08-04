using Backend.Api.Data.Migrations;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// 守護式指令的目標解析與確認守門(03-design §1.3 末段)。
/// 腳本負責互動與列印,拒絕規則本身在 .NET 這一份,兩邊共用同一套判斷。
/// </summary>
public sealed class MigrationTargetTests
{
    private const string LocalTarget =
        "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest_mig_test_0011223344556677";

    // ---------------------------------------------------------------- P2-04
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Port=5433;Username=postgres;Database=appdb")]                     // 空 host
    [InlineData("Host=localhost;Port=5433;Username=postgres")]                     // 空 database
    [InlineData("Host=localhost;Port=5433;Database=appdb")]                        // 空 user
    public void P2_04_EmptyOrIncompleteTargetIsRefused(string? connectionString)
    {
        var ex = Assert.Throws<DbMigrationException>(() => MigrationTarget.Parse(connectionString));
        Assert.Equal("invalid_migration_target", ex.Code);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("template0")]
    [InlineData("template1")]
    [InlineData("POSTGRES")]
    public void P2_04_ProtectedSystemDatabaseIsRefused(string database)
    {
        var ex = Assert.Throws<DbMigrationException>(() =>
            MigrationTarget.Parse($"Host=localhost;Port=5433;Username=postgres;Database={database}"));
        Assert.Equal("protected_database", ex.Code);
    }

    /// <summary>P2-04 的另一半:真正的一次性應用資料庫仍然是合法目標(P2-01 才跑得起來)。</summary>
    [Fact]
    public void P2_04_DisposableApplicationDatabaseRemainsAValidTarget()
    {
        var target = MigrationTarget.Parse(LocalTarget);

        Assert.Equal("localhost", target.Host);
        Assert.Equal(5433, target.Port);
        Assert.Equal("springaitest_mig_test_0011223344556677", target.Database);
        Assert.True(target.IsLocal);
        Assert.Contains("database=springaitest_mig_test_0011223344556677", target.Describe(null));
        Assert.Contains("composeProject=springaitest", target.Describe("springaitest"));
    }

    [Fact]
    public void ExactConfirmationTokenContainingTheDatabaseNameIsRequired()
    {
        var target = MigrationTarget.Parse(LocalTarget);

        var ex = Assert.Throws<DbMigrationException>(() =>
            target.RequireDestructiveConfirmation("RESET some-other-db", allowRemote: false, remoteConfirmation: null));
        Assert.Equal("confirmation_required", ex.Code);

        target.RequireDestructiveConfirmation(target.ConfirmationToken, allowRemote: false, remoteConfirmation: null);
    }

    // ---------------------------------------------------------------- P2-05
    [Fact]
    public void P2_05_RemoteTargetWithoutAllowRemoteFlagIsRefused()
    {
        var target = MigrationTarget.Parse(
            "Host=db.internal.example;Port=5432;Username=postgres;Database=springaitest_dev");

        var ex = Assert.Throws<DbMigrationException>(() =>
            target.RequireDestructiveConfirmation(target.ConfirmationToken, allowRemote: false, remoteConfirmation: null));
        Assert.Equal("remote_target_requires_flag", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("db.internal.example")]
    [InlineData("db.internal.example/springaitest")]
    public void P2_05_RemoteTargetWithoutExactSecondConfirmationIsRefused(string? remoteConfirmation)
    {
        var target = MigrationTarget.Parse(
            "Host=db.internal.example;Port=5432;Username=postgres;Database=springaitest_dev");

        var ex = Assert.Throws<DbMigrationException>(() =>
            target.RequireDestructiveConfirmation(target.ConfirmationToken, allowRemote: true, remoteConfirmation));
        Assert.Equal("remote_confirmation_required", ex.Code);
    }

    [Fact]
    public void P2_05_RemoteTargetPassesOnlyWithFlagAndExactHostDatabaseConfirmation()
    {
        var target = MigrationTarget.Parse(
            "Host=db.internal.example;Port=5432;Username=postgres;Database=springaitest_dev");

        Assert.Equal("db.internal.example/springaitest_dev", target.RemoteConfirmationToken);
        target.RequireDestructiveConfirmation(
            target.ConfirmationToken, allowRemote: true, target.RemoteConfirmationToken);
    }
}
