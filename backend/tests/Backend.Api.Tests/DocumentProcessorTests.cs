using System.Data.Common;
using System.Diagnostics.Metrics;
using Backend.Api.Analysis;
using Backend.Api.Files;
using Backend.Api.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Api.Tests;

/// <summary>
/// DocumentProcessor 以 fake repo + fake embeddings 驗 happy / transient / terminal 路徑
/// (不連 broker、不連 DB)。transient vs terminal 分類與 bounded retry 上限(<see cref="DocumentProcessor.MaxRetries"/>)
/// 是消費者決定 requeue / 死信佇列 / 標記 failed 的唯一依據,故在 ProcessAsync 回傳的
/// <see cref="DocumentProcessingOutcome"/> 上完整驗證。
/// </summary>
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

        var outcome = await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.Success, outcome);
        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal(id, doc.Id);
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount); // 兩個以空白行分隔的段落。
    }

    [Fact]
    public async Task Process_TerminalEmbeddingError_MarksFailed_ReturnsTerminal_EvenOnFirstAttempt()
    {
        // poison/終態錯誤的分類不看重試次數 —— 即便是第一次投遞(retryCount=0)也不重試。
        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(repo, new ThrowingEmbeddingProvider(), NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        var outcome = await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.TerminalFailure, outcome);
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

        Assert.Equal(
            DocumentProcessingOutcome.Success,
            await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None)); // 首投 → ready
        Assert.Equal(
            DocumentProcessingOutcome.Success,
            await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None)); // 重投 → 應跳過

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount);
        Assert.Equal(1, embeddings.DocumentCalls); // 只嵌入一次,重投未重跑。
    }

    [Fact]
    public async Task Process_LegacyMessageWithoutAllocation_CreatesProcessingRowUntilProducerCutover()
    {
        // Deletion inventory: this is the intentional legacy missing→create branch. Delete only
        // after every producer calls ingest-intents before publishing and the rollback window ends.
        var repo = new FakeRagRepository();
        var id = Guid.NewGuid().ToString();
        var processor = new DocumentProcessor(repo, new FakeEmbeddingProvider(8), NullLogger<DocumentProcessor>.Instance);

        var outcome = await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.Success, outcome);
        Assert.Equal("ready", (Assert.Single(await repo.ListDocumentsAsync("demo-a", default))).Status);
    }

    [Fact]
    public async Task LegacyCreateMetric_IncrementsOnlyForActualMissingRowCreate_WithoutTags()
    {
        var meterName = DocumentIngestMetrics.MeterName + ".tests." + Guid.NewGuid().ToString("N");
        using var metrics = new DocumentIngestMetrics(meterName);
        using var listener = new MeterListener();
        long total = 0;
        var measurements = 0;
        var tagCount = -1;
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == DocumentIngestMetrics.LegacyCreateCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Interlocked.Add(ref total, value);
            Interlocked.Increment(ref measurements);
            tagCount = tags.Length;
        });
        listener.Start();

        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(
            repo,
            new FakeEmbeddingProvider(8),
            NullLogger<DocumentProcessor>.Instance,
            metrics);
        var legacy = Message(Guid.NewGuid().ToString());
        Assert.Equal(DocumentProcessingOutcome.Success, await processor.ProcessAsync(legacy, 0, default));
        Assert.Equal(DocumentProcessingOutcome.Success, await processor.ProcessAsync(legacy, 1, default));

        var allocated = await repo.AllocateDocumentAsync(
            "demo-a",
            "user-a",
            DocumentIngestIdentity.HashKey("allocated-key"),
            DocumentIngestIdentity.HashRequest("手冊", "第一段。\n\n第二段。"),
            "手冊",
            default);
        Assert.Equal(
            DocumentProcessingOutcome.Success,
            await processor.ProcessAsync(Message(allocated.DocumentId), 0, default));

        Assert.Equal(1, Interlocked.Read(ref total));
        Assert.Equal(1, measurements);
        Assert.Equal(0, tagCount);
    }

    [Fact]
    public async Task ProcessingOutcomeMetric_RecordsEachFinalOutcomeOnce_WithOnlyAllowedOutcomeTag()
    {
        var meterName = DocumentIngestMetrics.MeterName + ".outcomes.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new DocumentIngestMetrics(meterName);
        using var listener = new MeterListener();
        var measurements = new List<(long Value, string Key, string? ValueTag)>();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == DocumentIngestMetrics.ProcessingOutcomeCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Assert.Equal(1, tags.Length);
            measurements.Add((value, tags[0].Key, tags[0].Value?.ToString()));
        });
        listener.Start();

        var successful = new DocumentProcessor(
            new FakeRagRepository(),
            new FakeEmbeddingProvider(8),
            NullLogger<DocumentProcessor>.Instance,
            metrics);
        var id = Guid.NewGuid().ToString();
        Assert.Equal(DocumentProcessingOutcome.Success, await successful.ProcessAsync(Message(id), 0, default));
        Assert.Equal(DocumentProcessingOutcome.Success, await successful.ProcessAsync(Message(id), 1, default));

        var retryable = new DocumentProcessor(
            new FakeRagRepository(),
            new TransientThrowingEmbeddingProvider(),
            NullLogger<DocumentProcessor>.Instance,
            metrics);
        Assert.Equal(
            DocumentProcessingOutcome.RetryableFailure,
            await retryable.ProcessAsync(Message(Guid.NewGuid().ToString()), 0, default));

        var terminal = new DocumentProcessor(
            new FakeRagRepository(),
            new ThrowingEmbeddingProvider(),
            NullLogger<DocumentProcessor>.Instance,
            metrics);
        Assert.Equal(
            DocumentProcessingOutcome.TerminalFailure,
            await terminal.ProcessAsync(Message(Guid.NewGuid().ToString()), 0, default));

        Assert.Equal(4, measurements.Count);
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(1, measurement.Value);
            Assert.Equal("outcome", measurement.Key);
            Assert.Contains(measurement.ValueTag, new[] { "success", "retryable_failure", "terminal_failure" });
        });
        Assert.Equal(2, measurements.Count(measurement => measurement.ValueTag == "success"));
        Assert.Single(measurements, measurement => measurement.ValueTag == "retryable_failure");
        Assert.Single(measurements, measurement => measurement.ValueTag == "terminal_failure");
    }

    [Fact]
    public async Task ProcessingOutcomeMetric_ListenerFailure_DoesNotChangeProcessingResult()
    {
        var meterName = DocumentIngestMetrics.MeterName + ".listener-failure.tests." + Guid.NewGuid().ToString("N");
        using var metrics = new DocumentIngestMetrics(meterName);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == meterName
                && instrument.Name == DocumentIngestMetrics.ProcessingOutcomeCounterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("listener failure"));
        listener.Start();

        var processor = new DocumentProcessor(
            new FakeRagRepository(),
            new FakeEmbeddingProvider(8),
            NullLogger<DocumentProcessor>.Instance,
            metrics);

        Assert.Equal(
            DocumentProcessingOutcome.Success,
            await processor.ProcessAsync(Message(Guid.NewGuid().ToString()), 0, default));
    }

    [Fact]
    public async Task Process_RedeliveryAfterDelete_DoesNotRecreateOrEmbed()
    {
        var repo = new FakeRagRepository();
        var embeddings = new CountingEmbeddingProvider();
        var processor = new DocumentProcessor(repo, embeddings, NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();
        Assert.Equal(DocumentProcessingOutcome.Success, await processor.ProcessAsync(Message(id), 0, default));
        Assert.True(await repo.DeleteDocumentAsync("demo-a", id, default));

        var outcome = await processor.ProcessAsync(Message(id), retryCount: 1, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.Success, outcome);
        Assert.Equal("deleted", await repo.GetDocumentStatusAsync(id, "demo-a", default));
        Assert.Empty(await repo.ListDocumentsAsync("demo-a", default));
        Assert.Equal(1, embeddings.DocumentCalls);
    }

    [Fact]
    public async Task Process_FailedThenRedelivered_RecoversToReady()
    {
        var repo = new FakeRagRepository();
        var id = Guid.NewGuid().ToString();

        // 首投:嵌入終態失敗 → 標 failed(保留文件列)。
        var failing = new DocumentProcessor(repo, new ThrowingEmbeddingProvider(), NullLogger<DocumentProcessor>.Instance);
        Assert.Equal(
            DocumentProcessingOutcome.TerminalFailure,
            await failing.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None));
        var failed = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("failed", failed.Status);
        Assert.Equal(DocumentFailureReasons.Unexpected, failed.FailureReason);

        // 重投(同一訊息)以正常 provider:failed 非 ready → 重跑至 ready;insert ON CONFLICT 不覆寫既有列。
        var recovering = new DocumentProcessor(repo, new FakeEmbeddingProvider(8), NullLogger<DocumentProcessor>.Instance);
        var outcome = await recovering.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.Success, outcome);
        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal(id, doc.Id);
        Assert.Equal("ready", doc.Status);
        Assert.Equal(2, doc.ChunkCount);
        // 重跑成功後不得殘留上一輪的失敗原因。
        Assert.Null(doc.FailureReason);
    }

    /// <summary>
    /// failure_reason 會原樣回給瀏覽器,所以只能是封閉集合裡的固定字串:原始例外訊息(這裡塞了一段
    /// 帶密碼的 sentinel)一個片段都不得外洩。每個等價類一個代表值。
    /// </summary>
    [Fact]
    public async Task TerminalFailure_WritesOnlyClosedSetReasons_NeverTheExceptionText()
    {
        const string sentinel = "SENTINEL Password=hunter2 at RagRepository.cs:42";
        (Exception Failure, string Expected)[] cases =
        [
            (new TimeoutException(sentinel), DocumentFailureReasons.Timeout),
            (new TaskCanceledException(sentinel), DocumentFailureReasons.Timeout),
            (new HttpRequestException(sentinel), DocumentFailureReasons.EmbeddingUnavailable),
            (new FakeTransientDbException(sentinel), DocumentFailureReasons.Unexpected),
            (new InvalidOperationException(sentinel), DocumentFailureReasons.Unexpected),
        ];

        foreach (var (failure, expected) in cases)
        {
            var repo = new FakeRagRepository();
            var processor = new DocumentProcessor(
                repo,
                new TransientThrowingEmbeddingProvider(failure),
                NullLogger<DocumentProcessor>.Instance);

            // 暫時性等價類也走到這裡:重試已用盡,分類仍必須是同一組固定字串。
            Assert.Equal(
                DocumentProcessingOutcome.TerminalFailure,
                await processor.ProcessAsync(
                    Message(Guid.NewGuid().ToString()), DocumentProcessor.MaxRetries, CancellationToken.None));

            var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
            Assert.Equal("failed", doc.Status);
            Assert.Equal(expected, doc.FailureReason);
            Assert.DoesNotContain("SENTINEL", doc.FailureReason!, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", doc.FailureReason!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 舊列相容:reason 為 null 的 failed 文件必須照常讀得出來(欄位是 null,不是空字串)。
    /// </summary>
    [Fact]
    public async Task MarkFailedWithoutAReason_LeavesTheFieldNull_NotAnEmptyString()
    {
        var repo = new FakeRagRepository();
        var id = Guid.NewGuid().ToString();
        await repo.InsertProcessingDocumentAsync(id, "demo-a", "手冊", CancellationToken.None);

        await repo.MarkFailedAsync(id, "demo-a", null, CancellationToken.None);

        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("failed", doc.Status);
        Assert.Null(doc.FailureReason);
    }

    [Fact]
    public async Task Process_EmbeddingThrows_AndMarkFailedAlsoThrows_DoesNotThrow_LogsBothErrors()
    {
        // 雙重故障:嵌入終態失敗 → 進 catch 想標 failed,但 MarkFailedAsync 本身也拋(例如 DB 瞬斷)。
        // ProcessAsync 整體不得拋出(仍要回傳 outcome 供消費者決定 ack/死信佇列);兩段錯誤都要落日誌。
        var repo = new FaultyRagRepository(new FakeRagRepository(), FaultyRagStep.MarkFailed);
        var logger = new RecordingLogger<DocumentProcessor>();
        var processor = new DocumentProcessor(repo, new ThrowingEmbeddingProvider(), logger);
        var id = Guid.NewGuid().ToString();

        var outcome = await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.TerminalFailure, outcome);
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

        var outcome = await processor.ProcessAsync(Message(id, "   "), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.Success, outcome);
        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("ready", doc.Status);
        Assert.Equal(1, doc.ChunkCount);
    }

    [Fact]
    public async Task Process_TransientEmbeddingFailure_BelowRetryLimit_ReturnsRetryable_LeavesDocumentProcessing()
    {
        // Off-point:重試次數還沒到上限(MaxRetries-1),應回 RetryableFailure,不標 failed —— 訊息應由
        // 消費者重新投遞(bounded requeue),而不是直接放棄。
        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(repo, new TransientThrowingEmbeddingProvider(), NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        var outcome = await processor.ProcessAsync(Message(id), retryCount: DocumentProcessor.MaxRetries - 1, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.RetryableFailure, outcome);
        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("processing", doc.Status); // 未被標記 failed —— 仍等待重試。
    }

    // IsTransient 的另外兩個暫時性等價類(逾時 / 取消)—— 與 HttpRequestException 同類:未達重試上限時
    // 一樣回 RetryableFailure 且不標 failed。少了這兩列,誤刪任一 switch arm 也不會有測試變紅。
    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(TaskCanceledException))]
    public async Task Process_TransientEmbeddingFailure_TimeoutOrCancellation_BelowRetryLimit_ReturnsRetryable(
        Type exceptionType)
    {
        var repo = new FakeRagRepository();
        var embeddings = new TransientThrowingEmbeddingProvider((Exception)Activator.CreateInstance(exceptionType)!);
        var processor = new DocumentProcessor(repo, embeddings, NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        var outcome = await processor.ProcessAsync(Message(id), retryCount: 0, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.RetryableFailure, outcome);
        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("processing", doc.Status); // 未被標記 failed —— 仍等待重試。
    }

    [Fact]
    public async Task Process_TransientEmbeddingFailure_AtRetryLimit_ReturnsTerminal_MarksFailed()
    {
        // On-point:重試次數已達上限(MaxRetries),暫時性錯誤也不再重試 —— 視同終態,標記 failed。
        var repo = new FakeRagRepository();
        var processor = new DocumentProcessor(repo, new TransientThrowingEmbeddingProvider(), NullLogger<DocumentProcessor>.Instance);
        var id = Guid.NewGuid().ToString();

        var outcome = await processor.ProcessAsync(Message(id), retryCount: DocumentProcessor.MaxRetries, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.TerminalFailure, outcome);
        var doc = Assert.Single(await repo.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal("failed", doc.Status);
    }

    // 第一步(讀狀態 / 建 processing 列)就遇上暫時性錯誤(如 DB 短暫不可達)—— 與「終態錯誤」不同結果:
    // 文件列根本沒被建立。未達重試上限時應回 RetryableFailure 交由消費者 bounded requeue,而不是
    // 現在已修正掉的舊行為(無條件標記/吞掉、訊息被 ack 丟棄、完全沒有稽核痕跡)。
    [Theory]
    [InlineData(FaultyRagStep.GetStatus)]
    [InlineData(FaultyRagStep.InsertProcessing)]
    public async Task Process_TransientFailureBeforeInsert_BelowRetryLimit_ReturnsRetryable_LeavesNoDocumentRow(
        FaultyRagStep step)
    {
        var inner = new FakeRagRepository();
        var repo = new FaultyRagRepository(inner, step);
        var embeddings = new CountingEmbeddingProvider();
        var processor = new DocumentProcessor(repo, embeddings, NullLogger<DocumentProcessor>.Instance);

        var outcome = await processor.ProcessAsync(
            Message(Guid.NewGuid().ToString()), retryCount: DocumentProcessor.MaxRetries - 1, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.RetryableFailure, outcome);
        Assert.Empty(await inner.ListDocumentsAsync("demo-a", CancellationToken.None));
        Assert.Equal(0, embeddings.DocumentCalls);
    }

    [Theory]
    [InlineData(FaultyRagStep.GetStatus)]
    [InlineData(FaultyRagStep.InsertProcessing)]
    public async Task Process_TransientFailureBeforeInsert_AtRetryLimit_ReturnsTerminal_DoesNotThrow(FaultyRagStep step)
    {
        var inner = new FakeRagRepository();
        var repo = new FaultyRagRepository(inner, step);
        var embeddings = new CountingEmbeddingProvider();
        var logger = new RecordingLogger<DocumentProcessor>();
        var processor = new DocumentProcessor(repo, embeddings, logger);

        var outcome = await processor.ProcessAsync(
            Message(Guid.NewGuid().ToString()), retryCount: DocumentProcessor.MaxRetries, CancellationToken.None);

        Assert.Equal(DocumentProcessingOutcome.TerminalFailure, outcome);
        Assert.Empty(await inner.ListDocumentsAsync("demo-a", CancellationToken.None)); // insert 從未成功過,無列可標。
        Assert.Equal(0, embeddings.DocumentCalls);
        Assert.Contains(logger.Messages, m => m.Contains("文件處理失敗"));
        // MarkFailedAsync 對不存在的 id 是 no-op、不拋例外,故不該出現第二段錯誤訊息。
        Assert.DoesNotContain(logger.Messages, m => m.Contains("標記文件 failed 狀態時發生錯誤"));
    }
}

/// <summary>FaultyRagRepository 要在哪一步拋例外。</summary>
public enum FaultyRagStep
{
    GetStatus,
    InsertProcessing,
    MarkFailed,
}

/// <summary>測試用可控 <see cref="DbException.IsTransient"/> 例外,模擬「DB 暫時不可達」。</summary>
public sealed class FakeTransientDbException : DbException
{
    public FakeTransientDbException(string message) : base(message)
    {
    }

    public override bool IsTransient => true;
}

/// <summary>終態(非暫時性)嵌入例外,用來驗 DocumentProcessor 的 terminal failed 路徑。</summary>
public sealed class ThrowingEmbeddingProvider : IEmbeddingProvider
{
    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => throw new InvalidOperationException("嵌入服務不可達");

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
        => throw new InvalidOperationException("嵌入服務不可達");
}

/// <summary>
/// 暫時性嵌入例外,用來驗 DocumentProcessor 的 bounded retry 路徑;預設 HttpRequestException,
/// 可指定其他暫時性等價類(TimeoutException / TaskCanceledException)。
/// </summary>
public sealed class TransientThrowingEmbeddingProvider : IEmbeddingProvider
{
    private readonly Exception _failure;

    public TransientThrowingEmbeddingProvider(Exception? failure = null)
        => _failure = failure ?? new HttpRequestException("嵌入服務逾時");

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => throw _failure;

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) => throw _failure;
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
/// 包一層 IRagRepository:其餘方法都委派給內層 fake,唯獨指定的那一步拋暫時性(<see cref="FakeTransientDbException"/>)
/// 例外 —— 用來注入「第一步就故障(DB 暫時不可達)」與「嵌入失敗 → 連 MarkFailedAsync 都失敗」兩種故障路徑。
/// </summary>
public sealed class FaultyRagRepository : IRagRepository
{
    private readonly IRagRepository _inner;
    private readonly FaultyRagStep _failingStep;

    public FaultyRagRepository(IRagRepository inner, FaultyRagStep failingStep)
    {
        _inner = inner;
        _failingStep = failingStep;
    }

    private void FailIf(FaultyRagStep step)
    {
        if (_failingStep == step)
        {
            throw new FakeTransientDbException($"DB 暫時不可達:{step}");
        }
    }

    public Task<DocumentIngestAllocation> AllocateDocumentAsync(
        string tenantId,
        string userId,
        string idempotencyKeyHash,
        string requestHash,
        string title,
        CancellationToken ct)
        => _inner.AllocateDocumentAsync(
            tenantId,
            userId,
            idempotencyKeyHash,
            requestHash,
            title,
            ct);

    public Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct)
    {
        FailIf(FaultyRagStep.GetStatus);
        return _inner.GetDocumentStatusAsync(documentId, tenantId, ct);
    }

    public Task<bool> InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct)
    {
        FailIf(FaultyRagStep.InsertProcessing);
        return _inner.InsertProcessingDocumentAsync(documentId, tenantId, title, ct);
    }

    public Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings,
        CancellationToken ct)
        => _inner.CompleteDocumentAsync(documentId, tenantId, chunks, embeddings, ct);

    public Task MarkFailedAsync(string documentId, string tenantId, string? failureReason, CancellationToken ct)
    {
        FailIf(FaultyRagStep.MarkFailed);
        return _inner.MarkFailedAsync(documentId, tenantId, failureReason, ct);
    }

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct)
        => _inner.ListDocumentsAsync(tenantId, ct);

    public Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct)
        => _inner.DeleteDocumentAsync(tenantId, docId, ct);

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string tenantId, float[] queryEmbedding, int topK, CancellationToken ct)
        => _inner.SearchAsync(tenantId, queryEmbedding, topK, ct);

    public Task<IReadOnlyList<RetrievedChunk>> SearchScopedAsync(
        string tenantId,
        float[] queryEmbedding,
        int topK,
        IReadOnlyCollection<Guid> allowedDocumentIds,
        CancellationToken ct)
        => _inner.SearchScopedAsync(
            tenantId,
            queryEmbedding,
            topK,
            allowedDocumentIds,
            ct);

    public Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct)
        => _inner.SummaryAsync(tenantId, ct);
}

/// <summary>手寫 fake logger:記錄層級與格式化後的訊息字串,供斷言特定訊息確實被記錄。
/// 另記錄開啟過的 log scope state,供斷言「哪些情況才開 scope」。</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public List<object> Scopes { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        Scopes.Add(state);
        return new NoopScope();
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
