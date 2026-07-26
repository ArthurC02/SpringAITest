using System.Net;
using System.Net.Http.Json;
using static Platform.Web.Tests.ApiTestHelpers;

namespace Platform.Web.Tests;

/// <summary>
/// Agent Registry 代理端點(D1)。這一層驗:
/// (a) feature flag 關閉 → 整個 /api/agents* fail-closed 回 404,請求不得抵達代理(未登入亦然);
/// (b) flag 開啟後 —— 無 JWT → 401;整個 Builder API 非 ADMIN → 403(platform/backend 雙層把關);
/// (c) backend 的狀態碼、body 與 ETag/If-Match 原樣穿透(含 409/428/404)。
/// backend 代理的身分 header/If-Match 轉發細節由 AgentServiceTests 以 stub handler 驗(此處下游是 fake service)。
/// </summary>
[Collection("EngineCalls")]
public sealed class AgentApiTests : IDisposable
{
    private const string ExistingId = FakeAgentService.ExistingIdText;
    private const string GhostId = FakeAgentService.GhostIdText;
    private const string ExistingPath = "/api/agents/" + ExistingId;
    private readonly TestWebAppFactory _factory = new(agentBuilderEnabled: true);

    public void Dispose() => _factory.Dispose();

    // ---- (a) flag off:所有 /api/agents* → 404,且請求不得抵達代理 ----

    [Theory]
    [InlineData("GET", "/api/agents")]
    [InlineData("POST", "/api/agents")]
    [InlineData("GET", ExistingPath)]
    [InlineData("PUT", ExistingPath + "/draft")]
    [InlineData("DELETE", ExistingPath)]
    [InlineData("POST", ExistingPath + "/enable")]
    [InlineData("POST", ExistingPath + "/validate")]
    [InlineData("POST", ExistingPath + "/publish")]
    [InlineData("GET", ExistingPath + "/revisions")]
    [InlineData("POST", ExistingPath + "/revisions/1/restore")]
    public async Task Endpoints_FlagOff_Return404_AndNeverReachProxy(string method, string path)
    {
        using var flagOff = new TestWebAppFactory(agentBuilderEnabled: false);
        var before = FakeAgentService.Calls.Count;
        // 帶有效 ADMIN token 仍應 404(flag 關閉在認證之前 fail-closed,不洩漏端點存在)。
        var client = flagOff.CreateClient().WithToken(flagOff.IssueToken("admin-a", "ADMIN", "demo-a"));

        var resp = await client.SendAsync(Request(method, path, body: method is "POST" or "PUT" ? new { slug = "x" } : null));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        Assert.NotNull(body["timestamp"]);
        Assert.NotNull(body["fieldErrors"]);
        Assert.Equal(before, FakeAgentService.Calls.Count);
    }

    // ---- (b) flag on:無 JWT → 401 ----

    [Theory]
    [InlineData("GET", "/api/agents")]
    [InlineData("POST", "/api/agents")]
    [InlineData("GET", ExistingPath)]
    [InlineData("PUT", ExistingPath + "/draft")]
    [InlineData("DELETE", ExistingPath)]
    [InlineData("POST", ExistingPath + "/enable")]
    [InlineData("POST", ExistingPath + "/validate")]
    [InlineData("POST", ExistingPath + "/publish")]
    [InlineData("GET", ExistingPath + "/revisions")]
    [InlineData("POST", ExistingPath + "/revisions/1/restore")]
    public async Task Endpoints_FlagOn_Return401_WithoutToken(string method, string path)
    {
        var resp = await _factory.CreateClient().SendAsync(
            Request(method, path, body: method is "POST" or "PUT" ? new { slug = "x" } : null));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(401, (await resp.ReadJsonAsync())["status"]!.GetValue<int>());
    }

    // ---- (b) flag on:整個 Builder API 非 ADMIN → 403(detail 含敏感 authoring draft)----

