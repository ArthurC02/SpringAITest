using Backend.Api.Analysis;
using Backend.Api.Retrieval;

namespace Backend.Api.Files;

/// <summary>
/// rag_documents / rag_chunks 兩表的資料存取,由 Files、Retrieval、Analysis 三個 feature 共用
/// (皆讀寫同一組多租戶向量資料表)。薄介面供測試換 fake;SQL 正確性交給 E2E。
/// 所有方法都以 tenantId 作第一層隔離:任何查詢/刪除只碰得到該租戶的資料。
/// </summary>
public interface IRagRepository
{
    /// <summary>
    /// Atomically allocate the pending document row and its hashed idempotency identity. A replay
    /// returns the original id; a changed payload or tombstoned document returns a conflict status.
    /// </summary>
    Task<DocumentIngestAllocation> AllocateDocumentAsync(
        string tenantId,
        string userId,
        string idempotencyKeyHash,
        string requestHash,
        string title,
        CancellationToken ct);

    /// <summary>取得文件現況 status;文件不存在(或非本租戶)回 null。供消費者判斷重複投遞。</summary>
    Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct);

    /// <summary>建立 legacy 文件列，或把 pending_publish/failed 轉成 processing。
    /// ready/deleted 與其他租戶的同 id 均不動。</summary>
    Task<bool> InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct);

    /// <summary>清掉舊切塊(重跑冪等)後寫入切塊 + 更新 chunk_count 與 status='ready'(單一交易)。
    /// 文件列已被刪除則不影響任何列。</summary>
    Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct);

    /// <summary>將文件標記為 status='failed'(保留供查詢);文件列已被刪除則不影響任何列。
    /// <paramref name="failureReason"/> 必須取自 <see cref="DocumentFailureReasons"/> 的封閉集合
    /// (該欄位會回給瀏覽器);null 代表沒有分類,欄位保持 NULL。</summary>
    Task MarkFailedAsync(string documentId, string tenantId, string? failureReason, CancellationToken ct);

    /// <summary>列出租戶文件(僅中繼資料,含 status),created_at ASC。</summary>
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct);

    /// <summary>交易內 tombstone 文件並清除切塊;成功回 true,找不到/已刪除回 false。</summary>
    Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct);

    /// <summary>向量相似度檢索,依 cosine distance 由近到遠,score = 1 - distance。</summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string tenantId, float[] queryEmbedding, int topK, CancellationToken ct);

    /// <summary>
    /// D3 scoped retrieval. The server-injected immutable scope is authoritative; implementations
    /// must apply both tenant and exact document-ID predicates, and an empty scope returns empty.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchScopedAsync(
        string tenantId,
        float[] queryEmbedding,
        int topK,
        IReadOnlyCollection<Guid> allowedDocumentIds,
        CancellationToken ct);

    Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct);
}
