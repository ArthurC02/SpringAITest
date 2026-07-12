using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class DocumentApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public DocumentApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient AuthedClient() => _factory.CreateClient().WithToken(_factory.IssueToken());

    [Fact]
    public async Task Create_Returns202_WithStatusProcessing()
    {
        var resp = await AuthedClient().PostAsJsonAsync("/api/documents",
            new { title = "標題", text = "內容" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("doc-1", body["id"]!.GetValue<string>());
        Assert.Equal("標題", body["title"]!.GetValue<string>());
        Assert.Equal("processing", body["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_Returns400_WhenTitleMissing()
    {
        var resp = await AuthedClient().PostAsJsonAsync("/api/documents", new { text = "內容" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("title 不可為空", body["fieldErrors"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_Returns200()
    {
        var resp = await AuthedClient().GetAsync("/api/documents");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        var arr = body.AsArray();
        Assert.Single(arr);
        Assert.Equal(3, arr[0]!["chunk_count"]!.GetValue<int>());
        Assert.Equal("2026-07-11T00:00:00Z", arr[0]!["created_at"]!.GetValue<string>());
        Assert.Equal("ready", arr[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_Returns204()
    {
        var resp = await AuthedClient().DeleteAsync("/api/documents/doc-1");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns404_WhenMissing()
    {
        var resp = await AuthedClient().DeleteAsync("/api/documents/ghost");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("找不到文件：ghost", body["message"]!.GetValue<string>());
    }
}
