using System.Net;

namespace Platform.Web.Tests;

public sealed class AnalysisApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public AnalysisApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Summary_Returns401_WithoutToken()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/analysis/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Summary_Returns200_WithSnakeCaseBody()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await client.GetAsync("/api/analysis/summary");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(2, body["document_count"]!.GetValue<int>());
        Assert.Equal(7, body["chunk_count"]!.GetValue<int>());
        Assert.Equal(2, body["latest_titles"]!.AsArray().Count);
    }
}
