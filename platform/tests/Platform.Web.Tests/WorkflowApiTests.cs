using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class WorkflowApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public WorkflowApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient() => _factory.CreateClient().WithToken(_factory.IssueToken());

    [Fact]
    public async Task List_Returns200_WithWorkflows()
    {
        var resp = await AuthedClient().GetAsync("/api/workflows");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        var arr = body.AsArray();
        Assert.Single(arr);
        Assert.Equal("rag_qa", arr[0]!["name"]!.GetValue<string>());
        // JSON key 是 snake_case required_role。
        Assert.Equal("USER", arr[0]!["required_role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_Returns200_WithResult()
    {
        var resp = await AuthedClient().PostAsJsonAsync("/api/workflows/rag_qa",
            new { input = new { q = "hi" } });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("rag_qa", body["workflow"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_Returns404_WhenWorkflowMissing()
    {
        var resp = await AuthedClient().PostAsJsonAsync("/api/workflows/ghost",
            new { input = new { q = "hi" } });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("找不到工作流：ghost", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_Returns400_WhenInputMissing()
    {
        var resp = await AuthedClient().PostAsJsonAsync("/api/workflows/rag_qa", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("input 不可為空", body["fieldErrors"]!["input"]!.GetValue<string>());
    }
}
