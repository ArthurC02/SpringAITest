namespace Backend.Api.Files;

/// <summary>
/// 文件處理核心(原 POST /api/documents 端點的邏輯,現由佇列消費者驅動):
/// 建列(processing)→ 切塊 → 嵌入 → 寫入切塊並標 ready;任一步失敗則標 failed(保留文件列供查詢)、
/// 記錄錯誤、不重丟(訊息由消費者 ack,不重試)。
/// </summary>
public sealed class DocumentProcessor
{
    private readonly IRagRepository _rag;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(IRagRepository rag, IEmbeddingProvider embeddings, ILogger<DocumentProcessor> logger)
    {
        _rag = rag;
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task ProcessAsync(DocumentMessage message, CancellationToken ct)
    {
        try
        {
            // at-least-once 重複投遞:已 ready 直接跳過(避免把已完成的文件重跑/誤標);
            // processing/failed 則往下重跑,CompleteDocumentAsync 會先清舊切塊確保冪等。
            var existing = await _rag.GetDocumentStatusAsync(message.DocumentId, message.TenantId, ct);
            if (existing == "ready")
            {
                _logger.LogInformation("文件已處理完成,重複投遞略過:documentId={DocumentId}", message.DocumentId);
                return;
            }

            await _rag.InsertProcessingDocumentAsync(message.DocumentId, message.TenantId, message.Title, ct);

            // 切塊;整體切完為空時退回原文(去頭尾空白),與原 create_document 行為一致。
            var chunks = Chunking.SplitText(message.Text);
            if (chunks.Count == 0)
            {
                chunks = new List<string> { message.Text.Trim() };
            }

            var embeddings = await _embeddings.EmbedDocumentsAsync(chunks, ct);
            await _rag.CompleteDocumentAsync(message.DocumentId, message.TenantId, chunks, embeddings, ct);
        }
        catch (Exception ex)
        {
            // ponytail: 瞬時故障(如 DB 短暫不可達 → insert 失敗 → markFailed 也失敗)時,訊息仍會被消費者
            // ack 丟棄,不重試 — 刻意取捨。需要更強保證時改 nack+requeue,或加死信佇列(DLQ)。
            _logger.LogError(ex, "文件處理失敗,標記為 failed:documentId={DocumentId}", message.DocumentId);
            try
            {
                await _rag.MarkFailedAsync(message.DocumentId, message.TenantId, ct);
            }
            catch (Exception markEx)
            {
                _logger.LogError(markEx, "標記文件 failed 狀態時發生錯誤:documentId={DocumentId}", message.DocumentId);
            }
        }
    }
}
