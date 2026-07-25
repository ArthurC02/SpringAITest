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

    [Fact]
    public async Task FlagOn_AnonymousReturns401()
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);

        var response = await factory.CreateClient().GetAsync("/api/admin/workflows");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("USER")]
    public async Task RoleWithoutWorkflowManageReturns403(string role)
    {
        using var factory = new TestWebAppFactory(workflowDesignerEnabled: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken(role: role));

        var response = await client.GetAsync("/api/admin/workflows");

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
}