    [Theory]
    [InlineData("GET", "/api/agents")]
    [InlineData("POST", "/api/agents")]
    [InlineData("GET", ExistingPath)]
    [InlineData("PUT", ExistingPath + "/draft")]
    [InlineData("DELETE", ExistingPath)]
    [InlineData("POST", ExistingPath + "/enable")]
    [InlineData("POST", ExistingPath + "/validate")]
    [InlineData("POST", ExistingPath + "/publish")]
    [InlineData("GET", ExistingPath + "/revisions")]
    [InlineData("POST", ExistingPath + "/revisions/1/restore")]
    public async Task BuilderEndpoints_User_Returns403_WithoutReachingProxy(string method, string path)
    {
        var user = _factory.CreateClient().WithToken(_factory.IssueToken("user-a", "USER", "demo-a"));
        var before = FakeAgentService.Calls.Count;

        var resp = await user.SendAsync(Request(
            method, path,
            ifMatch: method is "PUT" or "POST" ? FakeAgentService.CurrentETag : null,
            body: method is "POST" or "PUT" ? new { slug = "x" } : null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(403, (await resp.ReadJsonAsync())["status"]!.GetValue<int>());
        Assert.Equal(before, FakeAgentService.Calls.Count);
    }

    // ---- (c) 穿透:讀取、建立、ETag、If-Match/409/428/404 ----

    [Fact]
    public async Task List_Admin_Returns200_SnakeCaseBody()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/agents");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var item = (await resp.ReadJsonAsync()).AsArray().Single()!;
        Assert.Equal("researcher", item["slug"]!.GetValue<string>());
        Assert.Equal(1, item["draft_version"]!.GetValue<int>());
    }

    [Fact]
    public async Task Create_Admin_Returns201()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/agents", new { slug = "new-agent", name = "新代理" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("new-agent", (await resp.ReadJsonAsync())["slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_Admin_Returns200_WithEtagHeader()
    {
        var resp = await _factory.AdminClient().GetAsync(ExistingPath);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // GET 回傳目前 draft 版本的 ETag(供前端後續 PUT 帶 If-Match)。
        Assert.Equal(FakeAgentService.CurrentETag, resp.Headers.ETag!.ToString());
        Assert.Equal("researcher", (await resp.ReadJsonAsync())["slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_Admin_Ghost_Returns404_Passthrough()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/agents/" + GhostId);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.Equal("找不到 Agent：" + GhostId, body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateDraft_MatchingIfMatch_Returns200_WithNewEtag()
    {
        var resp = await _factory.AdminClient().SendAsync(Request(
            "PUT", ExistingPath + "/draft", ifMatch: FakeAgentService.CurrentETag, body: new { name = "改名" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("\"2\"", resp.Headers.ETag!.ToString());
        Assert.Equal(2, (await resp.ReadJsonAsync())["draft_version"]!.GetValue<int>());
    }

    [Fact]
    public async Task UpdateDraft_StaleIfMatch_Returns409_Passthrough()
    {
        var resp = await _factory.AdminClient().SendAsync(Request(
            "PUT", ExistingPath + "/draft", ifMatch: "\"999\"", body: new { name = "改名" }));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(409, body["status"]!.GetValue<int>());
        Assert.Equal("草稿版本衝突，請重新載入", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpdateDraft_MissingIfMatch_Returns428_Passthrough()
    {
        var resp = await _factory.AdminClient().SendAsync(Request(
            "PUT", ExistingPath + "/draft", body: new { name = "改名" }));

        Assert.Equal(HttpStatusCode.PreconditionRequired, resp.StatusCode);
        Assert.Equal(428, (await resp.ReadJsonAsync())["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Validate_MatchingIfMatch_Returns200()
    {
        var resp = await _factory.AdminClient().SendAsync(Request(
            "POST", ExistingPath + "/validate", ifMatch: FakeAgentService.CurrentETag));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await resp.ReadJsonAsync())["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Validate_MissingIfMatch_Returns428_Passthrough()
    {
        var resp = await _factory.AdminClient().PostAsync(ExistingPath + "/validate", content: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, resp.StatusCode);
        Assert.Equal(428, (await resp.ReadJsonAsync())["status"]!.GetValue<int>());
    }

    // validate 的「舊 If-Match → 409」與 UpdateDraft 走同一個 IfMatch 屬性與同一個 Write():
    // 409 的等價類由 UpdateDraft_StaleIfMatch_Returns409_Passthrough 覆蓋;此處只留 428
    // (AGENTS.md 明列 draft update 與 validate 兩個 action 都必須要求 If-Match)。

    [Fact]
    public async Task BearerRepeatedCapabilityClaims_AreParsedByMiddlewareIntoDownstreamUserContext()
    {
        FakeAgentService.LastContext = null;
        var token = _factory.IssueToken(
            "system-admin", "ADMIN", "demo-a",
            capabilities: new[] { "workflow.manage", "agent.author" });
        var client = _factory.CreateClient().WithToken(token);

        var resp = await client.GetAsync("/api/agents");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            new[] { "agent.author", "workflow.manage" },
            FakeAgentService.LastContext!.Capabilities);
    }

    [Fact]
    public async Task Enable_Admin_Returns200()
    {
        var resp = await _factory.AdminClient().PostAsync(ExistingPath + "/enable", content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await resp.ReadJsonAsync())["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task InvalidAgentId_DoesNotMatchProxyRoute()
    {
        var before = FakeAgentService.Calls.Count;

        var resp = await _factory.AdminClient().GetAsync("/api/agents/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(before, FakeAgentService.Calls.Count);
    }

    [Fact]
    public async Task Publish_Admin_ForwardsExpectedDraftVersion_AndReturnsAgentSnapshot()
    {
        var resp = await _factory.AdminClient().SendAsync(Request(
            "POST",
            ExistingPath + "/publish",
            ifMatch: FakeAgentService.CurrentETag,
            body: new { expected_draft_version = 1 }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(FakeAgentService.CurrentETag, resp.Headers.ETag!.ToString());
        Assert.Equal(1, (await resp.ReadJsonAsync())["published_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Publish_MissingExpectedDraftVersion_Returns400_Passthrough()
    {
        var resp = await _factory.AdminClient().SendAsync(Request(
            "POST",
            ExistingPath + "/publish",
            ifMatch: FakeAgentService.CurrentETag,
            body: new { }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(400, (await resp.ReadJsonAsync())["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Deactivate_Admin_Returns204()
    {
        var resp = await _factory.AdminClient().DeleteAsync(ExistingPath);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(string.Empty, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Revisions_Admin_ReturnsPublishedHistory()
    {
        var resp = await _factory.AdminClient().GetAsync(ExistingPath + "/revisions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var revision = (await resp.ReadJsonAsync()).AsArray().Single()!;
        Assert.Equal(1, revision["revision"]!.GetValue<int>());
        Assert.Equal("published", revision["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RestoreRevision_Admin_ReturnsNewPublishedRevision_WithEtag()
    {
        var resp = await _factory.AdminClient().PostAsync(
            ExistingPath + "/revisions/1/restore",
            content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(FakeAgentService.CurrentETag, resp.Headers.ETag!.ToString());
        Assert.Equal(2, (await resp.ReadJsonAsync())["published_revision"]!.GetValue<int>());
    }

    // 生命週期(validate → publish → revisions → restore → deactivate)的狀態機不變量無法在這一層驗:
    // FakeAgentService 是無狀態的,每個動作的回應與呼叫順序無關。真正的順序契約在 backend 的
    // AgentsApiTests / AgentRepositoryTests;此處保留的是每個 action 各自的穿透行為。

    [Theory]
    [InlineData("GET", "/api/agents/catalog/rule-facts")]
    [InlineData("GET", "/api/agents/catalog/rule-actions")]
    [InlineData("POST", "/api/agents/rules/validate")]
    [InlineData("POST", "/api/agents/rules/simulate")]
    public async Task RuleEndpoints_FlagOff_Return404BeforeAuthOrWorkflow(string method, string path)
    {
        using var flagOff = new TestWebAppFactory(agentBuilderEnabled: false);
        var calls = FakeWorkflowService.EngineCalls.Count;

        var response = await flagOff.CreateClient().SendAsync(Request(
            method,
            path,
            body: method == "POST"
                ? new { gate = "pre-action", ruleSet = new { version = 1, rules = Array.Empty<object>() }, facts = new { } }
                : null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(calls, FakeWorkflowService.EngineCalls.Count);
    }

    [Theory]
    [InlineData("GET", "/api/agents/catalog/rule-facts")]
    [InlineData("POST", "/api/agents/rules/validate")]
    public async Task RuleEndpoints_RequireAuthenticationAndAdmin(string method, string path)
    {
        var anonymous = await _factory.CreateClient().SendAsync(Request(
            method,
            path,
            body: method == "POST"
                ? new { gate = "pre-action", ruleSet = new { version = 1, rules = Array.Empty<object>() } }
                : null));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var calls = FakeWorkflowService.EngineCalls.Count;
        var user = _factory.CreateClient().WithToken(_factory.IssueToken("user-a", "USER", "demo-a"));
        var forbidden = await user.SendAsync(Request(
            method,
            path,
            body: method == "POST"
                ? new { gate = "pre-action", ruleSet = new { version = 1, rules = Array.Empty<object>() } }
                : null));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(calls, FakeWorkflowService.EngineCalls.Count);
    }

    [Fact]
    public async Task RuleCatalogs_Admin_ReturnRegistryMetadataOnly()
    {
        var client = _factory.AdminClient();

        var facts = await client.GetAsync("/api/agents/catalog/rule-facts");
        var actions = await client.GetAsync("/api/agents/catalog/rule-actions");

        Assert.Equal(HttpStatusCode.OK, facts.StatusCode);
        Assert.Equal(
            "action.amount",
            (await facts.ReadJsonAsync())["facts"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, actions.StatusCode);
        Assert.Equal(
            "deny",
            (await actions.ReadJsonAsync())["actions"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("demo-a", FakeWorkflowService.LastRuleContext!.TenantCode);
        Assert.Equal("admin-a", FakeWorkflowService.LastRuleContext.UserId);
        Assert.Equal("ADMIN", FakeWorkflowService.LastRuleContext.Role);
    }

    [Fact]
    public async Task RuleValidateAndSimulate_Admin_PassWorkflowBodiesThrough()
    {
        var client = _factory.AdminClient();
        var request = new
        {
            gate = "pre-action",
            ruleSet = new { version = 1, rules = Array.Empty<object>() },
            facts = new Dictionary<string, object> { ["action.amount"] = 9000 },
        };

        var validate = await client.PostAsJsonAsync("/api/agents/rules/validate", request);
        var simulate = await client.PostAsJsonAsync("/api/agents/rules/simulate", request);

        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.True((await validate.ReadJsonAsync())["valid"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, simulate.StatusCode);
        Assert.Equal(
            "allow",
            (await simulate.ReadJsonAsync())["simulation"]!["decision"]!.GetValue<string>());
        Assert.Contains("rule-validate:pre-action", FakeWorkflowService.EngineCalls);
        Assert.Contains("rule-simulate:pre-action", FakeWorkflowService.EngineCalls);
    }
}
