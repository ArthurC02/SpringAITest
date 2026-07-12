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
}
