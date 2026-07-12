using System.Net;

namespace Backend.Api.Tests;

/// <summary>分析摘要:document_count/chunk_count 正確、latest_titles 只取最近 5 筆、欄位 snake_case;缺 X-Tenant-Id 400。</summary>
public sealed class AnalysisApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public AnalysisApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Summary_SixDocuments_CountsAndLatestFiveTitles()
    {
        for (var i = 0; i < 6; i++)
        {
            // 每份單段內文 → 恰 1 塊,方便驗 chunk_count。
            await _factory.SeedDocumentAsync("demo-a", "文件" + i, "內容。");
        }
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var body = await (await client.GetAsync("/api/analysis/summary")).ReadJsonAsync();

        // 欄位 snake_case(document_count / chunk_count / latest_titles)。
        Assert.Equal(6, body["document_count"]!.GetValue<int>());
        Assert.Equal(6, body["chunk_count"]!.GetValue<int>());
        // Take(5) 邊界:6 份文件 → latest_titles 只回最近 5 筆。
        Assert.Equal(5, body["latest_titles"]!.AsArray().Count);
    }

    [Fact]
    public async Task Summary_MissingTenantHeader_Returns400()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.GetAsync("/api/analysis/summary");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }
}
