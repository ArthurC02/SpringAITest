using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Files;

namespace Backend.Api.Tests;

/// <summary>假嵌入必須確定性、維度 1536。</summary>
public sealed class EmbeddingTests
{
    private static readonly FakeEmbeddingProvider Provider = new(1536);

    [Fact]
    public async Task Fake_SameText_ProducesIdenticalVector()
    {
        var a = await Provider.EmbedQueryAsync("台北天氣", CancellationToken.None);
        var b = await Provider.EmbedQueryAsync("台北天氣", CancellationToken.None);

        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Fake_HasDimension1536()
    {
        var v = await Provider.EmbedQueryAsync("anything", CancellationToken.None);

        Assert.Equal(1536, v.Length);
    }

    [Fact]
    public async Task Fake_DifferentText_ProducesDifferentVector()
    {
        var a = await Provider.EmbedQueryAsync("台北", CancellationToken.None);
        var b = await Provider.EmbedQueryAsync("高雄", CancellationToken.None);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Fake_EmbedDocuments_MatchesEmbedQuery_PerText()
    {
        var docs = await Provider.EmbedDocumentsAsync(new[] { "片段一", "片段二" }, CancellationToken.None);
        var single = await Provider.EmbedQueryAsync("片段一", CancellationToken.None);

        Assert.Equal(2, docs.Count);
        Assert.Equal(single, docs[0]);
    }

    // OpenAI/LiteLLM 允許 data[] 亂序回傳,只保證每筆帶 index。若不依 index 還原,
    // chunk 與向量會整批錯位(RAG 內容全對錯位置),而且不會有任何例外 —— 靜默迴歸。
    [Fact]
    public async Task OpenAi_EmbedDocuments_RestoresOrderByIndex_WhenResponseIsOutOfOrder()
    {
        var handler = new StubEmbeddingHandler(
            """
            {"data":[
              {"index":2,"embedding":[2.0,2.0]},
              {"index":0,"embedding":[0.0,0.0]},
              {"index":1,"embedding":[1.0,1.0]}
            ]}
            """);
        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler), "http://litellm:4000/v1/", "sk-1234", "text-embedding-3-small");

        var vectors = await provider.EmbedDocumentsAsync(new[] { "零", "一", "二" }, CancellationToken.None);

        Assert.Equal(new[] { 0f, 1f, 2f }, vectors.Select(v => v[0]));
        // 請求本身也要照原始順序送出,否則 index 還原沒有意義。
        Assert.Equal(
            new[] { "零", "一", "二" },
            handler.LastRequest!["input"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("text-embedding-3-small", handler.LastRequest["model"]!.GetValue<string>());
        Assert.Equal("http://litellm:4000/v1/embeddings", handler.LastUri!.ToString());
    }

    [Fact]
    public async Task OpenAi_EmbedQuery_ReturnsFirstVector()
    {
        var handler = new StubEmbeddingHandler("""{"data":[{"index":0,"embedding":[7.0,8.0]}]}""");
        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler), "http://litellm:4000/v1", "sk-1234", "text-embedding-3-small");

        var vector = await provider.EmbedQueryAsync("查詢", CancellationToken.None);

        Assert.Equal(new[] { 7f, 8f }, vector);
    }

    /// <summary>手寫 HttpMessageHandler:回固定 JSON 並記下送出的請求(不連真 LiteLLM)。</summary>
    private sealed class StubEmbeddingHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubEmbeddingHandler(string json) => _json = json;

        public JsonNode? LastRequest { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastRequest = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(JsonNode.Parse(_json)),
            };
        }
    }
}
