using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Skills;
using Backend.Api.Data.InMemory;

namespace Backend.Api.Tests;

public sealed class BusinessWorkflowApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public BusinessWorkflowApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithTenant(tenant).WithRole("ADMIN").WithUser("admin-a");

    private static JsonObject Body(string name) => new()
    {
        ["definition"] = $"name: {name}\ndescription: business flow\nrequired_role: USER\nflow:\n  - node: query_intake\n",
    };

    [Fact]
    public async Task Create_UsesBusinessWorkflowPath_AndRemainsVisibleThroughCompatibilitySkillPath()
    {
        var name = "business_flow_api";
        var response = await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/business-workflows/{name}", response.Headers.Location!.ToString());
        var body = await response.ReadJsonAsync();
        Assert.Equal("flow", body["kind"]!.GetValue<string>());

        var compatibility = await Admin().GetAsync($"/api/skills/{name}");
        Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
        Assert.Equal(
            body.ToJsonString(),
            (await compatibility.ReadJsonAsync()).ToJsonString());
    }

    [Fact]
    public async Task Reads_FilterAgentSkillKind_AndPreserveTenantIsolation()
    {
        const string name = "agent_only_business_filter";
        var repo = _factory.Fake<ISkillRepository>();
        await repo.ImportAsync(
            "demo-a",
            new Skill(name, "agent", "name: agent", "USER", true, 0, default, default, "agentic"),
            new byte[] { 1, 2, 3 },
            SkillHash.Sha256(new byte[] { 1, 2, 3 }),
            "admin-a",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/business-workflows/{name}")).StatusCode);
        Assert.DoesNotContain(
            (await (await Admin().GetAsync("/api/business-workflows")).ReadJsonAsync()).AsArray(),
            row => row!["name"]!.GetValue<string>() == name);

        await Admin().PostAsJsonAsync("/api/business-workflows", Body("tenant_a_business_flow"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Admin("demo-b").GetAsync("/api/business-workflows/tenant_a_business_flow")).StatusCode);
    }

    [Fact]
    public async Task User_CanRead_ButCannotWrite()
    {
        var user = _factory.CreateInternalClient().WithTenant("demo-a").WithRole("USER").WithUser("user-a");

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/business-workflows")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/business-workflows", Body("user_cannot_write"))).StatusCode);
    }

    // A2-2:plain PUT 經 /api/business-workflows/{name} 更新後,current_revision 的 bump
    // 必須同樣反映在 /api/skills 清單(兩條路由共用同一個 repo/revision 序列,不是各自維護的投影)。
    [Fact]
    public async Task Update_BumpsRevision_VisibleThroughSkillsList()
    {
        const string name = "business_flow_put_bump";
        Assert.Equal(
            HttpStatusCode.Created,
            (await Admin().PostAsJsonAsync("/api/business-workflows", Body(name))).StatusCode);

        var updated = await Admin().PutAsJsonAsync($"/api/business-workflows/{name}", Body(name));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await updated.ReadJsonAsync())["current_revision"]!.GetValue<int>());

        var rows = (await (await Admin().GetAsync("/api/skills")).ReadJsonAsync()).AsArray();
        Assert.Equal(
            2,
            rows.Single(row => row!["name"]!.GetValue<string>() == name)!["current_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Update_MissingNameStillRunsRequestTimeValidation_AndEngineFailureWinsAs502()
    {
        var body = Body("missing_business_flow");
        body["definition"] = body["definition"]!.GetValue<string>() + FakeSkillValidator.EngineDownMarker;

        var response = await Admin().PutAsJsonAsync("/api/business-workflows/missing_business_flow", body);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(502, (await response.ReadJsonAsync())["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Update_AgentSkillNameReturns404_AndCannotOverwriteItsKind()
    {
        const string name = "agent_update_guard";
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 4, 5, 6 };
        await repo.ImportAsync(
            "demo-a",
            new Skill(name, "agent", "name: agent", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);

        var response = await Admin().PutAsJsonAsync($"/api/business-workflows/{name}", Body(name));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("agentic", (await repo.GetAsync("demo-a", name, CancellationToken.None))!.Kind);
    }

    [Fact]
    public async Task Create_DisabledAgentSkillNameReturns409_WithoutChangingAuditSnapshot()
    {
        const string name = "disabled_agent_create_guard";
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 7, 8, 9 };
        await repo.ImportAsync(
            "demo-a",
            new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);
        Assert.True(await repo.DeleteAsync("demo-a", name, CancellationToken.None));
        var before = await repo.GetRevisionAsync("demo-a", name, 1, CancellationToken.None);

        var response = await Admin().PostAsJsonAsync("/api/business-workflows", Body(name));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var revisions = await repo.ListRevisionsAsync("demo-a", name, CancellationToken.None);
        Assert.Single(revisions);
        var after = await repo.GetRevisionAsync("demo-a", name, 1, CancellationToken.None);
        Assert.Equal("agentic", after!.Kind);
        Assert.Equal(before!.Package, after.Package);
        Assert.Equal(before.PackageSha256, after.PackageSha256);
        Assert.Null(await repo.GetAsync("demo-a", name, CancellationToken.None));
    }

    [Fact]
    public async Task SkillList_ReportsTrustedKindForFlowAndAgentSkillRows()
    {
        const string flowName = "kind_contract_flow";
        const string agentName = "kind-contract-agent";
        await Admin().PostAsJsonAsync("/api/business-workflows", Body(flowName));
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 10, 11 };
        await repo.ImportAsync(
            "demo-a",
            new Skill(agentName, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);

        var rows = (await (await Admin().GetAsync("/api/skills")).ReadJsonAsync()).AsArray();

        Assert.Equal("flow", rows.Single(row => row!["name"]!.GetValue<string>() == flowName)!["kind"]!.GetValue<string>());
        Assert.Equal("agentic", rows.Single(row => row!["name"]!.GetValue<string>() == agentName)!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task CompatibilitySkillPost_DoesNotReviveDisabledAgentSkill()
    {
        const string name = "compat-disabled-agent";
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 12, 13, 14 };
        await repo.ImportAsync(
            "demo-a",
            new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);
        Assert.True(await repo.DeleteAsync("demo-a", name, CancellationToken.None));

        var response = await Admin().PostAsJsonAsync("/api/skills", Body(name));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var revisions = await repo.ListRevisionsAsync("demo-a", name, CancellationToken.None);
        Assert.Single(revisions);
        var snapshot = await repo.GetRevisionAsync("demo-a", name, 1, CancellationToken.None);
        Assert.Equal("agentic", snapshot!.Kind);
        Assert.Equal(package, snapshot.Package);
    }

    [Fact]
    public async Task FlowDelete_RacingAgentImport_NeverDeletesResultingAgentSkill()
    {
        var repo = new InMemorySkillRepository();

        for (var index = 0; index < 100; index++)
        {
            var name = "delete-import-race-" + index;
            await repo.CreateAsync(
                "demo-a",
                new Skill(name, "flow", "name: " + name, "USER", true, 0, default, default, "flow"),
                "admin-a",
                CancellationToken.None);
            var package = new byte[] { 21, 22, (byte)index };

            using var start = new ManualResetEventSlim(false);
            var delete = Task.Run(async () =>
            {
                start.Wait();
                return await repo.DeleteAsync("demo-a", name, "flow", CancellationToken.None);
            });
            var import = Task.Run(async () =>
            {
                start.Wait();
                return await repo.ImportAsync(
                    "demo-a",
                    new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
                    package,
                    SkillHash.Sha256(package),
                    "admin-a",
                    CancellationToken.None);
            });
            start.Set();
            await Task.WhenAll(delete, import);

            var stored = await repo.GetAsync("demo-a", name, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal("agentic", stored.Kind);
            Assert.True(stored.Enabled);
        }
    }
}
