using System.Net;

namespace Platform.Web.Tests;

/// <summary>
/// GET /api/tools:需 JWT，透明轉送 Workflow Tool Registry 的安全 picker metadata。
/// </summary>
[Collection("EngineCalls")]
public sealed class ToolApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ToolApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task List_WithoutToken_Returns401_WithoutCallingWorkflow()
    {
        var before = FakeWorkflowService.EngineCalls.Count(c => c == "tools");

        var resp = await _factory.CreateClient().GetAsync("/api/tools");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(before, FakeWorkflowService.EngineCalls.Count(c => c == "tools"));
    }

    [Fact]
    public async Task List_WithToken_ReturnsSafeRegistryMetadata()
    {
        var resp = await _factory.UserClient().GetAsync("/api/tools");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var tool = (await resp.ReadJsonAsync()).AsArray().Single()!;
        Assert.Equal("backend.retrieval_search", tool["name"]!.GetValue<string>());
        Assert.Equal("http", tool["kind"]!.GetValue<string>());
        Assert.Equal("在目前租戶已授權的知識庫中進行向量檢索", tool["description"]!.GetValue<string>());
        Assert.Equal("read", tool["risk"]!.GetValue<string>());
        Assert.Equal("list[chunk]", tool["returns"]!.GetValue<string>());
        Assert.Null(tool["endpoint"]);
        Assert.Null(tool["token"]);
        Assert.Null(tool["implementation"]);
    }
}
