using Backend.Api.Files;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

/// <summary>DocumentProcessor 以 fake repo + fake embeddings 驗 happy / failed 路徑(不連 broker、不連 DB)。</summary>
public sealed class DocumentProcessorTests
{
    private static DocumentMessage Message(string id, string text = "第一段。\n\n第二段。")
        => new(id, "demo-a", "user-a", "手冊", text);

    [Fact]
    public async Task Process_Happy_MarksReady_WithChunkCount()
    {
        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(repo, new FakeEmbeddingProvider(8), NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        await processor.ProcessAsync(Message(id), CancellationToken.None);

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal(id, doc.Id);
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount); // 兩個以空白行分隔的段落。
    }

    [Fact]
    public async Task Process_EmbeddingThrows_MarksFailed_DoesNotThrow()
    {
        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(repo, new ThrowingEmbeddingProvider(), NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        // 不得拋出(訊息由消費者 ack,不重試)。
        await processor.ProcessAsync(Message(id), CancellationToken.None);

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("failed", doc.Status);
    }

    [Fact]
    public async Task Process_RedeliveryOfReady_Skips_NoRework()
    {
        var repo = new FakeRagRepository();
        var embeddings = new CountingEmbeddingProvider();
        var processor = new DocumentProcessor(repo, embeddings, NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        await processor.ProcessAsync(Message(id), CancellationToken.None); // 首投 → ready
        await processor.ProcessAsync(Message(id), CancellationToken.None); // 重投 → 應跳過

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount);
        Assert.Equal(1, embeddings.DocumentCalls); // 只嵌入一次,重投未重跑。
    }
}

/// <summary>嵌入時拋例外,用來驗 DocumentProcessor 的 failed 路徑。</summary>
public sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
{
    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => throw new InvalidOperationException("嵌入服務不可達");

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
        => throw new InvalidOperationException("嵌入服務不可達");
}

/// <summary>計數用嵌入 provider:委派給 FakeEmbeddingProvider,記錄批次嵌入被呼叫次數(驗重投未重跑)。</summary>
public sealed class CountingEmbeddingProvider : IEmbeddingProvider
{
    private readonly FakeEmbeddingProvider _inner = new(8);

    public int DocumentCalls { get; private set; }

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        DocumentCalls++;
        return _inner.EmbedDocumentsAsync(texts, ct);
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) => _inner.EmbedQueryAsync(text, ct);
}
