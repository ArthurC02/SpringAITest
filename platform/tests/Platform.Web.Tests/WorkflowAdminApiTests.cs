using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class WorkflowAdminApiTests
{
    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string? ifMatch = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    [Theory]
    [InlineData("/api/admin/workflows")]
    [InlineData("/api/admin/workflows/catalog/nodes")]
    [InlineData("/api/admin/orchestrators")]
    public async Task FlagOff_Returns404BeforeAuthentication(string path)
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: false);

        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // 旗標關閉的 404 早於 authorization policy:連帶著 workflow.manage 的 ADMIN 也看不到端點存在。
    [Fact]
    public async Task FlagOff_Returns404_EvenWithExactCapability()
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: false);
        var client = factory.CreateClient().WithToken(factory.IssueToken(
            role: "ADMIN", capabilities: new[] { "workflow.manage" }));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/orchestrators")).StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/workflows")]
    [InlineData("/api/admin/orchestrators")]
    public async Task FlagOn_AnonymousReturns401(string path)
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);

        var response = await factory.CreateClient().GetAsync(path);

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
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken(role: role));

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExactCapabilityAllowsManagementAndForwardsEtag()
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken(
            role: "USER",
            capabilities: new[] { "workflow.manage" }));
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var response = await client.SendAsync(Request(
            HttpMethod.Put,
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
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);
        var client = ManagerClient(factory);
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var response = await client.SendAsync(Request(
            HttpMethod.Put,
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

    // forwardIfMatch:false 的路由(List/Get/revisions/restore/enable/disable)即使 client 帶 If-Match
    // 也不得往下轉發 —— 否則 backend 會對一個非變更請求做前置條件檢查。
    [Theory]
    [InlineData("GET", "/api/admin/workflows")]
    [InlineData("GET", "/api/admin/workflows/11111111-1111-1111-1111-111111111111/revisions")]
    [InlineData("POST", "/api/admin/orchestrators/11111111-1111-1111-1111-111111111111/enable")]
    [InlineData("DELETE", "/api/admin/orchestrators/11111111-1111-1111-1111-111111111111")]
    public async Task NonMutatingRoutes_DoNotForwardIfMatch(string method, string path)
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);
        var client = ManagerClient(factory);

        var response = await client.SendAsync(Request(new HttpMethod(method), path, "\"6\""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.ReadJsonAsync())["if_match"]);
    }

    private static HttpClient ManagerClient(TestWebAppFactory factory)
        => factory.CreateClient().WithToken(factory.IssueToken(
            role: "USER", capabilities: new[] { "workflow.manage" }));
}
