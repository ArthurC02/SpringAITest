using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class DocumentApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public DocumentApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_Returns202_WithStatusProcessing()
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
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
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents", new { text = "內容" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("title 不可為空", body["fieldErrors"]!["title"]!.GetValue<string>());
    }

    // StringLength 邊界:on-point(上限剛好)受理,off-point(超一)回 400 fieldErrors。
    [Theory]
    [InlineData(500, 10)]         // title 上限剛好
    [InlineData(10, 1_000_000)]   // text 上限剛好
    public async Task Create_AtLengthLimit_Accepted(int titleLen, int textLen)
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
            new { title = new string('a', titleLen), text = new string('b', textLen) });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Theory]
    [InlineData(501, 10, "title", "title 長度不可超過 500 字")]
    [InlineData(10, 1_000_001, "text", "text 長度不可超過 1000000 字")]
    public async Task Create_OverLengthLimit_Returns400_WithFieldError(
        int titleLen, int textLen, string field, string message)
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
            new { title = new string('a', titleLen), text = new string('b', textLen) });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(message, body["fieldErrors"]![field]!.GetValue<string>());
    }

    [Fact]
    public async Task List_Returns200()
    {
        var resp = await _factory.UserClient().GetAsync("/api/documents");

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
        var resp = await _factory.UserClient().DeleteAsync("/api/documents/doc-1");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns404_WhenMissing()
    {
        var resp = await _factory.UserClient().DeleteAsync("/api/documents/ghost");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("找不到文件：ghost", body["message"]!.GetValue<string>());
    }
}
