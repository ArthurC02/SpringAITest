using System.Net;
using System.Net.Http.Json;

namespace Backend.Api.Tests;

/// <summary>檢索 top_k 範圍驗證(1~50):缺省退回 4(200);邊界 1/50 放行(200),0、負值、51 → 400 fieldErrors。</summary>
public sealed class RetrievalApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public RetrievalApiTests(TestWebAppFactory factory) => _factory = factory;

    // 邊界值分析(off-point):負值進真 SQL LIMIT 會 500、0 回空結果(無意義)、51 超出上界
    // → 一律以 Range 驗證擋成 400。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public async Task Search_InvalidTopK_Returns400_WithFieldError(int topK)
    {
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var resp = await client.PostAsJsonAsync("/api/retrieval/search", new { query = "重點", top_k = topK });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var fieldErrors = (await resp.ReadJsonAsync())["fieldErrors"]!.AsObject();
        Assert.Contains(fieldErrors, kv => kv.Value!.GetValue<string>() == "top_k 必須介於 1 到 50 之間");
    }

    // 邊界值分析(on-point):1 與 50 恰在合法區間內,必須放行 — 確保 Range 沒把邊界寫成 exclusive。
    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task Search_BoundaryTopK_Returns200(int topK)
    {
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var resp = await client.PostAsJsonAsync("/api/retrieval/search", new { query = "重點", top_k = topK });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull((await resp.ReadJsonAsync())["chunks"]);
    }

    [Fact]
    public async Task Search_OmittedTopK_Returns200_UsesDefault()
    {
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var resp = await client.PostAsJsonAsync("/api/retrieval/search", new { query = "重點" });

        // 缺省 top_k → controller ?? 4,正常放行。
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull((await resp.ReadJsonAsync())["chunks"]);
    }
}
