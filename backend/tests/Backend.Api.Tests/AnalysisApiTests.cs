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
        // Take(5) 邊界 + OrderByDescending(CreatedAt):必須是「最新」5 筆且由新到舊,
        // 只驗筆數的話,誤改成 OrderBy(取最舊 5 筆)或漏排序都測不出來。
        Assert.Equal(
            new[] { "文件5", "文件4", "文件3", "文件2", "文件1" },
            body["latest_titles"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    // Take(5) 的 on-point 邊界:恰 5 份時五筆都要回、一筆都不能被截掉(n=6 測的是超出後截斷那半邊)。
    [Fact]
    public async Task Summary_ExactlyFiveDocuments_ReturnsAllFiveNewestFirst()
    {
        for (var i = 0; i < 5; i++)
        {
            await _factory.SeedDocumentAsync("analysis-five", "五之" + i, "內容。");
        }
        var client = _factory.CreateInternalClient().WithTenant("analysis-five");

        var body = await (await client.GetAsync("/api/analysis/summary")).ReadJsonAsync();

        Assert.Equal(5, body["document_count"]!.GetValue<int>());
        Assert.Equal(5, body["chunk_count"]!.GetValue<int>());
        Assert.Equal(
            new[] { "五之4", "五之3", "五之2", "五之1", "五之0" },
            body["latest_titles"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    // 下界邊界:0 份文件的租戶。Dapper 端靠 coalesce(sum(chunk_count), 0)、記憶體端靠空序列 Sum(),
    // 兩邊都必須回 200 + 0/0/[],而不是 500 或 null。
    [Fact]
    public async Task Summary_NoDocuments_ReturnsZeroCountsAndEmptyTitles()
    {
        var client = _factory.CreateInternalClient().WithTenant("analysis-empty");

        var resp = await client.GetAsync("/api/analysis/summary");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(0, body["document_count"]!.GetValue<int>());
        Assert.Equal(0, body["chunk_count"]!.GetValue<int>());
        Assert.Empty(body["latest_titles"]!.AsArray());
    }

    // 六個端點裡 Analysis 是唯一沒有跨租戶測試的:聚合查詢漏掉 tenant 條件會把別家文件數/切塊數/標題算進來。
    [Fact]
    public async Task Summary_IsTenantScoped_ExcludesOtherTenantDocuments()
    {
        await _factory.SeedDocumentAsync("analysis-x", "X 的文件", "內容。");
        await _factory.SeedDocumentAsync("analysis-y", "Y 的文件一", "內容。\n\n第二段。");
        await _factory.SeedDocumentAsync("analysis-y", "Y 的文件二", "內容。");

        var x = await (await _factory.CreateInternalClient().WithTenant("analysis-x")
            .GetAsync("/api/analysis/summary")).ReadJsonAsync();
        var y = await (await _factory.CreateInternalClient().WithTenant("analysis-y")
            .GetAsync("/api/analysis/summary")).ReadJsonAsync();

        Assert.Equal(1, x["document_count"]!.GetValue<int>());
        Assert.Equal(1, x["chunk_count"]!.GetValue<int>());
        Assert.Equal(new[] { "X 的文件" }, x["latest_titles"]!.AsArray().Select(t => t!.GetValue<string>()));

        Assert.Equal(2, y["document_count"]!.GetValue<int>());
        Assert.Equal(3, y["chunk_count"]!.GetValue<int>());
        Assert.DoesNotContain("X 的文件", y["latest_titles"]!.AsArray().Select(t => t!.GetValue<string>()));
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
