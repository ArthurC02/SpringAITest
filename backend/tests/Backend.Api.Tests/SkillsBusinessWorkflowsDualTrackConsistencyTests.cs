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
