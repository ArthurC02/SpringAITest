using System.Diagnostics;
using Backend.Api.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests.Migrations;

/// <summary>
/// P2-13 / P2-14:fingerprint 是「fresh 與 reset 收斂到同一個 schema」的唯一驗收方式,
/// 並且同一份 fingerprint 也用來背書 appdb 的 dump/restore 復原機制。
/// 比較永遠不依賴 pg_dump 的文字輸出 —— 那是 catalog projection 的工作。
/// </summary>
[Collection("Postgres")]
public sealed class SchemaFingerprintTests(PostgresFixture fixture)
{
    /// <summary>appdb 容器名(pg_dump/pg_restore 只存在於容器內)。</summary>
    private static readonly string AppdbContainer =
        Environment.GetEnvironmentVariable("APPDB_CONTAINER") ?? "springaitest-appdb-1";

    private static DbMigrationRunner Runner() => new(MigrationFixtures.Bundle, NullLogger.Instance);

    // ---------------------------------------------------------------- P2-13
    [SkippableFact]
    public async Task P2_13_FreshAndResetDatabasesProduceIdenticalFingerprints()
    {
        fixture.SkipIfUnavailable();

        await using var fresh = await DisposableDatabase.CreateAsync(fixture.ConnectionString);
        await Runner().RunAsync(fresh.ConnectionString, MigrationMode.Startup);

        await using var reset = await DisposableDatabase.CreateAsync(fixture.ConnectionString);
        await reset.ExecuteAsync(MigrationFixtures.SyntheticLegacySchema);
        await Runner().RunAsync(reset.ConnectionString, MigrationMode.Destructive);

        var freshFingerprint = await SchemaFingerprint.ComputeAsync(fresh.ConnectionString);
        var resetFingerprint = await SchemaFingerprint.ComputeAsync(reset.ConnectionString);

        // 先確認 projection 真的有內容,免得「兩邊都空」變成假綠燈。
        Assert.Contains("public|table|fx_widget|", freshFingerprint);
        Assert.Contains("public|constraint|fx_widget_item.fk_fx_widget_item_widget|", freshFingerprint);
        Assert.Contains("public|sequence|", freshFingerprint);
        Assert.Equal(freshFingerprint, resetFingerprint);
    }

    // ---------------------------------------------------------------- P2-14
    [SkippableFact]
    public async Task P2_14_CustomFormatDumpRestoredIntoASecondDatabaseKeepsTheSameFingerprint()
    {
        fixture.SkipIfUnavailable();
        Skip.IfNot(DockerAvailable(), $"docker/appdb 容器 {AppdbContainer} 不可用,略過 dump/restore 演練");

        await using var source = await DisposableDatabase.CreateAsync(fixture.ConnectionString);
        await source.ExecuteAsync(MigrationFixtures.SyntheticLegacySchema);
        await Runner().RunAsync(source.ConnectionString, MigrationMode.Destructive);

        await using var restored = await DisposableDatabase.CreateAsync(fixture.ConnectionString);

        var dumpPath = $"/tmp/{source.Name}.dump";
        try
        {
            RunDocker("exec", AppdbContainer, "pg_dump", "-U", "postgres", "--format=custom",
                "--no-owner", "--no-acl", "-d", source.Name, "-f", dumpPath);
            RunDocker("exec", AppdbContainer, "pg_restore", "-U", "postgres",
                "--no-owner", "--no-acl", "-d", restored.Name, dumpPath);

            Assert.Equal(
                await SchemaFingerprint.ComputeAsync(source.ConnectionString),
                await SchemaFingerprint.ComputeAsync(restored.ConnectionString));
        }
        finally
        {
            // 一次性資料庫會被 Dispose 掉,容器內的 dump 檔要自己收拾。
            Run("docker", ["exec", AppdbContainer, "rm", "-f", dumpPath]);
        }
    }

    private static bool DockerAvailable()
    {
        try
        {
            return Run("docker", ["exec", AppdbContainer, "true"]).ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RunDocker(params string[] args)
    {
        var (exitCode, output) = Run("docker", args);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', args)} 失敗({exitCode}):{output}");
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, string[] args)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, output);
    }
}
