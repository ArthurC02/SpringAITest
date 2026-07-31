using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Backend.Api.Data;
using Backend.Api.Skills;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

/// <summary>
/// SkillRepository 的**真 PostgreSQL**驗收(Agent Skill P0 儲存不變量,AST-P0-001/013 的 DB 級部分)。
/// 手寫 fake 背書不了的:import 於單一 CTE/交易同時寫 definition + package + 兩個 hash、
/// definition-only flow update **必須清除**既有 package(SkillRepository.cs 的 `package = NULL`,
/// 否則匯出的舊 zip 會與新 definition 漂移)、軟刪復活、跨租戶 package 不可見、flow 匯入 package 欄為 NULL。
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
        Assert.Equal(SkillHash.Sha256(canonical), stored.DefinitionSha256);

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
        Assert.Equal(SkillHash.Sha256("kind: agentic\nv: 2\n"), v2.DefinitionSha256);
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

    [SkippableFact]
    public async Task FlowOnlyCreate_DoesNotReviveDisabledAgenticRow()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-kind-fenced-revive";
        const string name = "kind-fenced-revive";
        var package = Encoding.UTF8.GetBytes("agent-package");
        await Repo.ImportAsync(
            tenant, Meta(name, "kind: agentic", kind: "agentic"), package,
            SkillHash.Sha256(package), "admin-a", default);
        Assert.True(await Repo.DeleteAsync(tenant, name, default));

        var revived = await Repo.CreateAsync(
            tenant, "flow", Meta(name, "name: forbidden-flow\nflow: []"), "admin-a", default);

        Assert.Null(revived);
        var revisions = await Repo.ListRevisionsAsync(tenant, name, default);
        Assert.Single(revisions);
        var snapshot = await Repo.GetRevisionAsync(tenant, name, 1, default);
        Assert.Equal("agentic", snapshot!.Kind);
        Assert.Equal(package, snapshot.Package);
        Assert.Equal(package, await DbPackageAsync(tenant, name));
    }

    // ---- Dapper kind 過濾直測:ListAsync/GetAsync/DeleteAsync/UpdateAsync 帶 kind 參數的 WHERE fence,
    // 直接對 SkillRepository(真 Postgres)測,不走 API 層(controller 層已有等價 HTTP 案例,這裡驗 SQL 本身)。----

    [SkippableFact]
    public async Task ListAsync_WithKind_ExcludesOtherKindRows()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-kindlist";
        var package = Encoding.UTF8.GetBytes("agentic-zip");
        await Repo.CreateAsync(t, Meta("flow-item", "name: flow-item\nflow: []\n"), "admin-a", default);
        await Repo.ImportAsync(
            t, Meta("agentic-item", "kind: agentic\nname: agentic-item\n", kind: "agentic"),
            package, SkillHash.Sha256(package), "admin-a", default);

        var flows = await Repo.ListAsync(t, "flow", default);
        var agentics = await Repo.ListAsync(t, "agentic", default);

        Assert.Contains(flows, s => s.Name == "flow-item");
        Assert.DoesNotContain(flows, s => s.Name == "agentic-item");
        Assert.Contains(agentics, s => s.Name == "agentic-item");
        Assert.DoesNotContain(agentics, s => s.Name == "flow-item");
    }

    [SkippableFact]
    public async Task GetAsync_WithWrongKind_ReturnsNull()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-kindget";
        const string name = "kindget-item";
        var package = Encoding.UTF8.GetBytes("agentic-zip");
        await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\nname: kindget-item\n", kind: "agentic"),
            package, SkillHash.Sha256(package), "admin-a", default);

        Assert.Null(await Repo.GetAsync(t, name, "flow", default));
        var current = await Repo.GetAsync(t, name, "agentic", default);
        Assert.NotNull(current);
        Assert.Equal(SkillHash.Sha256(current.Definition), current.DefinitionSha256);
    }

    [SkippableFact]
    public async Task DeleteAsync_WithWrongKind_DoesNotDelete()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-kinddelete";
        const string name = "kinddelete-item";
        var package = Encoding.UTF8.GetBytes("agentic-zip");
        await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\nname: kinddelete-item\n", kind: "agentic"),
            package, SkillHash.Sha256(package), "admin-a", default);

        Assert.False(await Repo.DeleteAsync(t, name, "flow", default));
        Assert.NotNull(await Repo.GetAsync(t, name, default)); // 仍啟用,未被誤刪

        Assert.True(await Repo.DeleteAsync(t, name, "agentic", default));
        Assert.Null(await Repo.GetAsync(t, name, default));
    }

    [SkippableFact]
    public async Task UpdateAsync_WithWrongExpectedKind_ReturnsNull_AndLeavesRowUnchanged()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-kindupdate";
        const string name = "kindupdate-item";
        var package = Encoding.UTF8.GetBytes("agentic-zip");
        await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\nname: kindupdate-item\n", kind: "agentic"),
            package, SkillHash.Sha256(package), "admin-a", default);

        var updated = await Repo.UpdateAsync(
            t, name, "flow", Meta(name, "name: kindupdate-item\nflow: []\n"), "admin-a", default);

        Assert.Null(updated);
        var current = await Repo.GetAsync(t, name, default);
        Assert.Equal("agentic", current!.Kind);
        Assert.Equal(1, current.CurrentRevision);
        Assert.Equal(package, current.Package);
    }

    // ---- A1-4:遷移前 kind 為 NULL 的舊資料列(尚未跑過分類 UPDATE),DbBootstrap 一次性歸類為
    // flow(definition 未宣告 `kind: agentic`)。實際遷移只發生在 kind 欄仍可為 NULL 的舊 schema,
    // 這裡暫時放寬 NOT NULL 約束來重現那個窗口,而非另建一個從未跑過 bootstrap 的資料庫。----

    [SkippableFact]
    public async Task Migration_NullKindLegacyRow_ClassifiesAsFlow()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-nullkind";
        const string name = "nullkind-item";
        await Repo.CreateAsync(t, Meta(name, "name: nullkind-item\nflow: []\n"), "admin-a", default);
        await ForceKindNullAsync(t, name);

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        var list = await Repo.ListAsync(t, default);
        Assert.Equal("flow", Assert.Single(list, s => s.Name == name).Kind);
        Assert.Equal("flow", (await Repo.GetAsync(t, name, default))!.Kind);
    }

    /// <summary>暫時放寬 NOT NULL 約束並把 kind 欄直接改回 NULL,模擬遷移前(尚未跑過 kind 分類)的舊資料列。</summary>
    private async Task ForceKindNullAsync(string tenant, string name)
    {
        await using var conn = await _fx.DataSource!.OpenConnectionAsync();
        await conn.ExecuteAsync("ALTER TABLE skill ALTER COLUMN kind DROP NOT NULL;");
        await conn.ExecuteAsync(
            "UPDATE skill SET kind = NULL WHERE tenant_id = @tenant AND name = @name",
            new { tenant, name });
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

    // ---- Dapper 路徑的併發配號:UPDATE / INSERT ON CONFLICT 都是 read-modify-write
    // (current_revision = current_revision + 1),靠 row lock 序列化。重號會撞
    // uq_skill_revision(23505)而不是靜默損毀,但那是「寫入直接失敗」—— 這條守的是
    // 「正常併發下不該有人失敗,而且每個號碼的 snapshot 屬於自己那次寫入」。----

    [SkippableFact]
    public async Task Concurrent_UpdateAndImport_ProduceUniqueRevisions()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-concurrent";
        const string name = "concurrent_skill";
        await Repo.CreateAsync(t, Meta(name, "name: concurrent_skill\nflow: seed\n"), "admin-a", default);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var definition = $"name: {name}\nflow: v{index}\n";
            if (index % 2 == 0)
            {
                var updated = await Repo.UpdateAsync(t, name, Meta(name, definition), $"u-{index}", default);
                return (Definition: definition, Stored: updated!);
            }

            var package = Encoding.UTF8.GetBytes($"zip-{index}");
            var imported = await Repo.ImportAsync(
                t, Meta(name, definition, kind: "agentic"), package, SkillHash.Sha256(package),
                $"i-{index}", default);
            return (Definition: definition, Stored: imported!);
        })).ToArray();

        start.SetResult();
        var writes = await Task.WhenAll(tasks);

        // 每次寫入拿到唯一且連續的號碼(seed 是 1 → 2..17)。
        Assert.Equal(
            Enumerable.Range(2, 16),
            writes.Select(w => w.Stored.CurrentRevision).OrderBy(r => r));

        // 該號 revision 的 snapshot 屬於同一次寫入(交錯汙染 = 稽核鏈說謊)。
        foreach (var write in writes)
        {
            var snapshot = await Repo.GetRevisionAsync(t, name, write.Stored.CurrentRevision, default);
            Assert.NotNull(snapshot);
            Assert.Equal(write.Definition, snapshot!.Definition);
            Assert.Equal(SkillHash.Sha256(write.Definition), snapshot.DefinitionSha256);
        }

        Assert.Equal(17, (await Repo.GetAsync(t, name, default))!.CurrentRevision);
        Assert.Equal(17, (await Repo.ListRevisionsAsync(t, name, default)).Count);
    }

    // ---- B3:simple_form 的真 SQL 語意(手寫 fake 背書不了 jsonb::text/COALESCE/DO UPDATE 保留) ----

    private static Skill FormMeta(string name, string definition, string? simpleForm)
        => new(name, "銷售小幫手", definition, "USER", Enabled: true, CurrentRevision: 0,
            CreatedAt: default, UpdatedAt: default, Kind: "flow", Package: null, SimpleForm: simpleForm);

    [SkippableFact] // create 帶 simple_form → jsonb 落地,GetAsync 以 ::text 讀回原文;update 不帶 → COALESCE 保留。
    public async Task Create_StoresSimpleForm_AndUpdateWithoutIt_Preserves()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-sfcreate";
        const string name = "sf_skill";
        var form = "{\"templateId\": \"template-stats\", \"form\": {\"topK\": \"50\"}}";

        var created = await Repo.CreateAsync(t, FormMeta(name, "name: sf_skill\nflow: v1\n", form), "admin-a", default);
        Assert.NotNull(created!.SimpleForm);
        // jsonb round-trip:語意等值(以解析後比較,避免空白/鍵序差異)。
        Assert.Equal("template-stats", JsonDocument.Parse(created.SimpleForm!).RootElement
            .GetProperty("templateId").GetString());
        Assert.Equal("template-stats", JsonDocument.Parse(
            (await Repo.GetAsync(t, name, default))!.SimpleForm!).RootElement.GetProperty("templateId").GetString());

        // 進階編輯器 update(simpleForm = null)→ 保留既有(COALESCE 半邊)。
        var updated = await Repo.UpdateAsync(
            t, name, FormMeta(name, "name: sf_skill\nflow: v2\n", simpleForm: null), "admin-a", default);
        Assert.NotNull(updated!.SimpleForm);
        Assert.Equal("template-stats", JsonDocument.Parse(updated.SimpleForm!).RootElement
            .GetProperty("templateId").GetString());
    }

    [SkippableFact] // update 帶新 simple_form → 覆寫(COALESCE 的另半邊,決策表收尾)。
    public async Task Update_WithNewSimpleForm_Overwrites()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-sfupd";
        const string name = "sf_upd";
        await Repo.CreateAsync(
            t, FormMeta(name, "name: sf_upd\nflow: v1\n", "{\"templateId\": \"template-stats\"}"), "admin-a", default);

        var updated = await Repo.UpdateAsync(
            t, name, FormMeta(name, "name: sf_upd\nflow: v2\n", "{\"templateId\": \"template-compare\"}"),
            "admin-a", default);

        Assert.Equal("template-compare", JsonDocument.Parse(updated!.SimpleForm!).RootElement
            .GetProperty("templateId").GetString());
    }

    [SkippableFact] // import 建立品 simple_form 為 NULL;import 覆寫既有(帶 simple_form)不清空(不帶不清)。
    public async Task Import_LeavesSimpleFormNull_OnCreate_AndPreserves_OnOverwrite()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-sfimp";

        // (i) 全新 import → NULL。
        var pkg = Encoding.UTF8.GetBytes("zip");
        var imported = await Repo.ImportAsync(
            t, Meta("sf_import_new", "kind: agentic\n", kind: "agentic"),
            pkg, SkillHash.Sha256(pkg), "admin-a", default);
        Assert.Null(imported!.SimpleForm);

        // (ii) 既有 flow skill(帶 simple_form)被 import 覆寫 → 保留表單狀態。
        const string name = "sf_import_keep";
        await Repo.CreateAsync(
            t, FormMeta(name, $"name: {name}\nflow: v1\n", "{\"templateId\": \"template-stats\"}"), "admin-a", default);
        var overwritten = await Repo.ImportAsync(
            t, Meta(name, "kind: agentic\n", kind: "agentic"), pkg, SkillHash.Sha256(pkg), "admin-a", default);
        Assert.Equal("template-stats", JsonDocument.Parse(overwritten!.SimpleForm!).RootElement
            .GetProperty("templateId").GetString());
    }

    // ---- 05 §5:DbBootstrap 遷移把自訂底線名就地改連字號,skill_revision 歷史零遺失 ----
    // (冪等由 Migration_RewritesDefinitionNameLine_… 的 (d) 段負責,不在此重複斷言)

    [SkippableFact]
    public async Task Migration_RenamesUnderscoreCustomName_PreservesRevisionHistory()
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

    // ---- 遷移是一次性 legacy 升級,不是 request-time validator:單列資料形狀壞掉只跳過該列
    // (savepoint 回捲、記 warning),不得讓服務永久起不來,也不得連累其他可遷移的列。----

    [SkippableFact]
    public async Task Migration_MissingRootName_SkipsRowAndStillMigratesOthers()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-yamlbad";
        await Repo.CreateAsync(
            tenant, Meta("good_name", "name: good_name\nflow: []\n"), "admin-a", default);
        await Repo.CreateAsync(
            tenant, Meta("missing_name", "description: no name\nflow: []\n"), "admin-a", default);

        // 跳過的唯一安全網是 warning 紀錄 → 用會擷取的 logger 驗證它真的留下可查紀錄。
        var logger = new RecordingLogger<SkillRepositoryTests>();
        await DbBootstrap.RunAsync(_fx.DataSource!, logger);

        // 壞掉那列原封不動留著(名稱仍是底線)；健康的列照常升級。
        var skipped = await Repo.GetAsync(tenant, "missing_name", default);
        Assert.NotNull(skipped);
        Assert.Equal("description: no name\nflow: []\n", skipped!.Definition);
        Assert.NotNull(await Repo.GetAsync(tenant, "good-name", default));
        Assert.Null(await Repo.GetAsync(tenant, "good_name", default));
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains(tenant, StringComparison.Ordinal)
                && e.Message.Contains("missing_name", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Migration_DuplicateRootName_SkipsRowWithoutChoosingOne()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-yamldup";
        const string definition = "name: duplicate_name\n'name': duplicate_name\nflow: []\n";
        await Repo.CreateAsync(tenant, Meta("duplicate_name", definition), "admin-a", default);

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        var kept = await Repo.GetAsync(tenant, "duplicate_name", default);
        Assert.NotNull(kept);
        Assert.Equal(definition, kept!.Definition); // 兩個候選都不選:原封未動
        Assert.Null(await Repo.GetAsync(tenant, "duplicate-name", default));
    }

    [SkippableFact]
    public async Task Migration_LegacyAgenticWithoutDerivableDescription_SkipsRowAndKeepsOriginal()
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

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        var kept = await Repo.GetAsync(tenant, oldName, default);
        Assert.NotNull(kept);
        Assert.Equal(canonical, kept!.Definition);
        Assert.Equal(package, kept.Package);
        Assert.Null(await Repo.GetAsync(tenant, "missing-description", default));
    }

    // ---- 跳過必須是「整列」回捲:同一列較早的 revision 已寫入,後面的 revision 才爆掉時,
    // 前面那些 UPDATE 不得被 commit(否則 revision 1 被改名、skill 名稱卻仍是舊的 → 稽核鏈自相矛盾)。
    // 這條就是 savepoint 的承重測試:拿掉 SaveAsync/RollbackAsync 會紅。----

    [SkippableFact]
    public async Task Migration_BrokenLaterRevision_RollsBackEarlierRevisionWritesOfSameRow()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-revrollback";
        const string name = "two_rev";
        const string revision1Definition = "name: two_rev\nflow: v1\n";
        await Repo.CreateAsync(tenant, Meta(name, revision1Definition), "admin-a", default); // rev 1 可遷移
        await Repo.UpdateAsync(
            tenant, name, Meta(name, "description: no name\nflow: v2\n"), "admin-a", default); // rev 2 缺 name
        // skill.definition 修回可解析 → 該列會先成功改寫 revision 1,才在 revision 2 爆掉。
        await ForceDefinitionAsync(tenant, name, "name: two_rev\nflow: v2\n");

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        // revision 1 逐 byte 未變(沒有 savepoint 時會變成 name: two-rev + 新 hash)。
        var revision1 = await Repo.GetRevisionAsync(tenant, name, 1, default);
        Assert.NotNull(revision1);
        Assert.Equal(revision1Definition, revision1!.Definition);
        Assert.Equal(SkillHash.Sha256(revision1Definition), revision1.DefinitionSha256);
        Assert.Null(await Repo.GetAsync(tenant, "two-rev", default));
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

    // ---- 05 §0 標準佈局:SKILL.md 在單一頂層資料夾下(= 本服務匯出的形狀)也要認得,遷移不得誤判為缺檔 ----

    [SkippableFact]
    public async Task Migration_PackageWithTopLevelFolder_IsRecognizedAndLeftUnchanged()
    {
        _fx.SkipIfUnavailable();
        const string tenant = "skillrepo-folderpkg";
        const string name = "foldered-skill";
        var definition = $"name: {name}\ndescription: d\nflow:\n  - node: query_intake\n";
        var package = Zip(
            ($"{name}/SKILL.md",
             Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: d\n---\n\n```yaml\n{definition}\n```\n")),
            ($"{name}/docs/guide.bin", Encoding.UTF8.GetBytes("資源\0bytes")));
        await Repo.ImportAsync(
            tenant, Meta(name, definition), package, SkillHash.Sha256(package), "admin-a", default);

        await DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance);

        // 已符合標準 → 完全 no-op:bytes 逐位元不變(不是「找不到 SKILL.md 就重寫/爆掉」)。
        var migrated = await Repo.GetAsync(tenant, name, default);
        Assert.NotNull(migrated);
        Assert.Equal(package, migrated!.Package);
        Assert.Equal(
            new[] { $"{name}/SKILL.md", $"{name}/docs/guide.bin" },
            ReadZip(migrated.Package!).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
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

    // ---- fail-fast 的另半邊:名字換掉底線後**仍不合標準**(此處為大寫)→ 同樣不得靜默放過。
    // 這與「壞資料形狀跳過該列」是刻意分開的兩類:InvalidOperationException 被 savepoint 的
    // catch-when 排除,所以會直接讓開機失敗,而不是留下一個標準不可讀的名字。----

    [SkippableFact]
    public async Task Migration_NonNormalizableName_FailsFast()
    {
        _fx.SkipIfUnavailable();
        const string t = "skillrepo-mignorm";
        await Repo.CreateAsync(
            t, Meta("Bad_Name", "name: Bad_Name\nflow: v1\n"), "admin-a", default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DbBootstrap.RunAsync(_fx.DataSource!, NullLogger.Instance));

        Assert.Contains("無法把既有 Skill 名稱遷移為標準格式", error.Message);
        Assert.Contains("Bad_Name", error.Message);
        // 原列逐 byte 未動(不得「盡量改一半」)。
        var kept = await Repo.GetAsync(t, "Bad_Name", default);
        Assert.NotNull(kept);
        Assert.Equal("name: Bad_Name\nflow: v1\n", kept!.Definition);
    }
}
