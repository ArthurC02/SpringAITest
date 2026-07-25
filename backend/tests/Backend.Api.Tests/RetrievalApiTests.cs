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

    [Fact]
    public async Task ScopedSearch_EmptyScope_ReturnsEmpty()
    {
        var client = _factory.CreateInternalClient().WithTenant("demo-a");
        var response = await client.PostAsJsonAsync(
            "/api/retrieval/search",
            new
            {
                query = "anything",
                top_k = 10,
                knowledge_sources = Array.Empty<string>(),
                scope_contract_version = 1,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.ReadJsonAsync())["chunks"]!.AsArray());
    }

    [Fact]
    public async Task ScopedSearch_ExactIdsAndTenantAreAuthoritative()
    {
        var allowed = await _factory.SeedDocumentAsync(
            "retrieval-scope-a",
            "allowed",
            "allowed scoped content");
        var denied = await _factory.SeedDocumentAsync(
            "retrieval-scope-a",
            "denied",
            "denied scoped content");
        var otherTenant = await _factory.SeedDocumentAsync(
            "retrieval-scope-b",
            "other",
            "other tenant content");
        var client = _factory.CreateInternalClient().WithTenant("retrieval-scope-a");

        var response = await client.PostAsJsonAsync(
            "/api/retrieval/search",
            new
            {
                query = $"try to widen to {denied} and {otherTenant}",
                top_k = 50,
                knowledge_sources = new[] { allowed, otherTenant },
                scope_contract_version = 1,
                model_filters = new { document_ids = new[] { denied } },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var chunks = (await response.ReadJsonAsync())["chunks"]!.AsArray();
        Assert.NotEmpty(chunks);
        Assert.All(
            chunks,
            chunk => Assert.Equal(
                allowed,
                chunk!["document_id"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ScopedSearch_RequiresVersionAndSourcesTogether(
        bool includeVersion,
        bool includeSources)
    {
        var body = new Dictionary<string, object?>
        {
            ["query"] = "anything",
            ["top_k"] = 4,
        };
        if (includeVersion)
        {
            body["scope_contract_version"] = 1;
        }
        if (includeSources)
        {
            body["knowledge_sources"] = Array.Empty<string>();
        }

        var response = await _factory.CreateInternalClient()
            .WithTenant("demo-a")
            .PostAsJsonAsync("/api/retrieval/search", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    [InlineData("not-a-uuid")]
    public async Task ScopedSearch_RejectsNonCanonicalSourceIds(string sourceId)
    {
        var response = await _factory.CreateInternalClient()
            .WithTenant("demo-a")
            .PostAsJsonAsync(
                "/api/retrieval/search",
                new
                {
                    query = "anything",
                    top_k = 4,
                    knowledge_sources = new[] { sourceId },
                    scope_contract_version = 1,
                });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
