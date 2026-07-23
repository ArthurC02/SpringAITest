using System.Text;
using System.IO.Compression;
using Backend.Api.Data;
using Backend.Api.Skills;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

/// <summary>
/// SkillRepository 的**真 PostgreSQL**驗收(Agent Skill P0 儲存不變量,AST-P0-001/013 的 DB 級部分)。
/// 手寫 fake 背書不了的:import 於單一 CTE/交易同時寫 definition + package + 兩個 hash、
/// flow update 不得清除既有 package、軟刪復活、跨租戶 package 不可見、flow 匯入 package 欄為 NULL。
/// 共用 PostgresFixture(同 "Postgres" collection 序列化執行);appdb 不可達則 SkipIfUnavailable(不假綠)。
/// 每測用自己的 "skillrepo-&lt;case&gt;-" 租戶,類內循序、前後自清(skill_revision 有 FK → 先刪 revision)。
/// </summary>
[Collection("Postgres")]
public sealed class SkillRepositoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;

    public SkillRepositoryTests(PostgresFixture fx) => _fx = fx;

    private SkillRepository Repo => new(_fx.DataSource!);

    public async Task InitializeAsync() => await CleanupAsync();

    public async Task DisposeAsync() => await CleanupAsync();

    private async Task CleanupAsync()
    {
        if (!_fx.Available)
        {
            return;
        }

        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM skill_revision WHERE skill_id IN (SELECT id FROM skill WHERE tenant_id LIKE 'skillrepo-%');"
            + " DELETE FROM skill WHERE tenant_id LIKE 'skillrepo-%';");
    }

    private static Skill Meta(
        string name, string definition, string desc = "銷售小幫手",
        string role = "USER", string kind = "flow")
        => new(
            name, desc, definition, role, Enabled: true, CurrentRevision: 0,
            CreatedAt: default, UpdatedAt: default, Kind: kind);

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, byte[]> ReadZip(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var stream = e.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            });
    }

    private async Task<byte[]?> DbPackageAsync(string tenant, string name)
    {
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<byte[]?>(
            "SELECT package FROM skill WHERE tenant_id = @tenant AND name = @name", new { tenant, name });
    }

    private async Task<(string DefSha, string? PkgSha)> DbRevisionShaAsync(string tenant, string name, int revision)
    {
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        var row = await conn.QuerySingleAsync<(string, string?)>(
            "SELECT r.definition_sha256, r.package_sha256 FROM skill_revision r"
            + " JOIN skill s ON s.id = r.skill_id"
            + " WHERE s.tenant_id = @tenant AND s.name = @name AND r.revision = @revision",
            new { tenant, name, revision });
        return row;
    }

    // ---- AST-P0-001:import 於同一 revision 寫入 metadata + canonical definition + package + 兩個 hash ----

    [SkippableFact]
    public async Task Import_Agentic_WritesDefinitionPackageAndBothHashes_InOneRevision()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-001";
        const string name = "sales_helper";
        var canonical = "kind: agentic\nname: sales_helper\ndescription: 銷售小幫手\n";
        var package = Encoding.UTF8.GetBytes("PK-agentic-zip-bytes-001");
        var pkgSha = SkillHash.Sha256(package);

        var stored = await Repo.ImportAsync(
            t, Meta(name, canonical, kind: "agentic"), package, pkgSha, "admin-a", default);

        Assert.NotNull(stored);
        Assert.Equal(canonical, stored!.Definition);
        Assert.Equal("銷售小幫手", stored.Description);
        Assert.Equal(1, stored.CurrentRevision);
        Assert.Equal(package, stored.Package);

        // DB 級:package bytea 逐 byte 落地。
        Assert.Equal(package, await DbPackageAsync(t, name));

        // 單一 revision 同時記兩個 hash。
        var revisions = await Repo.ListRevisionsAsync(t, name, default);
        Assert.Single(revisions);
        var (defSha, revPkgSha) = await DbRevisionShaAsync(t, name, 1);
        Assert.Equal(SkillHash.Sha256(canonical), defSha);
        Assert.Equal(pkgSha, revPkgSha);
    }

    // ---- import 是 upsert:對既有(啟用中)skill 也直接更新、bump revision、替換 package ----

    [SkippableFact]
    public async Task Import_OverExisting_BumpsRevision_AndReplacesPackage()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-upsert";
        const string name = "upsert_skill";
        var pkg1 = Encoding.UTF8.GetBytes("zip-v1");
        var pkg2 = Encoding.UTF8.GetBytes("zip-v2-longer");

        await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\nv: 1\n", kind: "agentic"),
            pkg1, SkillHash.Sha256(pkg1), "admin-a", default);
        var v2 = await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\nv: 2\n", kind: "agentic"),
            pkg2, SkillHash.Sha256(pkg2), "admin-a", default);

        Assert.Equal(2, v2!.CurrentRevision);
        Assert.Equal(pkg2, await DbPackageAsync(t, name));
        var (_, revPkgSha) = await DbRevisionShaAsync(t, name, 2);
        Assert.Equal(SkillHash.Sha256(pkg2), revPkgSha);
        Assert.Equal(2, (await Repo.ListRevisionsAsync(t, name, default)).Count);
    }

    // ---- 軟刪的名字可經 import 復活,revision 接續(稽核鏈不斷號)----

    [SkippableFact]
    public async Task Import_AfterSoftDelete_Revives_AndContinuesRevisionChain()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-revive";
        const string name = "revive_skill";
        var pkg = Encoding.UTF8.GetBytes("zip-bytes");

        await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\n", kind: "agentic"),
            pkg, SkillHash.Sha256(pkg), "admin-a", default); // r1
        Assert.True(await Repo.DeleteAsync(t, name, default));

        var revived = await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\nv: 2\n", kind: "agentic"),
            pkg, SkillHash.Sha256(pkg), "admin-a", default);

        Assert.NotNull(revived);
        Assert.Equal(2, revived!.CurrentRevision); // 接續,不是重頭
        Assert.NotNull(await Repo.GetAsync(t, name, default)); // 可見性回來
        Assert.Equal(new[] { 2, 1 }, (await Repo.ListRevisionsAsync(t, name, default)).Select(r => r.Revision).ToArray());
    }

    // ---- definition-only flow update 必須清除舊 package，避免 export 舊 zip 與新 definition 漂移 ----

    [SkippableFact]
    public async Task FlowUpdate_ClearsImportedPackage_AndSnapshotsDefinitionOnlyRevision()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-keeppkg";
        const string name = "keeppkg_skill";
        var pkg = Encoding.UTF8.GetBytes("agentic-zip");
        await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\n", kind: "agentic"),
            pkg, SkillHash.Sha256(pkg), "admin-a", default);

        // 直接對 repo 呼叫 flow UpdateAsync（controller 對 agentic 會擋；repo 仍須維持資料不變式）。
        var updated = await Repo.UpdateAsync(t, name, Meta(name, "name: keeppkg_skill\nflow: []\n"), "u", default);

        Assert.NotNull(updated);
        Assert.Null(updated!.Package);
        Assert.Null(await DbPackageAsync(t, name));
        var currentRevision = await Repo.GetRevisionAsync(t, name, 2, default);
        Assert.NotNull(currentRevision);
        Assert.Null(currentRevision!.Package);
        Assert.Equal("flow", currentRevision.Kind);
    }

    // ---- 跨租戶:A 的 package 對 B 不可見(GetAsync tenant 過濾)----

    [SkippableFact]
    public async Task GetAsync_Package_IsTenantScoped()
    {
        _fx.SkipIfUnavailable();
        const string name = "shared_name";
        var pkg = Encoding.UTF8.GetBytes("tenant-a-secret-zip");
        await Repo.ImportAsync(
            "skillrepo-iso-a", Meta(name, "kind: agentic\n", kind: "agentic"),
            pkg, SkillHash.Sha256(pkg), "admin-a", default);

        Assert.Equal(pkg, (await Repo.GetAsync("skillrepo-iso-a", name, default))!.Package);
        Assert.Null(await Repo.GetAsync("skillrepo-iso-b", name, default)); // B 看不到
    }

    // ---- flow 匯入:package 欄與 revision.package_sha256 皆為 NULL(null-bytea 寫入路徑)----

    [SkippableFact]
    public async Task FlowImport_StoresNullPackage_AndNullRevisionSha()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-flowimp";
        const string name = "flow_import";
        var canonical = "name: flow_import\nflow:\n  - node: query_intake\n";

        var stored = await Repo.ImportAsync(t, Meta(name, canonical), package: null, packageSha256: null, "admin-a", default);

        Assert.Null(stored!.Package);
        Assert.Null(await DbPackageAsync(t, name));
        var (defSha, revPkgSha) = await DbRevisionShaAsync(t, name, 1);
        Assert.Equal(SkillHash.Sha256(canonical), defSha);
        Assert.Null(revPkgSha);
    }

    // ---- 05 §5:DbBootstrap 遷移把自訂底線名就地改連字號,skill_revision 歷史零遺失 + 冪等 ----

    [SkippableFact]
    public async Task Migration_RenamesUnderscoreCustomName_PreservesRevisionHistory_AndIsIdempotent()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-migrate";

        // 前置:模擬遷移前資料(底線名,兩筆 revision)。backend 不驗 name → 可直接寫入。
        await Repo.CreateAsync(
            t, Meta("year_compare", "name: year_compare\nflow: v1\n"), "admin-a", default); // rev 1
        await Repo.UpdateAsync(
            t, "year_compare", Meta("year_compare", "name: year_compare\nflow: v2\n"),
            "admin-a", default); // rev 2

        // 遷移(= 再跑一次 DbBootstrap;含 rename UPDATE)。
        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        // 舊名消失、新名可見、revision 接續不變、稽核鏈完整保留。
        Assert.Null(await Repo.GetAsync(t, "year_compare", default));
        var renamed = await Repo.GetAsync(t, "year-compare", default);
        Assert.NotNull(renamed);
        Assert.Equal(2, renamed!.CurrentRevision);
        var revs = await Repo.ListRevisionsAsync(t, "year-compare", default);
        Assert.Equal(new[] { 2, 1 }, revs.Select(r => r.Revision).ToArray());
        Assert.Equal(
            "name: year-compare\nflow: v1\n",
            revs.Single(r => r.Revision == 1).Definition);

        // 冪等:再跑一次不再變動(改後不含底線 → 匹配 0 列)。
        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);
        Assert.NotNull(await Repo.GetAsync(t, "year-compare", default));
        Assert.Equal(2, (await Repo.ListRevisionsAsync(t, "year-compare", default)).Count);
    }

    // ---- 05 §5:遷移一併改寫定義的 ^name: 標量,守住「definition name == 身分」不變式 ----
    // (a) 全新底線(column+def 皆底線)完整遷移;(b) 半遷移列(column 已連字號、def 仍底線)修定義;
    // (c) 已一致的連字號 skill 完全不動;(d) 二次啟動冪等。

    [SkippableFact]
    public async Task Migration_RewritesDefinitionNameLine_ForFreshAndHalfMigrated_AndNoOpsConsistent()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-defname";

        // (a) column 與 def 皆底線。
        await Repo.CreateAsync(t, Meta("year_compare", "name: \"year_compare\"\nflow: v1\n"), "admin-a", default);
        // (b) 半遷移:column 已是連字號,但 def 內嵌 name 仍是舊底線值(= live appdb 現況)。
        await Repo.CreateAsync(t, Meta("half_done", "name: \"half_done\"\nflow: v1\n"), "admin-a", default);
        await Repo.UpdateAsync(t, "half_done", Meta("half_done", "name: half-done\nflow: v2\n"), "admin-a", default);
        // ↑ 現在 column='half-done'、def='name: half-done...';但要模擬 def 仍底線,直接改回底線 def:
        await ForceDefinitionAsync(t, "half-done", "name: \"half_done\"\nflow: v2\n");
        // (c) 已一致的連字號 skill。
        await Repo.CreateAsync(t, Meta("stats-daily", "name: stats-daily\nflow: x\n"), "admin-a", default);

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        // (a) column + def 都改成連字號,舊名 404。
        Assert.Null(await Repo.GetAsync(t, "year_compare", default));
        Assert.Equal("name: year-compare\nflow: v1\n", (await Repo.GetAsync(t, "year-compare", default))!.Definition);
        // (b) column 不變,def name 行修成與 column 一致。
        Assert.Equal("name: half-done\nflow: v2\n", (await Repo.GetAsync(t, "half-done", default))!.Definition);
        // (c) 已一致者原封不動(未丟引號/未動任何 byte)。
        Assert.Equal("name: stats-daily\nflow: x\n", (await Repo.GetAsync(t, "stats-daily", default))!.Definition);

        // (d) 冪等:二次啟動不再更動(底線已無、def name 行皆等於 column)。
        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);
        Assert.Equal("name: year-compare\nflow: v1\n", (await Repo.GetAsync(t, "year-compare", default))!.Definition);
        Assert.Equal("name: half-done\nflow: v2\n", (await Repo.GetAsync(t, "half-done", default))!.Definition);
        Assert.Equal("name: stats-daily\nflow: x\n", (await Repo.GetAsync(t, "stats-daily", default))!.Definition);
    }

    [SkippableFact]
    public async Task Migration_RewritesOnlyRootNameScalar_ForSpacingQuotedKeyAndInlineMapping()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-yamlspan";
        var cases = new[]
        {
            (
                Old: "spacing_name",
                New: "spacing-name",
                Definition: "name : spacing_name # keep\r\ndescription: d\r\nflow: []\r\n",
                Expected: "name : spacing-name # keep\r\ndescription: d\r\nflow: []\r\n"),
            (
                Old: "quoted_name",
                New: "quoted-name",
                Definition: "'name': \"quoted_name\" # keep\nother_name: quoted_name\nflow: []\n",
                Expected: "'name': quoted-name # keep\nother_name: quoted_name\nflow: []\n"),
            (
                Old: "inline_name",
                New: "inline-name",
                Definition: "{name: inline_name, description: d, flow: []} # keep\r\n",
                Expected: "{name: inline-name, description: d, flow: []} # keep\r\n"),
        };
        foreach (var item in cases)
        {
            await Repo.CreateAsync(
                tenant, Meta(item.Old, item.Definition), "admin-a", default);
        }

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        foreach (var item in cases)
        {
            var migrated = await Repo.GetAsync(tenant, item.New, default);
            Assert.NotNull(migrated);
            Assert.Equal(item.Expected, migrated!.Definition);
            var revision = Assert.Single(await Repo.ListRevisionsAsync(
                tenant, item.New, default));
            Assert.Equal(item.Expected, revision.Definition);
            Assert.Equal(SkillHash.Sha256(item.Expected), revision.DefinitionSha256);
        }
    }

    [SkippableFact]
    public async Task Migration_MissingRootName_FailsFastAndRollsBackTransaction()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-yamlbad";
        await Repo.CreateAsync(
            tenant, Meta("good_name", "name: good_name\nflow: []\n"), "admin-a", default);
        await Repo.CreateAsync(
            tenant, Meta("missing_name", "description: no name\nflow: []\n"), "admin-a", default);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance));

        Assert.Contains("恰有一個 name scalar", error.Message);
        // good_name 即使排序較前並先被處理，也必須隨同一 transaction rollback。
        Assert.NotNull(await Repo.GetAsync(tenant, "good_name", default));
        Assert.Null(await Repo.GetAsync(tenant, "good-name", default));
    }

    [SkippableFact]
    public async Task Migration_DuplicateRootName_FailsFastWithoutChoosingOne()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-yamldup";
        await Repo.CreateAsync(
            tenant,
            Meta("duplicate_name", "name: duplicate_name\n'name': duplicate_name\nflow: []\n"),
            "admin-a",
            default);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance));

        Assert.Contains("實際 2 個", error.Message);
        Assert.NotNull(await Repo.GetAsync(tenant, "duplicate_name", default));
        Assert.Null(await Repo.GetAsync(tenant, "duplicate-name", default));
    }

    [SkippableFact]
    public async Task Migration_LegacyAgenticWithoutDerivableDescription_FailsAndKeepsOriginal()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-agentbad";
        const string oldName = "missing_description";
        var canonical = "name: missing_description\nkind: agentic\n";
        var package = Zip((
            "SKILL.md",
            Encoding.UTF8.GetBytes(
                "---\nname: missing_description\nkind: agentic\n---\nbody\n")));
        await Repo.ImportAsync(
            tenant, Meta(oldName, canonical, kind: "agentic"),
            package, SkillHash.Sha256(package), "admin-a", default);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance));

        Assert.Contains("description", error.Message);
        Assert.NotNull(await Repo.GetAsync(tenant, oldName, default));
        Assert.Null(await Repo.GetAsync(tenant, "missing-description", default));
    }

    [SkippableFact]
    public async Task Migration_UpgradesLegacyAgenticPackage_AndCurrentRevisionAtomically()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-agentmig";
        const string oldName = "sales_helper";
        const string newName = "sales-helper";
        var legacyCanonical = """
            name: sales_helper
            description: 銷售小幫手
            kind: agentic
            required_role: ADMIN
            input_schema:
              question:
                type: str
                required: true
            uses_tools:
              - retrieve
            """;
        var legacySkillMd = """
            ---
            name: sales_helper
            description: 銷售小幫手
            kind: agentic
            uses_tools:
              - retrieve
            ---
            請依照參考資料回答。
            """;
        var guide = Encoding.UTF8.GetBytes("legacy resource\0中文");
        var package = Zip(
            ("SKILL.md", Encoding.UTF8.GetBytes(legacySkillMd)),
            ("docs/guide.bin", guide));
        await Repo.ImportAsync(
            tenant, Meta(oldName, legacyCanonical, role: "ADMIN", kind: "agentic"),
            package, SkillHash.Sha256(package), "admin-a", default);

        // 模擬舊 schema：package 只存在 current skill，不存在 revision snapshot。
        await using (var conn = await _fx.DataSource!.OpenConnectionAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE skill_revision SET package = NULL"
                + " WHERE skill_id = (SELECT id FROM skill WHERE tenant_id = @tenant AND name = @oldName)",
                new { tenant, oldName });
        }

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        Assert.Null(await Repo.GetAsync(tenant, oldName, default));
        var migrated = await Repo.GetAsync(tenant, newName, default);
        Assert.NotNull(migrated);
        Assert.Equal("agentic", migrated!.Kind);
        var entries = ReadZip(migrated.Package!);
        Assert.Equal(guide, entries["docs/guide.bin"]);
        var skillMd = Encoding.UTF8.GetString(entries["SKILL.md"]);
        Assert.Contains("name: sales-helper", skillMd);
        Assert.Contains("allowed-tools: retrieve", skillMd);
        Assert.Contains("metadata:", skillMd);
        Assert.Contains("kind: agentic", skillMd);
        Assert.Contains("required_role: ADMIN", skillMd);
        Assert.Matches(@"timeout_seconds:\s+['""]?60['""]?", skillMd);
        Assert.Contains("\"question\"", skillMd);
        Assert.DoesNotContain("\nuses_tools:", skillMd);
        Assert.DoesNotContain("\nrequired_role:", skillMd);
        Assert.Contains("請依照參考資料回答。", skillMd);

        var current = await Repo.GetRevisionAsync(tenant, newName, 1, default);
        Assert.NotNull(current);
        Assert.Equal(migrated.Definition, current!.Definition);
        Assert.Equal(SkillHash.Sha256(migrated.Definition), current.DefinitionSha256);
        Assert.Equal(migrated.Package, current.Package);
        Assert.Equal(SkillHash.Sha256(migrated.Package!), current.PackageSha256);

        // 二次啟動完全 no-op：已標準 package 不應重新壓縮或改 timestamp。
        var once = migrated.Package!.ToArray();
        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);
        Assert.Equal(once, (await Repo.GetAsync(tenant, newName, default))!.Package);
    }

    /// <summary>繞過 repo 語意,直接把某 skill 的 definition 覆寫成指定字串(模擬遷移前殘狀態)。</summary>
    private async Task ForceDefinitionAsync(string tenant, string name, string definition)
    {
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        await conn.ExecuteAsync(
            "UPDATE skill SET definition = @definition WHERE tenant_id = @tenant AND name = @name",
            new { tenant, name, definition });
    }

    // ---- 05 §5:目標連字號名已存在 → fail fast，不靜默留下標準不可讀的底線列 ----

    [SkippableFact]
    public async Task Migration_TargetNameCollision_FailsFastWithoutOverwritingEitherRow()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-migclash";

        // 同租戶同時存在 dup_name 與 dup-name（只可能來自半部署/人工資料）。
        await Repo.CreateAsync(
            t, Meta("dup-name", "name: dup-name\nflow: existing\n"), "admin-a", default);
        await Repo.CreateAsync(
            t, Meta("dup_name", "name: dup_name\nflow: underscore\n"), "admin-a", default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance));

        Assert.Contains("名稱遷移發生衝突", error.Message);
        Assert.NotNull(await Repo.GetAsync(t, "dup-name", default));
        Assert.NotNull(await Repo.GetAsync(t, "dup_name", default));
        Assert.Equal(
            "name: dup-name\nflow: existing\n",
            (await Repo.GetAsync(t, "dup-name", default))!.Definition);
    }
}
