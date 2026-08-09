using System.Data.Common;

namespace Backend.Api.Files;

/// <summary>ProcessAsync 的處理結果,供消費者決定 ack/requeue/死信佇列。</summary>
public enum DocumentProcessingOutcome
{
    /// <summary>成功處理完成(或重複投遞的 ready 文件已跳過)。</summary>
    Success,

    /// <summary>暫時性失敗(如 DB/嵌入服務暫時不可用)且未達重試上限;呼叫端應重新投遞。</summary>
    RetryableFailure,

    /// <summary>終態失敗(payload 無法處理、或暫時性失敗已達重試上限);不得再重試,文件已標記 failed(best effort)。</summary>
    TerminalFailure,
}

/// <summary>
/// 文件處理核心(原 POST /api/documents 端點的邏輯,現由佇列消費者驅動):
/// 建列(processing)→ 切塊 → 嵌入 → 寫入切塊並標 ready。
/// 失敗時區分暫時性(DB/嵌入服務暫時不可用等)與終態:暫時性且未達重試上限回傳 RetryableFailure,
/// 交由消費者依 bounded retry 規則重新投遞,不在此標記 failed(文件可能在重試後成功);
/// 其餘一律視為終態,標記為 failed(保留文件列供查詢)並回傳 TerminalFailure,交由消費者轉入死信佇列。
/// </summary>
public sealed class DocumentProcessor
{
    /// <summary>暫時性失敗的重試上限(不含首次嘗試);超過即視為終態。</summary>
    public const int MaxRetries = 3;

    private readonly IRagRepository _rag;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ILogger<DocumentProcessor> _logger;
    private readonly DocumentIngestMetrics _metrics;

    public DocumentProcessor(
        IRagRepository rag,
        IEmbeddingProvider embeddings,
        ILogger<DocumentProcessor> logger,
        DocumentIngestMetrics? metrics = null)
    {
        _rag = rag;
        _embeddings = embeddings;
        _logger = logger;
        _metrics = metrics ?? DocumentIngestMetrics.Shared;
    }

    public async Task<DocumentProcessingOutcome> ProcessAsync(DocumentMessage message, int retryCount, CancellationToken ct)
    {
        try
        {
            // at-least-once 重複投遞:ready 已完成、deleted 是不可復活的 tombstone,兩者都略過。
            // pending_publish/processing/failed 往下重跑,CompleteDocumentAsync 以 row lock + status fence
            // 確保與刪除競爭時不會在 tombstone 後重新寫回 chunks/ready。
            var existing = await _rag.GetDocumentStatusAsync(message.DocumentId, message.TenantId, ct);
            if (existing is "ready" or "deleted")
            {
                _logger.LogInformation(
                    "文件處於不可重跑狀態 {Status},重複投遞略過:documentId={DocumentId}",
                    existing,
                    message.DocumentId);
                return ReportOutcome(DocumentProcessingOutcome.Success);
            }

            // Deletion inventory / producer-cutover compatibility: old Platform binaries publish a
            // fresh document id without first calling ingest-intents, so a missing row must still be
            // created here. Removal gate: this content-free counter must remain at usage=0 through
            // the agreed observation and rollback window after producer cutover.
            if (await _rag.InsertProcessingDocumentAsync(message.DocumentId, message.TenantId, message.Title, ct))
            {
                _metrics.RecordLegacyCreate();
            }

            // 切塊;整體切完為空時退回原文(去頭尾空白),與原 create_document 行為一致。
            var chunks = Chunking.SplitText(message.Text);
            if (chunks.Count == 0)
            {
                chunks = new List<string> { message.Text.Trim() };
            }

            var embeddings = await _embeddings.EmbedDocumentsAsync(chunks, ct);
            await _rag.CompleteDocumentAsync(message.DocumentId, message.TenantId, chunks, embeddings, ct);
            return ReportOutcome(DocumentProcessingOutcome.Success);
        }
        catch (Exception ex)
        {
            if (IsTransient(ex) && retryCount < MaxRetries)
            {
                _logger.LogWarning(
                    ex, "文件處理暫時性失敗,將重新投遞(第 {NextAttempt} 次重試):documentId={DocumentId}",
                    retryCount + 1, message.DocumentId);
                return ReportOutcome(DocumentProcessingOutcome.RetryableFailure);
            }

            // 終態:非暫時性錯誤,或暫時性錯誤已達重試上限。標記為 failed(best effort,markFailed
            // 本身失敗也不得讓 ProcessAsync 拋出 —— 訊息仍要由消費者轉入死信佇列,不得無限重投)。
            _logger.LogError(ex, "文件處理失敗,標記為 failed:documentId={DocumentId}", message.DocumentId);
            try
            {
                // 原因取自封閉集合(見 DocumentFailureReasons)—— 這個欄位會直接回給瀏覽器,
                // 例外訊息本身只留在上面那行伺服器日誌裡。
                await _rag.MarkFailedAsync(
                    message.DocumentId, message.TenantId, DocumentFailureReasons.Classify(ex), ct);
            }
            catch (Exception markEx)
            {
                _logger.LogError(markEx, "標記文件 failed 狀態時發生錯誤:documentId={DocumentId}", message.DocumentId);
            }

            return ReportOutcome(DocumentProcessingOutcome.TerminalFailure);
        }
    }

    private DocumentProcessingOutcome ReportOutcome(DocumentProcessingOutcome outcome)
    {
        _metrics.RecordProcessingOutcome(outcome);
        return outcome;
    }

    /// <summary>
    /// 暫時性失敗分類:DB 連線層錯誤(<see cref="DbException.IsTransient"/>,Npgsql 已正確標記逾時/斷線)、
    /// HTTP 呼叫失敗(嵌入 provider 逾時/不可達)。其餘(payload 解析錯誤、格式錯誤等)一律視為終態。
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        DbException { IsTransient: true } => true,
        HttpRequestException => true,
        TimeoutException => true,
        TaskCanceledException => true,
        _ => false,
    };
}
