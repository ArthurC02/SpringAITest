using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// Agent Skill 匯入 / 內部 package 端點 / definition-only agentic 拒絕(AST-P0-002/003/009/010/012/013)。
/// controller 層:走真實 app(TestWebAppFactory)+ 行程記憶體 repo + FakeSkillPackageValidator(依 name 腳本化)。
/// package bytes 由 backend 對「實際儲存的原始 zip」計算 SHA-256 → 儲存與匯出以 bytes 為權威。
/// </summary>
public sealed class SkillImportTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillImportTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant).WithUser("admin-a");

    private HttpClient User(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("USER").WithTenant(tenant).WithUser("user-a");

    private FakeSkillPackageValidator Pkg => (FakeSkillPackageValidator)_factory.Fake<ISkillPackageValidator>();

    private static string Yaml(string name, string description = "季報問答")
        => $"name: {name}\ndescription: {description}\nrequired_role: USER\nflow:\n  - node: query_intake\n";

    private static JsonObject Body(string definition) => new() { ["definition"] = definition };

    // ---- zip helpers ----

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var s = entry.Open();
                s.Write(bytes, 0, bytes.Length);
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
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            });
    }

    /// <summary>抽 SKILL.md 的第一個 ```yaml 區塊內容(= 開場 "```yaml\n" 與收場 "\n```" 之間),
    /// 忠實模擬 workflow 匯入端對 flow SKILL.md 的抽取(05 §3.1 自包含格式)。</summary>
    private static byte[] ExtractYamlBlock(byte[] skillMd)
    {
        var md = Encoding.UTF8.GetString(skillMd);
        const string open = "```yaml\n";
        var start = md.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = md.IndexOf("\n```", start, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(md[start..end]);
    }

    private static MultipartFormDataContent Multipart(byte[] zip)
    {
        var fc = new ByteArrayContent(zip);
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new MultipartFormDataContent { { fc, "package", "pkg.zip" } };
    }

    private async Task<HttpResponseMessage> ImportAsync(HttpClient client, string name, byte[] zip)
        => await client.PostAsync($"/api/skills/{name}/import", Multipart(zip));

    private async Task<HttpResponseMessage> ImportDerivedAsync(HttpClient client, byte[] zip)
        => await client.PostAsync("/api/skills/import", Multipart(zip));

    /// <summary>腳本化「合法 agentic」回應:canonical 固定,kind=agentic。</summary>
    private void SetupAgentic(string name, string canonical, string description = "銷售小幫手")
        => Pkg.Setup(name, _ => new SkillPackageValidationResult(
            true,
            Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, description, "USER", "agentic"),
            canonical));

    [Fact]
    public async Task ImportDerived_UsesCanonicalName_AndOmitsExpectedName()
    {
        const string name = "server-derived-skill";
        var canonical = $"name: {name}\ndescription: server derived\nmetadata:\n  kind: agentic\n";
        var zip = Zip(("SKILL.md", Encoding.UTF8.GetBytes("server-derived")));
        Pkg.SetupDerived(_ => new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, "server derived", "USER", "agentic"), canonical));
        var callsBefore = Pkg.Calls.Count;

        var response = await ImportDerivedAsync(Admin(), zip);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(name, body["name"]!.GetValue<string>());
        Assert.Equal("agentic", body["kind"]!.GetValue<string>());
        var call = Assert.Single(Pkg.Calls.Skip(callsBefore));
        Assert.Null(call.ExpectedName);
        Assert.Equal(zip, call.Package);
        Assert.NotNull(await ((FakeSkillRepository)_factory.Fake<ISkillRepository>())
            .GetAsync("demo-a", name, default));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("kb-query")]
    public async Task ImportDerived_ReservedOrBuiltinName_Returns409(string name)
    {
        Pkg.SetupDerived(_ => new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, "reserved", "USER", "flow"), Yaml(name)));

        var response = await ImportDerivedAsync(
            Admin(), Zip(("SKILL.md", Encoding.UTF8.GetBytes("reserved"))));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("名稱與既有工作流同名",
            (await response.ReadJsonAsync())["message"]!.GetValue<string>());
        Assert.Null(await ((FakeSkillRepository)_factory.Fake<ISkillRepository>())
            .GetAsync("demo-a", name, default));
    }

    [Fact]
    public async Task ImportDerived_InvalidPackage_Returns422()
    {
        Pkg.SetupDerived(_ => new SkillPackageValidationResult(
            false,
            new[] { new SkillValidationError("invalid_frontmatter", "frontmatter 無效", 1) },
            null,
            null));

        var response = await ImportDerivedAsync(
            Admin(), Zip(("SKILL.md", Encoding.UTF8.GetBytes("invalid"))));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotNull((await response.ReadJsonAsync())["fieldErrors"]!["invalid_frontmatter"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad_name")]
    [InlineData("-bad")]
    public async Task ImportDerived_MalformedWorkflowName_Returns502(string name)
    {
        Pkg.SetupDerived(_ => new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, "d", "USER", "flow"),
            $"name: {name}\ndescription: d\nflow: []\n"));

        var response = await ImportDerivedAsync(
            Admin(), Zip(("SKILL.md", Encoding.UTF8.GetBytes("malformed"))));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("違反契約",
            (await response.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task UserImportDerived_Oversize_Returns403BeforeBodyRead()
    {
        var callsBefore = Pkg.Calls.Count;
        var response = await User().PostAsync(
            "/api/skills/import",
            Multipart(new byte[26 * 1024 * 1024]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(callsBefore, Pkg.Calls.Count);
    }

    // ==================================================================
    // AST-P0-001(controller 觀察面)+ AST-P0-009:import 寫入 + 內部 package 端點 + 公開 JSON 不含 package
    // ==================================================================

    [Fact]
    public async Task Import_Agentic_WritesMetadataKindCanonicalAndPackageSha_InOneRevision()
    {
        const string name = "imp_sales_helper";
        var canonical = "kind: agentic\nname: imp_sales_helper\ndescription: 銷售小幫手\n";
        var zip = Zip(("SKILL.md", Encoding.UTF8.GetBytes("---\nname: imp_sales_helper\n---\n身體")));
        SetupAgentic(name, canonical);

        var resp = await ImportAsync(Admin(), name, zip);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(name, body["name"]!.GetValue<string>());
        Assert.Equal("銷售小幫手", body["description"]!.GetValue<string>());
        Assert.Equal("USER", body["required_role"]!.GetValue<string>());
        // definition = 引擎回報的 canonical(不是原始 zip);package bytes 不出現在 JSON。
        Assert.Equal(canonical, body["definition"]!.GetValue<string>());
        Assert.DoesNotContain("package", body.AsObject().Select(p => p.Key));

        // 單一 revision 同時記 definition_sha256 與 package_sha256(package_sha256 = 對原始 zip 的雜湊)。
        var revisions = (await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        var rev = Assert.Single(revisions)!;
        Assert.Equal(1, rev["revision"]!.GetValue<int>());
        Assert.Equal(canonical, rev["definition"]!.GetValue<string>());
        Assert.Equal(SkillHash.Sha256(canonical), rev["definition_sha256"]!.GetValue<string>());
        Assert.Equal(SkillHash.Sha256(zip), rev["package_sha256"]!.GetValue<string>());

        // 公開 GET 單筆:不含 package 欄。
        var single = (await (await Admin().GetAsync($"/api/skills/{name}")).ReadJsonAsync()).AsObject();
        Assert.DoesNotContain("package", single.Select(p => p.Key));
    }

    [Fact] // AST-P0-009:內部 package GET → 正確 token/tenant zip、錯 token 401、tenant-b 404;flow 無 package → 404。
    public async Task PackageEndpoint_Zip_401WrongToken_404CrossTenantAndFlow()
    {
        const string name = "imp_pkg_read";
        var zip = Zip(("SKILL.md", Encoding.UTF8.GetBytes("agentic-pkg-bytes")));
        SetupAgentic(name, "kind: agentic\nname: imp_pkg_read\n");
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin("demo-a"), name, zip)).StatusCode);

        // 正確 token + tenant-a → zip,bytes 逐字等於已儲存 package。
        var ok = await Admin("demo-a").GetAsync($"/api/skills/{name}/package");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/zip", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal(zip, await ok.Content.ReadAsByteArrayAsync());

        // 錯/缺 internal token → 401(InternalTokenMiddleware 全域守門)。
        var wrongToken = _factory.CreateClient();
        wrongToken.DefaultRequestHeaders.Add("X-Internal-Token", "nope");
        wrongToken.WithTenant("demo-a");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await wrongToken.GetAsync($"/api/skills/{name}/package")).StatusCode);

        // 跨租戶 → 404(不洩漏存在性)。
        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin("demo-b").GetAsync($"/api/skills/{name}/package")).StatusCode);

        // flow skill(無 package)→ 404。
        await Admin("demo-a").PostAsJsonAsync("/api/skills", Body(Yaml("imp_flow_nopkg")));
        Assert.Equal(HttpStatusCode.NotFound,
            (await Admin("demo-a").GetAsync("/api/skills/imp_flow_nopkg/package")).StatusCode);
    }

    // ==================================================================
    // AST-P0-010:USER import → 403,早於 package/body 驗證(validator 完全未被呼叫)
    // ==================================================================

    [Fact]
    public async Task UserImport_Returns403_BeforeValidation()
    {
        const string name = "imp_user_denied";
        // 就算腳本化了,USER 也不該走到 validator。
        SetupAgentic(name, "kind: agentic\nname: imp_user_denied\n");

        var resp = await ImportAsync(User(), name, Zip(("SKILL.md", new byte[] { 1, 2, 3 })));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("權限不足，無法存取 Skill",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
        Assert.DoesNotContain(Pkg.Calls, c => c.ExpectedName == name);
    }

    [Fact] // 授權早於 body:USER 送非 multipart 垃圾 body 仍是 403(不是 400/415),且不觸及 validator。
    public async Task UserImport_InvalidBody_Returns403_NotBodyError()
    {
        const string name = "imp_user_badbody";
        var resp = await User().PostAsync(
            $"/api/skills/{name}/import", new StringContent("garbage", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.DoesNotContain(Pkg.Calls, c => c.ExpectedName == name);
    }

    // ==================================================================
    // AST-P0-012:invalid → 受控 422;unreachable → 502;兩者都不寫 revision,且不替換既有 package
    // ==================================================================

    [Fact]
    public async Task Import_Invalid_Returns422_AndWritesNothing()
    {
        const string name = "imp_invalid";
        Pkg.Setup(name, _ => new SkillPackageValidationResult(
            false,
            new[] { new SkillValidationError("path_traversal", "非法路徑：../x", null) },
            Skill: null,
            CanonicalDefinition: null));

        var resp = await ImportAsync(Admin(), name, Zip(("SKILL.md", new byte[] { 9 })));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("Skill 套件驗證失敗", body["message"]!.GetValue<string>());
        Assert.NotNull(body["fieldErrors"]!["path_traversal"]);

        // 零副作用:單筆 404、revisions 404。
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}/revisions")).StatusCode);
    }

    [Fact]
    public async Task Import_Unreachable_Returns502_AndWritesNothing()
    {
        const string name = "imp_unreachable";
        Pkg.SetupUnreachable(name);

        var resp = await ImportAsync(Admin(), name, Zip(("SKILL.md", new byte[] { 9 })));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Contains("Skill 套件驗證服務呼叫失敗",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}")).StatusCode);
    }

    [Fact] // 匯入失敗不得替換既有 package / 不得新增 revision(R1)。
    public async Task ImportFailure_OverExisting_KeepsPreviousPackageAndRevision()
    {
        const string name = "imp_fail_keeps";
        var zip1 = Zip(("SKILL.md", Encoding.UTF8.GetBytes("v1-bytes")));
        SetupAgentic(name, "kind: agentic\nname: imp_fail_keeps\nv: 1\n");
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin(), name, zip1)).StatusCode);

        // 第二次匯入失敗(unreachable)。
        Pkg.Setup(name, _ => throw new Backend.Api.Common.ApiException(502, "boom"));
        Assert.Equal(HttpStatusCode.BadGateway,
            (await ImportAsync(Admin(), name, Zip(("SKILL.md", new byte[] { 0 })))).StatusCode);

        // 既有 package 與 revision 未變(仍 v1、rev1)。
        var pkg = await Admin().GetAsync($"/api/skills/{name}/package");
        Assert.Equal(zip1, await pkg.Content.ReadAsByteArrayAsync());
        var revisions = (await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Single(revisions);
    }

    // ==================================================================
    // AST-P0-013:definition-only 寫入不得建立/更新 agentic
    // ==================================================================

    [Fact]
    public async Task DefinitionOnlyUpdate_OfAgenticSkill_Rejected_NothingChanges()
    {
        const string name = "imp_agentic_locked";
        var zip = Zip(("SKILL.md", Encoding.UTF8.GetBytes("agentic-body")));
        var canonical = "kind: agentic\nname: imp_agentic_locked\n";
        SetupAgentic(name, canonical);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin(), name, zip)).StatusCode);

        // 對既有 agentic skill 送 definition-only PUT(flow 定義)→ 固定 409。
        var resp = await Admin().PutAsJsonAsync($"/api/skills/{name}", Body(Yaml(name)));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // definition / package / revision 全部不變。
        var single = await (await Admin().GetAsync($"/api/skills/{name}")).ReadJsonAsync();
        Assert.Equal(canonical, single["definition"]!.GetValue<string>());
        Assert.Equal(1, single["current_revision"]!.GetValue<int>());
        Assert.Equal(zip, await (await Admin().GetAsync($"/api/skills/{name}/package")).Content.ReadAsByteArrayAsync());
        var revisions = (await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Single(revisions);
    }

    [Fact] // definition-only create 宣告 kind: agentic → 引擎 validate 判 invalid → 422,零寫入(AST-P0-013)。
    public async Task DefinitionOnlyCreate_AgenticKind_Rejected()
    {
        const string name = "imp_agentic_create";
        // 真 workflow 對 `kind: agentic` 的 definition-only 提交回 valid:false / agentic_requires_import;
        // backend 走既有 invalid→422 對映(不是 controller 的 kind 猜測)。
        var yaml = $"name: {name}\ndescription: x\nkind: agentic\nflow:\n  - node: query_intake\n";

        var resp = await Admin().PostAsJsonAsync("/api/skills", Body(yaml));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("Skill 定義驗證失敗", body["message"]!.GetValue<string>());
        Assert.NotNull(body["fieldErrors"]!["agentic_requires_import"]);

        // 零副作用:definition / metadata / package / revision / 兩個 hash 皆未變(此名從未存在)。
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}/revisions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}/package")).StatusCode);
    }

    // ==================================================================
    // AST-P0-002:agentic export = 已儲存 package bytes 逐 entry 逐 byte 相同
    // ==================================================================

    [Fact]
    public async Task Export_Agentic_ReturnsStoredPackageEntriesVerbatim()
    {
        const string name = "imp_export_agentic";
        var skillMd = Encoding.UTF8.GetBytes("---\nname: imp_export_agentic\nkind: agentic\n---\n指令內文");
        var guide = Encoding.UTF8.GetBytes("參考資料\ttab 與中文 <>&");
        var zip = Zip(("SKILL.md", skillMd), ("references/guide.md", guide));
        SetupAgentic(name, "kind: agentic\nname: imp_export_agentic\n");
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin(), name, zip)).StatusCode);

        var resp = await Admin().GetAsync($"/api/skills/{name}/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var entries = ReadZip(await resp.Content.ReadAsByteArrayAsync());

        Assert.Equal(
            new[] { "SKILL.md", "references/guide.md" }.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(skillMd, entries["SKILL.md"]);
        Assert.Equal(guide, entries["references/guide.md"]);
    }

    // ==================================================================
    // AST-P0-003:flow export → import → export 保留嵌入 definition bytes(自包含 SKILL.md)
    // ==================================================================

    [Fact]
    public async Task Flow_ExportImportExport_PreservesEmbeddedDefinitionBytes()
    {
        const string name = "imp_flow_roundtrip";
        // 刻意含 tab / 中文 / 特殊字元 / 無結尾換行,考驗 byte 保真。
        var definition = "name: imp_flow_roundtrip\r\ndescription: 有\ttab <>&\"'\nflow:\n\t- node: x  # 無換行";
        // 直接以 fake repo 種入(繞過 flow validate 的內容限制),模擬既有 flow skill。
        var repo = (FakeSkillRepository)_factory.Fake<ISkillRepository>();
        await repo.CreateAsync(
            "demo-a", new Skill(name, "d", definition, "USER", true, 0, default, default), "admin-a", default);

        // export1:flow 匯出為自包含 {name}/SKILL.md,definition 嵌在 ```yaml 區塊。
        var zip1 = await (await Admin().GetAsync($"/api/skills/{name}/export")).Content.ReadAsByteArrayAsync();
        var yaml1 = ExtractYamlBlock(ReadZip(zip1)[$"{name}/SKILL.md"]);

        // import(fake 剝除單一頂層資料夾後抽 SKILL.md 的第一個 ```yaml 區塊當 canonical,kind=flow — 對齊引擎抽取契約)。
        Pkg.Setup(name, uploaded =>
        {
            var skillYaml = Encoding.UTF8.GetString(ExtractYamlBlock(ReadZip(uploaded)[$"{name}/SKILL.md"]));
            return new SkillPackageValidationResult(
                true, Array.Empty<SkillValidationError>(),
                new SkillMetadata(name, "d", "USER", "flow"), skillYaml);
        });
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin(), name, zip1)).StatusCode);

        // flow 匯入不存 package → /package 應 404(仍是 flow)。
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/skills/{name}/package")).StatusCode);

        // export2:嵌入的 definition bytes 完全相同。
        var zip2 = await (await Admin().GetAsync($"/api/skills/{name}/export")).Content.ReadAsByteArrayAsync();
        Assert.Equal(yaml1, ExtractYamlBlock(ReadZip(zip2)[$"{name}/SKILL.md"]));
        Assert.Equal(Encoding.UTF8.GetBytes(definition), ExtractYamlBlock(ReadZip(zip2)[$"{name}/SKILL.md"]));
    }

    [Fact]
    public async Task FlowImport_PreservesAllPackageEntries_AndExportsThemVerbatim()
    {
        const string name = "flow-package-resources";
        var definition = Yaml(name);
        var skillMd = Encoding.UTF8.GetBytes(
            $"---\nname: {name}\ndescription: d\n---\n\n```yaml\n{definition}\n```\n");
        var guide = Encoding.UTF8.GetBytes("guide\0bytes\t中文");
        var zip = Zip(("SKILL.md", skillMd), ("docs/guide.bin", guide));
        Pkg.Setup(name, _ => new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, "d", "USER", "flow"), definition));

        var imported = await ImportAsync(Admin(), name, zip);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal("flow", (await imported.ReadJsonAsync())["kind"]!.GetValue<string>());

        var exported = await Admin().GetAsync($"/api/skills/{name}/export");
        Assert.Equal(zip, await exported.Content.ReadAsByteArrayAsync());
        var entries = ReadZip(await exported.Content.ReadAsByteArrayAsync());
        Assert.Equal(skillMd, entries["SKILL.md"]);
        Assert.Equal(guide, entries["docs/guide.bin"]);

        // package reader 端點仍僅供 agentic runner；flow package 不公開。
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Admin().GetAsync($"/api/skills/{name}/package")).StatusCode);
        var revision = Assert.Single(
            (await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray())!;
        Assert.Equal("flow", revision["kind"]!.GetValue<string>());
        Assert.True(revision["has_package"]!.GetValue<bool>());
        Assert.Equal(SkillHash.Sha256(zip), revision["package_sha256"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("summarize")]
    [InlineData("template-compare")]
    public async Task Import_ReservedName_Returns409_AndWritesNothing(string name)
    {
        Pkg.Setup(name, _ => new SkillPackageValidationResult(
            true, Array.Empty<SkillValidationError>(),
            new SkillMetadata(name, "reserved", "USER", "flow"), Yaml(name)));

        var response = await ImportAsync(
            Admin(), name, Zip(("SKILL.md", Encoding.UTF8.GetBytes("validated"))));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            "名稱與既有工作流同名",
            (await response.ReadJsonAsync())["message"]!.GetValue<string>());
        var repo = (FakeSkillRepository)_factory.Fake<ISkillRepository>();
        Assert.Null(await repo.GetAsync("demo-a", name, default));
    }

    [Fact]
    public async Task Restore_FlowRevision_CreatesNewAuditRevision()
    {
        const string name = "restore-flow";
        var v1 = Yaml(name, "第一版");
        var v2 = Yaml(name, "第二版");
        Assert.Equal(HttpStatusCode.Created, (await Admin().PostAsJsonAsync("/api/skills", Body(v1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin().PutAsJsonAsync($"/api/skills/{name}", Body(v2))).StatusCode);

        var restored = await Admin().PostAsync(
            $"/api/skills/{name}/revisions/1/restore", content: null);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var body = await restored.ReadJsonAsync();
        Assert.Equal(3, body["current_revision"]!.GetValue<int>());
        Assert.Equal(v1, body["definition"]!.GetValue<string>());
        Assert.Equal("flow", body["kind"]!.GetValue<string>());
        var revisions = (await (await Admin().GetAsync(
            $"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(new[] { 3, 2, 1 }, revisions.Select(r => r!["revision"]!.GetValue<int>()));
        Assert.Equal(v1, revisions[0]!["definition"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restore_AgenticRevision_RestoresPackageAndCreatesNewAuditRevision()
    {
        const string name = "restore-agentic";
        var zip1 = Zip(
            ("SKILL.md", Encoding.UTF8.GetBytes("agentic-v1")),
            ("docs/v1.txt", Encoding.UTF8.GetBytes("v1")));
        var zip2 = Zip(
            ("SKILL.md", Encoding.UTF8.GetBytes("agentic-v2")),
            ("docs/v2.txt", Encoding.UTF8.GetBytes("v2")));
        Pkg.Setup(name, bytes =>
        {
            var version = ReadZip(bytes)["SKILL.md"].SequenceEqual(
                Encoding.UTF8.GetBytes("agentic-v1")) ? "v1" : "v2";
            return new SkillPackageValidationResult(
                true, Array.Empty<SkillValidationError>(),
                new SkillMetadata(name, version, "USER", "agentic"),
                $"name: {name}\ndescription: {version}\nmetadata:\n  kind: agentic\n");
        });

        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin(), name, zip1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ImportAsync(Admin(), name, zip2)).StatusCode);

        var restored = await Admin().PostAsync(
            $"/api/skills/{name}/revisions/1/restore", content: null);

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var body = await restored.ReadJsonAsync();
        Assert.Equal(3, body["current_revision"]!.GetValue<int>());
        Assert.Equal("v1", body["description"]!.GetValue<string>());
        Assert.Equal("agentic", body["kind"]!.GetValue<string>());
        Assert.Equal(
            zip1,
            await (await Admin().GetAsync($"/api/skills/{name}/export"))
                .Content.ReadAsByteArrayAsync());
        var revisions = (await (await Admin().GetAsync(
            $"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(new[] { 3, 2, 1 }, revisions.Select(r => r!["revision"]!.GetValue<int>()));
        Assert.All(revisions, r => Assert.Equal("agentic", r!["kind"]!.GetValue<string>()));
        Assert.All(revisions, r => Assert.True(r!["has_package"]!.GetValue<bool>()));
    }
}
