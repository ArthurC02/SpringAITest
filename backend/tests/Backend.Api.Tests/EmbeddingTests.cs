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

    // 維度是建構子參數,只測過預設 1536;0(退化)與 1(最小非退化)是邊界:
    // 目前建構子不驗證 dim,0 會安靜回傳空向量(寫死現況,將來若改成拒絕會被這裡擋下)。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Fake_HonoursConfiguredDimension_AtBoundary(int dim)
    {
        var provider = new FakeEmbeddingProvider(dim);

        var v = await provider.EmbedQueryAsync("邊界", CancellationToken.None);

        Assert.Equal(dim, v.Length);
    }

    [Fact]
    public async Task Fake_EmbedDocuments_EmptyInput_ReturnsEmptyList()
    {
        var docs = await Provider.EmbedDocumentsAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(docs);
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

    [Fact]
    public async Task OpenAi_EmbedDocuments_EmptyInput_SendsEmptyArray_AndReturnsEmptyList()
    {
        var handler = new StubEmbeddingHandler("""{"data":[]}""");
        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler), "http://litellm:4000/v1/", "sk-1234", "text-embedding-3-small");

        var vectors = await provider.EmbedDocumentsAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(vectors);
        Assert.Empty(handler.LastRequest!["input"]!.AsArray());
    }

    // 閘道回 200 但內容是 JSON null(例如 LiteLLM 中繼把 body 吃掉):必須是可讀的中文錯誤,
    // 而不是後面 payload.Data 的 NullReference。
    [Fact]
    public async Task OpenAi_EmbedDocuments_ThrowsWhenBodyIsJsonNull()
    {
        var handler = new StubEmbeddingHandler("null");
        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler), "http://litellm:4000/v1/", "sk-1234", "text-embedding-3-small");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedDocumentsAsync(new[] { "片段" }, CancellationToken.None));

        Assert.Equal("嵌入服務回應為空", error.Message);
    }

    // 非 2xx(429/500 同一等價類)必須原樣拋 HttpRequestException —— DocumentProcessor 靠這個
    // 例外型別把嵌入失敗歸類成 transient 並重試,吞掉或換型別都會讓重試失效。
    [Fact]
    public async Task OpenAi_EmbedDocuments_PropagatesGatewayErrorStatus()
    {
        var handler = new StubEmbeddingHandler("""{"error":"boom"}""", HttpStatusCode.InternalServerError);
        var provider = new OpenAiEmbeddingProvider(
            new HttpClient(handler), "http://litellm:4000/v1/", "sk-1234", "text-embedding-3-small");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.EmbedDocumentsAsync(new[] { "片段" }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    /// <summary>手寫 HttpMessageHandler:回固定 JSON 並記下送出的請求(不連真 LiteLLM)。</summary>
    private sealed class StubEmbeddingHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;

        public StubEmbeddingHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }

        public JsonNode? LastRequest { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastRequest = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(_status)
            {
                Content = JsonContent.Create(JsonNode.Parse(_json)),
            };
        }
    }
}
