using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>P2–P5 相容窗口契約；P5 C8 收斂時本檔必須與雙軌一起刪除。</summary>
public sealed class SkillsBusinessWorkflowsDualTrackConsistencyTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillsBusinessWorkflowsDualTrackConsistencyTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin()
        => _factory.CreateInternalClient().WithTenant("dual-track").WithRole("ADMIN").WithUser("admin");

    private static JsonObject Body(string name, string description = "dual") => new()
    {
        ["definition"] = $"name: {name}\ndescription: {description}\nrequired_role: USER\nflow:\n  - node: query_intake\n",
    };

    [Fact]
    public async Task DetailReads_AreByteEquivalent()
    {
        const string name = "dual-read";
        await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));

        var skill = await Admin().GetStringAsync($"/api/skills/{name}");
        var workflow = await Admin().GetStringAsync($"/api/business-workflows/{name}");

        Assert.Equal(skill, workflow);
    }

    // DetailReads_AreByteEquivalent 的另一半:雙軌的第二條讀取路徑。兩個 controller 對同一列各自跑
    // `Package ?? SkillExporter.ToZip(...)` 的 fallback,definition-only flow 匯出的內容必須一致。
    // 比對解壓後的 entry 而非原始 zip bytes:zip header 的時間戳取自 ZipArchive 建 entry 當下的
    // DateTimeOffset.Now(DOS 格式 2 秒刻度),兩次請求跨過刻度就會不等 —— 那是打包時間,不是契約。
    [Fact]
    public async Task ExportReads_AreEntryEquivalent()
    {
        const string name = "dual-export";
        await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));

        var skill = await Admin().GetAsync($"/api/skills/{name}/export");
        var workflow = await Admin().GetAsync($"/api/business-workflows/{name}/export");

        Assert.Equal(HttpStatusCode.OK, skill.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workflow.StatusCode);
        Assert.Equal(
            skill.Content.Headers.ContentType!.MediaType, workflow.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            skill.Content.Headers.ContentDisposition!.FileName,
            workflow.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal(await SingleEntryAsync(skill), await SingleEntryAsync(workflow));
    }

    /// <summary>匯出 zip 的唯一 entry(名稱 + 內容);zip 外殼的打包時間戳不納入比對。</summary>
    private static async Task<(string Name, string Content)> SingleEntryAsync(HttpResponseMessage response)
    {
        using var archive = new ZipArchive(
            new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var entry = archive.Entries.Single();
        using var reader = new StreamReader(entry.Open());
        return (entry.FullName, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task BusinessPost_RevivesSoftDeletedFlow_AndBumpsSharedRevision()
    {
        const string name = "dual-revive";
        await Admin().PostAsJsonAsync("/api/business-workflows", Body(name, "v1"));
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().DeleteAsync($"/api/skills/{name}")).StatusCode);

        var revived = await Admin().PostAsJsonAsync("/api/business-workflows", Body(name, "v2"));

        Assert.Equal(HttpStatusCode.Created, revived.StatusCode);
        Assert.Equal(2, (await revived.ReadJsonAsync())["current_revision"]!.GetValue<int>());
        var revisions = (await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(new[] { 2, 1 }, revisions.Select(row => row!["revision"]!.GetValue<int>()));
    }

    [Fact]
    public async Task BusinessPut_DoesNotReviveSoftDeletedFlow()
    {
        const string name = "dual-put-deleted";
        await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));
        await Admin().DeleteAsync($"/api/business-workflows/{name}");

        var response = await Admin().PutAsJsonAsync("/api/business-workflows/" + name, Body(name, "v2"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single((await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray());
    }

    [Fact]
    public async Task BusinessPost_ReservedName_Returns409()
    {
        var response = await Admin().PostAsJsonAsync("/api/business-workflows", Body("rag-qa"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // 409 的另一個來源:撞的不是保留字,而是一列**仍啟用的 agentic**。BusinessPost_ReservedName_Returns409
    // 停在 controller 內的保留字檢查(還沒碰 repo);這條要走到 CreateAsync 的 `WHERE NOT skill.enabled`
    // → null → 409,且那列 Agent Skill 不得被 flow 覆寫(kind/package/revision 皆不動)。
    [Fact]
    public async Task BusinessPost_EnabledAgenticName_Returns409_AndLeavesAgenticIntact()
    {
        const string name = "dual-live-agentic";
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 41, 42 };
        await repo.ImportAsync(
            "dual-track",
            new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);

        var response = await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "Business Workflow 名稱已存在：" + name,
            (await response.ReadJsonAsync())["message"]!.GetValue<string>());
        var stored = await repo.GetAsync("dual-track", name, CancellationToken.None);
        Assert.Equal("agentic", stored!.Kind);
        Assert.Equal(package, stored.Package);
        Assert.Single(await repo.ListRevisionsAsync("dual-track", name, CancellationToken.None));
    }

    // A2-5 的寫入面:同一個「flow 列被 import 覆蓋成 agentic」情境 —— GET 由下一條釘住,
    // PUT/DELETE 也必須被 kind='flow' fence 擋成 404,且不得改寫或軟刪那列 agentic
    // (business-workflows 路由永遠不能靜靜地動到 Agent Skill,即使這個名字原本是它建的)。
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task BusinessWrites_AgainstAgenticOverwrittenFlow_Return404_AndLeaveAgenticIntact(string method)
    {
        var name = "dual-agentic-write-" + method.ToLowerInvariant();
        Assert.Equal(
            HttpStatusCode.Created,
            (await Admin().PostAsJsonAsync("/api/business-workflows", Body(name))).StatusCode);
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 51, 52 };
        await repo.ImportAsync(
            "dual-track",
            new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);

        var request = new HttpRequestMessage(new HttpMethod(method), $"/api/business-workflows/{name}");
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(Body(name));
        }

        var response = await Admin().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // GetAsync 不回軟刪的列 → 非 null 同時證明「沒被 DELETE 軟刪」。
        var stored = await repo.GetAsync("dual-track", name, CancellationToken.None);
        Assert.Equal("agentic", stored!.Kind);
        Assert.Equal(package, stored.Package);
        Assert.Equal(2, stored.CurrentRevision);
    }

    // A2-5:先建 flow 列(經 /api/business-workflows),再以 import 覆蓋成 agentic ——
    // (a) business-workflows 路由(kind='flow' fence)必須跟著隱藏這個名字;
    // (b) /api/skills/{name}/revisions 與 restore 仍要能服務混合歷史(r1=flow, r2=agentic)。
    [Fact]
    public async Task FlowOverwrittenByAgenticImport_HidesFromBusinessRoute_ButKeepsMixedHistoryServiceable()
    {
        const string name = "dual-flow-then-agentic";
        Assert.Equal(
            HttpStatusCode.Created,
            (await Admin().PostAsJsonAsync("/api/business-workflows", Body(name))).StatusCode);

        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 31, 32, 33 };
        await repo.ImportAsync(
            "dual-track",
            new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);

        // (a) 覆蓋成 agentic 後,business-workflows 路由不再服務這個名字。
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Admin().GetAsync($"/api/business-workflows/{name}")).StatusCode);

        // (b) 混合歷史(r1=flow, r2=agentic)仍可經 /api/skills 服務。
        var revisions = (await (await Admin().GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(new[] { 2, 1 }, revisions.Select(r => r!["revision"]!.GetValue<int>()));
        Assert.Equal("flow", revisions.Single(r => r!["revision"]!.GetValue<int>() == 1)!["kind"]!.GetValue<string>());
        Assert.Equal(
            "agentic", revisions.Single(r => r!["revision"]!.GetValue<int>() == 2)!["kind"]!.GetValue<string>());

        // restore 仍可服務(回復 r1,重新驗證,新增 r3)。
        var restored = await Admin().PostAsync($"/api/skills/{name}/revisions/1/restore", content: null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var body = await restored.ReadJsonAsync();
        Assert.Equal(3, body["current_revision"]!.GetValue<int>());
        Assert.Equal("flow", body["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task BusinessPut_DefinitionNameMismatch_Returns422()
    {
        const string name = "dual-route-name";
        await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));

        var response = await Admin().PutAsJsonAsync(
            $"/api/business-workflows/{name}", Body("different-name"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotNull((await response.ReadJsonAsync())["fieldErrors"]!["name"]);
    }
}
