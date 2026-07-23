using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// flow Skill 匯出為 Claude Skill 格式 zip(05 §3.1 自包含版)。zip 恰含一個檔 SKILL.md:
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
    private void Seed(string tenant, string name, string definition, string description = "季報問答")
        => Repo.CreateAsync(
            tenant,
            new Skill(name, description, definition, "USER", true, 0, default, default),
            "admin-a",
            CancellationToken.None).GetAwaiter().GetResult();

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

    // ---- AT2-15:flow 匯出 = 恰一個 entry SKILL.md(自包含,無 skill.yaml)----

    [Fact]
    public async Task Export_ZipContainsOnlySkillMd()
    {
        var client = Admin();
        await client.PostAsJsonAsync("/api/skills", Body(Yaml("at215_skill")));

        var entries = await ExportZipAsync(client, "at215_skill");

        Assert.Equal(new[] { "SKILL.md" }, entries.Keys.ToArray());
    }

    // ---- AT2-16:SKILL.md frontmatter 只有 name + description ----

    [Fact]
    public async Task Export_SkillMd_FrontmatterHasOnlyNameAndDescription()
    {
        var client = Admin();
        await client.PostAsJsonAsync("/api/skills", Body(Yaml("at216_skill", description: "季度營收問答")));

        var entries = await ExportZipAsync(client, "at216_skill");
        var md = Encoding.UTF8.GetString(entries["SKILL.md"]);

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
        var md = Encoding.UTF8.GetString(entries["SKILL.md"]);

        Assert.Contains("本 Skill 為 node-first 引擎的宣告式流程定義，權威內容即下方 ```yaml 區塊。", md);
        Assert.Contains("```yaml\n", md);
        Assert.EndsWith("\n```\n", md);
        // 嵌入的區塊內容 = definition。
        Assert.Equal(Yaml("at217_skill"), ExtractYamlBlock(md));
    }

    // ---- AT2-18:嵌入的 ```yaml 區塊內容逐 byte 等於 DB 的 definition(含 tab / 中文 / 無結尾換行 / 特殊字元)----

    [Fact]
    public async Task Export_EmbeddedYaml_IsByteForByteIdenticalToDefinition()
    {
        // 刻意含:tab 縮排、中文、CRLF 與 LF 混用、無結尾換行、特殊字元。
        var definition = "name: at218_skill\r\ndescription: 有\ttab 的描述 <>&\"'\nflow:\n\t- node: x  # 無結尾換行";
        Seed("demo-a", "at218_skill", definition);

        var entries = await ExportZipAsync(Admin(), "at218_skill");
        var block = ExtractYamlBlock(Encoding.UTF8.GetString(entries["SKILL.md"]));

        Assert.Equal(Encoding.UTF8.GetBytes(definition), Encoding.UTF8.GetBytes(block));
    }

    // ---- AT2-19:匯出不解析 definition — 不是合法 YAML 也照樣原樣打包 ----

    [Fact]
    public async Task Export_DoesNotParseDefinition_InvalidYamlStillExportsVerbatim()
    {
        // 完全不是合法 YAML(未閉合括號、tab、隨機符號)—— 繞過 validate 直接塞進 repo。
        var garbage = "{[this is not: yaml\t@@@ \x01 未閉合";
        Seed("demo-a", "at219_skill", garbage);

        var entries = await ExportZipAsync(Admin(), "at219_skill");
        var block = ExtractYamlBlock(Encoding.UTF8.GetString(entries["SKILL.md"]));

        Assert.Equal(Encoding.UTF8.GetBytes(garbage), Encoding.UTF8.GetBytes(block));
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

    [Fact]
    public async Task Export_SkillMd_EscapesDescriptionWithSpecialChars()
    {
        // 含冒號與雙引號 → 不是 plain-safe → 必須以雙引號包裹並逃脫(舊 POC 未逃脫會產生非法 YAML)。
        Seed("demo-a", "at220_escape", Yaml("at220_escape"), description: "營收: 100 \"高\"");

        var entries = await ExportZipAsync(Admin(), "at220_escape");
        var md = Encoding.UTF8.GetString(entries["SKILL.md"]);

        var lines = md.Split('\n');
        var end = Array.IndexOf(lines, "---", 1);
        var frontmatter = lines[1..end];

        Assert.Equal(
            new[] { "name: at220_escape", "description: \"營收: 100 \\\"高\\\"\"" },
            frontmatter);
    }
}
