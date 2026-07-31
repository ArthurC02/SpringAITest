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

    // 同名且**仍啟用**的 flow → 409「已存在」(kind fence 的 CreateAsync 回 null),既有那筆不得被覆寫。
    // 與 Create_DisabledAgentSkillNameReturns409 不同分支:那條是 kind 不符,這條是純粹的 flow-vs-flow 撞名。
    [Fact]
    public async Task Create_DuplicateFlowName_Returns409_AndKeepsExisting()
    {
        const string name = "business_flow_duplicate";
        var client = Admin();
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/business-workflows", Body(name))).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/business-workflows", Body(name));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var error = await duplicate.ReadJsonAsync();
        Assert.Equal(409, error["status"]!.GetValue<int>());
        Assert.Equal("Business Workflow 名稱已存在：" + name, error["message"]!.GetValue<string>());

        var current = await (await client.GetAsync($"/api/business-workflows/{name}")).ReadJsonAsync();
        Assert.Equal(1, current["current_revision"]!.GetValue<int>());
    }

    // 決策表另一半:502(引擎不可達)已由 Update_MissingName... 覆蓋,這裡補「引擎判定定義不合法」→ 422。
    // BusinessWorkflowController 自己組 fieldErrors(不是共用 SkillController 的),錯誤碼與行號都得對得上,且零寫入。
    [Fact]
    public async Task Create_EngineRejects_Returns422_WithEngineCodes_AndWritesNothing()
    {
        const string name = "business_flow_invalid";
        var body = Body(name);
        body["definition"] = body["definition"]!.GetValue<string>() + FakeSkillValidator.InvalidMarker;

        var response = await Admin().PostAsJsonAsync("/api/business-workflows", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.ReadJsonAsync();
        Assert.Equal(422, error["status"]!.GetValue<int>());
        Assert.Equal("Business Workflow 定義驗證失敗", error["message"]!.GetValue<string>());
        var fieldErrors = error["fieldErrors"]!.AsObject();
        Assert.Equal("loop 缺少 max_iterations（第 7 行）", fieldErrors["unbounded_loop"]!.GetValue<string>());
        Assert.Equal("節點不存在：no_such_node", fieldErrors["unknown_node"]!.GetValue<string>());

        Assert.Equal(
            HttpStatusCode.NotFound, (await Admin().GetAsync($"/api/business-workflows/{name}")).StatusCode);
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

    // 補完 role x method 組合:POST 之外的兩個寫入動詞同屬「非 ADMIN 寫入」等價類,
    // 三個動詞都掛了 [AdminOnly],少測任一個就等於那個動詞的閘門沒有測試背書。
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task User_Write_Returns403(string method)
    {
        var user = _factory.CreateInternalClient().WithTenant("demo-a").WithRole("USER").WithUser("user-a");
        var request = new HttpRequestMessage(new HttpMethod(method), "/api/business-workflows/user_write_guard");
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(Body("user_write_guard"));
        }

        var response = await user.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.ReadJsonAsync();
        Assert.Equal(403, error["status"]!.GetValue<int>());
        Assert.Equal("權限不足，無法存取 Business Workflow", error["message"]!.GetValue<string>());
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

    // DELETE 的正常等價類:204 軟刪 → 清單/單筆都看不到,但稽核 revision 一列都沒少;
    // 再刪一次(已停用)與「名字從未存在」是同一分支 → 404 + Business Workflow 專屬訊息。
    [Fact]
    public async Task Delete_SoftDeletes_HidesFromReads_ButKeepsRevisions()
    {
        const string name = "business_flow_delete";
        var client = Admin();
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/business-workflows", Body(name))).StatusCode);

        var deleted = await client.DeleteAsync($"/api/business-workflows/{name}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await client.GetAsync($"/api/business-workflows/{name}")).StatusCode);
        Assert.DoesNotContain(
            (await (await client.GetAsync("/api/business-workflows")).ReadJsonAsync()).AsArray(),
            row => row!["name"]!.GetValue<string>() == name);
        Assert.Single((await (await client.GetAsync($"/api/skills/{name}/revisions")).ReadJsonAsync()).AsArray());

        var again = await client.DeleteAsync($"/api/business-workflows/{name}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(
            "找不到 Business Workflow：" + name, (await again.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // PUT 的姊妹案:kind fence 對 DELETE 一樣要成立 —— Agent Skill 的名字不得被 flow 路由停用。
    [Fact]
    public async Task Delete_AgentSkillNameReturns404_AndLeavesItEnabled()
    {
        const string name = "agent_delete_guard";
        var repo = _factory.Fake<ISkillRepository>();
        var package = new byte[] { 15, 16, 17 };
        await repo.ImportAsync(
            "demo-a",
            new Skill(name, "agent", "kind: agentic", "USER", true, 0, default, default, "agentic"),
            package,
            SkillHash.Sha256(package),
            "admin-a",
            CancellationToken.None);

        var response = await Admin().DeleteAsync($"/api/business-workflows/{name}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stored = await repo.GetAsync("demo-a", name, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("agentic", stored.Kind);
        Assert.True(stored.Enabled);
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
