using System.Net;
using static Platform.Web.Tests.ApiTestHelpers;

namespace Platform.Web.Tests;

// G4:FakeWorkflowAdminService 是無狀態的 echo fake(見 Fakes.cs XML doc),沒有靜態呼叫紀錄或計數,
// 各測試只斷言「自己這次請求」回傳的 body/status/header,故按旗標值分兩組共用 host 是安全的。
public sealed class WorkflowAdminApiTests
    : IClassFixture<WorkflowAdminApiTests.DisabledFixture>, IClassFixture<WorkflowAdminApiTests.EnabledFixture>
{
    private readonly DisabledFixture _disabled;
    private readonly EnabledFixture _enabled;

    public WorkflowAdminApiTests(DisabledFixture disabled, EnabledFixture enabled)
    {
        _disabled = disabled;
        _enabled = enabled;
    }

    [Theory]
    [InlineData("/api/admin/workflows")]
    [InlineData("/api/admin/workflows/catalog/nodes")]
    [InlineData("/api/admin/orchestrators")]
    public async Task FlagOff_Returns404BeforeAuthentication(string path)
    {
        var response = await _disabled.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // 旗標關閉的 404 早於 authorization policy:連帶著 workflow.manage 的 ADMIN 也看不到端點存在。
    [Fact]
    public async Task FlagOff_Returns404_EvenWithExactCapability()
    {
        var client = _disabled.CreateClient().WithToken(_disabled.IssueToken(
            role: "ADMIN", capabilities: new[] { "workflow.manage" }));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/orchestrators")).StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/workflows")]
    [InlineData("/api/admin/orchestrators")]
    public async Task FlagOn_AnonymousReturns401(string path)
    {
        var response = await _enabled.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ADMIN 不隱含 workflow.manage —— 兩個 controller 各自掛 [Authorize(Policy="workflow.manage")],
    // 兩個 resource 都必須擋。
    [Theory]
    [InlineData("ADMIN", "/api/admin/workflows")]
    [InlineData("USER", "/api/admin/workflows")]
    [InlineData("ADMIN", "/api/admin/orchestrators")]
    public async Task RoleWithoutWorkflowManageReturns403(string role, string path)
    {
        var client = _enabled.CreateClient().WithToken(_enabled.IssueToken(role: role));

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExactCapabilityAllowsManagementAndForwardsEtag()
    {
        var client = _enabled.CreateClient().WithToken(_enabled.IssueToken(
            role: "USER",
            capabilities: new[] { "workflow.manage" }));
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var response = await client.SendAsync(Request(
            "PUT",
            $"/api/admin/workflows/{id:D}/draft",
            "\"6\"",
            new { definition = new { schemaVersion = 1 } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"7\"", response.Headers.ETag?.ToString());
        var body = await response.ReadJsonAsync();
        Assert.Equal("workflows", body["resource"]!.GetValue<string>());
        Assert.Equal("\"6\"", body["if_match"]!.GetValue<string>());
        Assert.Equal("user-a", body["user"]!.GetValue<string>());
    }

    // OrchestratorAdminController 是獨立的 controller,在 HTTP 層之前完全沒被走過:
    // 驗它送出的 resource 是 "orchestrators"(不是 workflows)、路徑後綴與 If-Match 都正確轉發。
    [Fact]
    public async Task OrchestratorAdmin_ExactCapability_ForwardsResourceAndIfMatch()
    {
        var client = ManagerClient(_enabled);
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var response = await client.SendAsync(Request(
            "PUT",
            $"/api/admin/orchestrators/{id:D}/draft",
            "\"6\"",
            new { definition = new { schemaVersion = 1 } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"7\"", response.Headers.ETag?.ToString());
        var body = await response.ReadJsonAsync();
        Assert.Equal("orchestrators", body["resource"]!.GetValue<string>());
        Assert.Equal("draft", body["suffix"]!.GetValue<string>());
        Assert.Equal("PUT", body["method"]!.GetValue<string>());
        Assert.Equal(id.ToString("D"), body["id"]!.GetValue<string>());
        Assert.Equal("\"6\"", body["if_match"]!.GetValue<string>());
    }

    // 兩個 resource 共用的動作宣告(含 [HttpXxx])住在 WorkflowAdminControllerBase,OrchestratorAdminController
    // 自己一個 action 都沒有 —— 這幾條路由若沒被繼承下來會變 404。其餘 5 種形狀(List/draft/revisions/
    // enable/delete)已由本檔其他案例走過,兩者合起來即 10 條共用路由的完整面。
    [Theory]
    [InlineData("POST", "", "")]
    [InlineData("GET", "/11111111-1111-1111-1111-111111111111", "")]
    [InlineData("POST", "/11111111-1111-1111-1111-111111111111/validate", "validate")]
    [InlineData("POST", "/11111111-1111-1111-1111-111111111111/publish", "publish")]
    [InlineData("POST", "/11111111-1111-1111-1111-111111111111/revisions/2/restore", "revisions/2/restore")]
    public async Task SharedActions_AreInheritedByBothResources(
        string method, string relativePath, string expectedSuffix)
    {
        var client = ManagerClient(_enabled);

        foreach (var resource in new[] { "workflows", "orchestrators" })
        {
            var response = await client.SendAsync(Request(
                method, $"/api/admin/{resource}{relativePath}", body: new { definition = new { } }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.ReadJsonAsync();
            Assert.Equal(resource, body["resource"]!.GetValue<string>());
            Assert.Equal(
                expectedSuffix.Length == 0 ? null : expectedSuffix,
                body["suffix"]?.GetValue<string>());
        }
    }

    // forwardIfMatch:false 的路由(List/Get/revisions/restore/enable/disable)即使 client 帶 If-Match
    // 也不得往下轉發 —— 否則 backend 會對一個非變更請求做前置條件檢查。
    [Theory]
    [InlineData("GET", "/api/admin/workflows")]
    [InlineData("GET", "/api/admin/workflows/11111111-1111-1111-1111-111111111111/revisions")]
    [InlineData("POST", "/api/admin/orchestrators/11111111-1111-1111-1111-111111111111/enable")]
    [InlineData("DELETE", "/api/admin/orchestrators/11111111-1111-1111-1111-111111111111")]
    public async Task NonMutatingRoutes_DoNotForwardIfMatch(string method, string path)
    {
        var client = ManagerClient(_enabled);

        var response = await client.SendAsync(Request(method, path, "\"6\""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadJsonAsync())["if_match"]);
    }

    private static HttpClient ManagerClient(TestWebAppFactory factory)
        => factory.CreateClient().WithToken(factory.IssueToken(
            role: "USER", capabilities: new[] { "workflow.manage" }));

    public sealed class DisabledFixture : TestWebAppFactory
    {
        public DisabledFixture() : base(new() { ["WORKFLOW_DESIGNER_ENABLED"] = "false" })
        {
        }
    }

    public sealed class EnabledFixture : TestWebAppFactory
    {
        public EnabledFixture() : base(new() { ["WORKFLOW_DESIGNER_ENABLED"] = "true" })
        {
        }
    }
}
