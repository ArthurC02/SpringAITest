using Backend.Api.Analysis;
using Backend.Api.Files;
using Backend.Api.Retrieval;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Process_FailedThenRedelivered_RecoversToReady()
    {
        var repo = new FakeRagRepository();
        var id = Guid.NewGuid().ToString();

        // 首投:嵌入失敗 → 標 failed(保留文件列)。
        var failing = new DocumentProcessor(repo, new ThrowingEmbeddingProvider(), NullLogger<DocumentProcessor>.Instance);
        await failing.ProcessAsync(Message(id), CancellationToken.None);
        Assert.Equal("failed", (Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None))).Status);

        // 重投(同一訊息)以正常 provider:failed 非 ready → 重跑至 ready;insert ON CONFLICT 不覆寫既有列。
        var recovering = new DocumentProcessor(repo, new FakeEmbeddingProvider(8), NullLogger<DocumentProcessor>.Instance);
        await recovering.ProcessAsync(Message(id), CancellationToken.None);

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal(id, doc.Id);
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount);
    }

    [Fact]
    public async Task Process_EmbeddingThrows_AndMarkFailedAlsoThrows_DoesNotThrow_LogsBothErrors()
    {
        // 雙重故障:嵌入失敗 → 進 catch 想標 failed,但 MarkFailedAsync 本身也拋(例如 DB 瞬斷)。
        // 訊息仍要被消費者 ack(不重試),故 ProcessAsync 整體不得拋出;兩段錯誤都要落日誌。
        var repo = new ThrowingMarkFailedRagRepository(new FakeRagRepository());
        var logger = new RecordingLogger<DocumentProcessor>();
        var processor = new DocumentProcessor(repo, new ThrowingEmbeddingProvider(), logger);
        var id = Guid.NewGuid().ToString();

        await processor.ProcessAsync(Message(id), CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("文件處理失敗"));
        Assert.Contains(logger.Messages, m => m.Contains("標記文件 failed 狀態時發生錯誤"));
    }

    [Fact]
    public async Task Process_BlankText_FallsBackToSingleEmptyChunk_Ready()
    {
        // ponytail: 空白內文的現行 POC 行為 — 切塊為空時退回單一(trim 後的空)切塊並標 ready。
        // 非理想(理應標 failed 或拒收),但這是刻意接受的 POC 取捨;以測試釘住避免無意間改變。
        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(repo, new FakeEmbeddingProvider(8), NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        await processor.ProcessAsync(Message(id, "   "), CancellationToken.None);

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("ready", doc.Status);
        Assert.Equal(1, doc.ChunkCount);
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

/// <summary>
/// 包一層 IRagRepository:其餘方法都委派給內層 fake,唯獨 MarkFailedAsync 拋例外 ——
/// 用來重現「嵌入失敗 → 想標 failed 但連 MarkFailedAsync 都失敗」的雙重故障路徑。
/// </summary>
public sealed class ThrowingMarkFailedRagRepository : IRagRepository
{
    private readonly IRagRepository _inner;

    public ThrowingMarkFailedRagRepository(IRagRepository inner) => _inner = inner;

    public Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct)
        => _inner.GetDocumentStatusAsync(documentId, tenantId, ct);

    public Task InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct)
        => _inner.InsertProcessingDocumentAsync(documentId, tenantId, title, ct);

    public Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings,
        CancellationToken ct)
        => _inner.CompleteDocumentAsync(documentId, tenantId, chunks, embeddings, ct);

    public Task MarkFailedAsync(string documentId, string tenantId, CancellationToken ct)
        => throw new InvalidOperationException("DB 不可達,無法標記 failed");

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct)
        => _inner.ListDocumentsAsync(tenantId, ct);

    public Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct)
        => _inner.DeleteDocumentAsync(tenantId, docId, ct);

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string tenantId, float[] queryEmbedding, int topK, CancellationToken ct)
        => _inner.SearchAsync(tenantId, queryEmbedding, topK, ct);

    public Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct)
        => _inner.SummaryAsync(tenantId, ct);
}

/// <summary>手寫 fake logger:只記錄格式化後的訊息字串,供斷言特定錯誤訊息確實被記錄。</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}
