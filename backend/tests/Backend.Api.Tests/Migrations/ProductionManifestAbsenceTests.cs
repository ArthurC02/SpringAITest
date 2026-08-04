using Backend.Api.Data.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// P2-17:正常啟動的 binary 不含任何生產 hard-reset SQL,舊 schema 仍是權威。
/// 這是 P2 最重要的一條 —— 只要 <see cref="MigrationManifest.Production"/> 是空的,
/// 舊 binary 就不可能提早建出目標 schema,也不可能在啟動時走破壞性分支(01-plan §5)。
/// </summary>
public sealed class ProductionManifestAbsenceTests
{
    [Fact]
    public void P2_17_RegisteredProductionManifestContainsNoMigrationSql()
    {
        Assert.Empty(MigrationManifest.Production.Scripts);
        Assert.Empty(MigrationManifest.Production.Postconditions);
        Assert.Equal(0, MigrationManifest.Production.MaxVersion);
    }

    [Fact]
    public void P2_17_ShippedAssemblyEmbedsNoHardResetOrProductionSqlResource()
    {
        var resources = typeof(MigrationManifest).Assembly.GetManifestResourceNames();

        Assert.DoesNotContain(resources, r => r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resources, r => r.Contains("architecture_hard_reset", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resources, r => r.Contains("target_schema", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resources, r => r.Contains("constraints_and_indexes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// runner 不在正常啟動流程上:它沒有被註冊到 DI,唯一的呼叫點是 Program.cs 裡
    /// 建 host 之前就結束的 migrate-db 專用行程分支。
    /// </summary>
    [Fact]
    public void P2_17_RunnerIsNotRegisteredInTheApplicationServiceProvider()
    {
        using var factory = new TestWebAppFactory();

        Assert.Null(factory.Services.GetService<DbMigrationRunner>());
        Assert.Null(factory.Services.GetService<MigrationManifest>());
    }
}
