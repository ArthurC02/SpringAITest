using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// flow Skill 匯出為 Claude Skill 格式 zip(05 §3.1 自包含版)。zip 恰含一個 entry `{name}/SKILL.md`
/// (05 §0:頂層資料夾名須等於 name):
/// 標準 frontmatter(name+description)+ body 以 fenced ```yaml 區塊嵌入 definition 原文,
/// 區塊內容逐 byte 等於 DB 的 definition;完全不解析/不執行;角色與 GET {name} 一致(USER 可用);跨租戶一律 404。
/// </summary>
public sealed class SkillExportTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillExportTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant);

    private HttpClient User(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("USER").WithTenant(tenant);

    private FakeSkillRepository Repo => (FakeSkillRepository)_factory.Fake<ISkillRepository>();

    private static string Yaml(string name, string description = "季報問答", string requiredRole = "USER")
        => $"name: {name}\ndescription: {description}\nrequired_role: {requiredRole}\nflow:\n  - node: query_intake\n";

    private static JsonObject Body(string definition) => new() { ["definition"] = definition };

    /// <summary>直接塞進 fake repository(繞過 validate),讓 definition 內容不受 CRUD 驗證限制。</summary>
    private Task Seed(string tenant, string name, string definition, string description = "季報問答")
        => Repo.CreateAsync(
            tenant,
            new Skill(name, description, definition, "USER", true, 0, default, default),
            "admin-a",
            CancellationToken.None);

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

    private async Task<Dictionary<string, byte[]>> ExportZipAsync(HttpClient client, string name)
    {
        var resp = await client.GetAsync($"/api/skills/{name}/export");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"{name}.zip", resp.Content.Headers.ContentDisposition!.FileNameStar
            ?? resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        return ReadZip(await resp.Content.ReadAsByteArrayAsync());
    }

    /// <summary>萃取 SKILL.md 的第一個 ```yaml 區塊內容(= 開場 "```yaml\n" 與收場 "\n```" 之間),
    /// 忠實模擬 workflow 匯入端的抽取。與 exporter 的嵌入格式對稱。</summary>
    private static string ExtractYamlBlock(string md)
    {
        const string open = "```yaml\n";
        var start = md.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = md.IndexOf("\n```", start, StringComparison.Ordinal);
        return md[start..end];
    }

    // ---- AT2-15:flow 匯出 = 恰一個 entry {name}/SKILL.md(自包含,無 skill.yaml)----

    [Fact]
    public async Task Export_ZipContainsOnlySkillMd()
    {
        var client = Admin();
        await client.PostAsJsonAsync("/api/skills", Body(Yaml("at215_skill")));

        var entries = await ExportZipAsync(client, "at215_skill");

        Assert.Equal(new[] { "at215_skill/SKILL.md" }, entries.Keys.ToArray());
    }

    // ---- 05 §0:頂層資料夾名 = frontmatter name = skill.Name(解壓後資料夾名不再由工具決定)----

    [Fact]
    public async Task Export_TopLevelFolder_EqualsSkillNameAndFrontmatterName()
    {
        const string name = "year-compare";
        await Seed("demo-a", name, Yaml(name));

        var entries = await ExportZipAsync(Admin(), name);

        var entryPath = Assert.Single(entries.Keys);
        Assert.Equal($"{name}/SKILL.md", entryPath);
        Assert.Equal(name, entryPath[..entryPath.IndexOf('/')]);
        Assert.Contains($"name: {name}\n", Encoding.UTF8.GetString(entries[entryPath]));
    }

    // ---- AT2-16:SKILL.md frontmatter 只有 name + description ----

    [Fact]
    public async Task Export_SkillMd_FrontmatterHasOnlyNameAndDescription()
    {
        var client = Admin();
        await client.PostAsJsonAsync("/api/skills", Body(Yaml("at216_skill", description: "季度營收問答")));

        var entries = await ExportZipAsync(client, "at216_skill");
        var md = Encoding.UTF8.GetString(entries["at216_skill/SKILL.md"]);

        // frontmatter = 第一組 --- 與第二個 --- 之間的行。
        var lines = md.Split('\n');
        Assert.Equal("---", lines[0]);
        var end = Array.IndexOf(lines, "---", 1);
        var frontmatter = lines[1..end];

        Assert.Equal(
            new[] { "name: at216_skill", "description: 季度營收問答" },
            frontmatter);
    }

    // ---- AT2-17:body 以 fenced ```yaml 區塊嵌入 definition(自包含,不再指向 skill.yaml)----

    [Fact]
    public async Task Export_SkillMd_EmbedsDefinitionInFencedYamlBlock()
    {
        var client = Admin();
        await client.PostAsJsonAsync("/api/skills", Body(Yaml("at217_skill")));

        var entries = await ExportZipAsync(client, "at217_skill");
        var md = Encoding.UTF8.GetString(entries["at217_skill/SKILL.md"]);

        Assert.Contains("本 Skill 為 node-first 引擎的宣告式流程定義，權威內容即下方 ```yaml 區塊。", md);
        Assert.Contains("```yaml\n", md);
        Assert.EndsWith("\n```\n", md);
        // 嵌入的區塊內容 = definition。
        Assert.Equal(Yaml("at217_skill"), ExtractYamlBlock(md));
    }

    // ---- AT2-18 + AT2-19:嵌入的 ```yaml 區塊逐 byte 等於 DB 的 definition,且匯出完全不解析 definition。
    // 兩者是同一條生產行為(SkillExporter 只做字串串接:不解析、不正規化),用一個同時「不是合法 YAML」
    // 且含 tab / 中文 / CRLF+LF 混用 / 控制字元 / 無結尾換行的 definition 一次覆蓋兩個等價類。----

    [Fact]
    public async Task Export_DoesNotParseDefinition_InvalidYamlStillExportsVerbatim()
    {
        // 未閉合括號 + 隨機符號 → 完全不是合法 YAML;同時含 tab 縮排、中文、CRLF/LF 混用、
        // 控制字元、無結尾換行 —— 繞過 validate 直接塞進 repo。
        var definition = "{[this is not: yaml\t@@@ \x01 未閉合\r\ndescription: 有\ttab <>&\"'\nflow:\n\t- node: x  # 無結尾換行";
        await Seed("demo-a", "at218_skill", definition);

        var entries = await ExportZipAsync(Admin(), "at218_skill");
        var block = ExtractYamlBlock(Encoding.UTF8.GetString(entries["at218_skill/SKILL.md"]));

        Assert.Equal(Encoding.UTF8.GetBytes(definition), Encoding.UTF8.GetBytes(block));
    }

    // ---- 角色:USER 也能匯出(與 GET {name} 一致,不掛 SkillAdminOnly)----

    [Fact]
    public async Task Export_User_CanExport()
    {
        await Admin().PostAsJsonAsync("/api/skills", Body(Yaml("at215_user_export")));

        var resp = await User().GetAsync("/api/skills/at215_user_export/export");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- 404:不存在的 name / 跨租戶(不洩漏存在性)----

    [Fact]
    public async Task Export_MissingName_Returns404()
    {
        var resp = await Admin().GetAsync("/api/skills/at215_ghost/export");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("找不到 Skill：at215_ghost",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_CrossTenant_Returns404()
    {
        await Admin("demo-a").PostAsJsonAsync("/api/skills", Body(Yaml("at215_tenant")));

        var resp = await Admin("demo-b").GetAsync("/api/skills/at215_tenant/export");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- YAML escaping 修正(03-design §2.4):含 `:`/引號的 description 以雙引號 scalar 並逃脫寫入 ----

    // ---- name 也必須是 YAML-safe scalar:YAML 1.1 隱式 token(no/on/true)與純數字裸寫會被
    // 匯入端解成 bool/int,frontmatter name 不再是字串 → 匯出的 zip 無法再匯入。----

    // 007 / 0x1f / 0b101 全部命中同一行 `char.IsAsciiDigit(s[0])`(SkillExporter.cs:112 一次收掉
    // decimal/八進位/hex/binary)→ 數字開頭只留一個代表值;`no` 走的是另一條 ImplicitNonStrings 分支。
    [Theory]
    [InlineData("no")]  // YAML 1.1 隱式 bool
    [InlineData("007")] // 數字起手式(前導零八進位/hex/binary 同一分支)
    public async Task Export_SkillMd_QuotesYamlImplicitTypedName(string name)
    {
        await Seed("demo-a", name, Yaml(name));

        var entries = await ExportZipAsync(Admin(), name);
        var md = Encoding.UTF8.GetString(entries[$"{name}/SKILL.md"]);

        var lines = md.Split('\n');
        // 資料夾名仍是裸名(= skill.Name),與 frontmatter 的字串值相等。
        Assert.Equal($"name: \"{name}\"", lines[1]);
    }

    // ---- B3:simpleForm 是 UI 便利欄,非可攜 skill 內容 — export zip 一律不含它 ----

    [Fact]
    public async Task Export_DoesNotIncludeSimpleForm()
    {
        const string name = "at_sf_export";
        await Repo.CreateAsync(
            "demo-a",
            new Skill(name, "季報問答", Yaml(name), "USER", true, 0, default, default,
                SimpleForm: "{\"templateId\":\"template-stats\",\"form\":{\"topK\":\"50\"}}"),
            "admin-a", CancellationToken.None);

        var entries = await ExportZipAsync(Admin(), name);

        // 只有 SKILL.md,沒有任何攜帶表單狀態的額外 entry。
        Assert.Equal(new[] { $"{name}/SKILL.md" }, entries.Keys.ToArray());
        var md = Encoding.UTF8.GetString(entries[$"{name}/SKILL.md"]);
        Assert.DoesNotContain("simpleForm", md);
        Assert.DoesNotContain("templateId", md);
        Assert.DoesNotContain("template-stats", md);
    }

    // description 空字串是**可達**狀態:引擎未回報 description 時 WorkflowSkillValidator 預設 string.Empty。
    // frontmatter 必須寫成顯式空字串 `description: ""` —— 裸寫 `description:` 會被匯入端解析成 null,
    // frontmatter 就不再是「name+description 兩個字串」的標準形狀。
    [Fact]
    public async Task Export_BlankDescription_ProducesRoundTrippableFrontmatter()
    {
        const string name = "at221-blank-desc";
        await Seed("demo-a", name, Yaml(name), description: string.Empty);

        var entries = await ExportZipAsync(Admin(), name);
        var md = Encoding.UTF8.GetString(entries[$"{name}/SKILL.md"]);

        var lines = md.Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        Assert.Equal(new[] { $"name: {name}", "description: \"\"" }, lines[1..end]);
    }

    // export → import round trip:同一個 skill 匯得出來就必須匯得回去。
    // 匯出的 zip 交給真的 WorkflowSkillPackageValidator(workflow 以 stub 取代,回報的 description
    // 直接取自剛匯出的 frontmatter,不是測試自己寫死的值)—— 空 description 不得被判 502。
    [Fact]
    public async Task Export_BlankDescription_RoundTripsBackThroughPackageImportValidation()
    {
        const string name = "at221-roundtrip";
        var definition = Yaml(name, description: "\"\"");
        await Seed("demo-a", name, definition, description: string.Empty);

        var exported = await Admin().GetAsync($"/api/skills/{name}/export");
        var zip = await exported.Content.ReadAsByteArrayAsync();
        var md = Encoding.UTF8.GetString(ReadZip(zip)[$"{name}/SKILL.md"]);

        // 匯入端讀 frontmatter 與第一個 ```yaml 區塊,就是 workflow 會回報的 skill metadata 與 canonical。
        var frontmatter = ParseFrontmatter(md);
        Assert.Equal(string.Empty, frontmatter["description"]);
        var stub = new StubWorkflow(
            $$"""
              {"valid":true,"errors":[],
               "skill":{"name":{{Json(frontmatter["name"])}},"description":{{Json(frontmatter["description"])}},
                        "required_role":"USER","kind":"flow"},
               "canonical_definition":{{Json(ExtractYamlBlock(md))}}}
              """);

        var result = await new WorkflowSkillPackageValidator(
                new HttpClient(stub), "http://workflow:8001", "tok")
            .ValidatePackageAsync(zip, $"{name}.zip", name, "demo-a", "admin-a", "ADMIN", default);

        Assert.True(result.Valid);
        Assert.Equal(string.Empty, result.Skill!.Description);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>把 SKILL.md 的 frontmatter 當 YAML 解析(忠實模擬匯入端,不做字串裁切)。</summary>
    private static Dictionary<string, string> ParseFrontmatter(string md)
    {
        var lines = md.Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        return new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, string>>(string.Join('\n', lines[1..end]));
    }

    /// <summary>固定回同一份 workflow /skills/validate-package 回應的 handler。</summary>
    private sealed class StubWorkflow : HttpMessageHandler
    {
        private readonly string _body;

        public StubWorkflow(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task Export_SkillMd_EscapesDescriptionWithSpecialChars()
    {
        // 含冒號與雙引號 → 不是 plain-safe → 必須以雙引號包裹並逃脫(舊 POC 未逃脫會產生非法 YAML)。
        await Seed("demo-a", "at220_escape", Yaml("at220_escape"), description: "營收: 100 \"高\"");

        var entries = await ExportZipAsync(Admin(), "at220_escape");
        var md = Encoding.UTF8.GetString(entries["at220_escape/SKILL.md"]);

        var lines = md.Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        var frontmatter = lines[1..end];

        Assert.Equal(
            new[] { "name: at220_escape", "description: \"營收: 100 \\\"高\\\"\"" },
            frontmatter);
    }

    // ---- IsPlainSafe 的「開頭/結尾」守衛:上面那條把 `:` 放在字串**中間**,走的是逐字元的 foreach 分支,
    // 碰不到開頭指示字元(`-?:,[]{}#&*!|>'"%@` 等)、數值起手式(`.`/`+`)與前後空白這三條先行守衛。
    // 前導/尾端空白是同一個 if 的兩個子句(s[0] / s[^1]),各留一個代表值 —— 少寫一半照樣會產出
    // 匯入端會 strip 掉空白的裸 scalar。----
    [Theory]
    [InlineData("at222-lead-dash", "-100 成長")]  // 開頭指示字元
    [InlineData("at222-lead-dot", ".5 倍營收")]   // 數值起手式(`.`/`+`/數字同一條)
    [InlineData("at222-lead-space", " 前導空白")] // 前導空白
    [InlineData("at222-trail-space", "尾端空白 ")] // 尾端空白
    public async Task Export_SkillMd_QuotesDescriptionWithLeadingIndicatorOrEdgeWhitespace(
        string name, string description)
    {
        await Seed("demo-a", name, Yaml(name), description);

        var entries = await ExportZipAsync(Admin(), name);
        var md = Encoding.UTF8.GetString(entries[$"{name}/SKILL.md"]);

        var lines = md.Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        Assert.Equal(new[] { $"name: {name}", $"description: \"{description}\"" }, lines[1..end]);
    }

    // ---- 逃脫表的其餘分支:反斜線/換行/CR/tab 必須寫成**兩字元**逃脫序列 —— 若換行原樣輸出,
    // frontmatter 會被從中間切成多行而不再是 name+description 兩個欄位(zip 匯不回去)。
    // 控制字元(\x01)沒有對應 case,走 `_ => c.ToString()` 原樣落在雙引號 scalar 內 —— 這裡是釘住現狀。----
    [Fact]
    public async Task Export_SkillMd_EscapesBackslashNewlineTabInDescription()
    {
        const string name = "at222-escape-ctrl";
        await Seed("demo-a", name, Yaml(name), "第一行\n第二行\ttab\\slash\r尾\u0001");

        var entries = await ExportZipAsync(Admin(), name);
        var md = Encoding.UTF8.GetString(entries[$"{name}/SKILL.md"]);

        var lines = md.Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        Assert.Equal(
            new[]
            {
                $"name: {name}",
                "description: \"第一行\\n第二行\\ttab\\\\slash\\r尾\u0001\"",
            },
            lines[1..end]);
    }
}
