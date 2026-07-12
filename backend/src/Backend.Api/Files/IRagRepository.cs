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
    /// <summary>取得文件現況 status;文件不存在(或非本租戶)回 null。供消費者判斷重複投遞。</summary>
    Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct);

    /// <summary>建立文件列(chunk_count=0、status='processing');id 由發佈端(platform)生成的 GUID。
    /// 重複投遞冪等:id 已存在則不動(ON CONFLICT DO NOTHING)。</summary>
    Task InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct);

    /// <summary>清掉舊切塊(重跑冪等)後寫入切塊 + 更新 chunk_count 與 status='ready'(單一交易)。
    /// 文件列已被刪除則不影響任何列。</summary>
    Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct);

    /// <summary>將文件標記為 status='failed'(保留供查詢);文件列已被刪除則不影響任何列。</summary>
    Task MarkFailedAsync(string documentId, string tenantId, CancellationToken ct);

    /// <summary>列出租戶文件(僅中繼資料,含 status),created_at ASC。</summary>
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct);

    /// <summary>刪除租戶文件;成功回 true,找不到(含非本租戶或 id 非法)回 false。</summary>
    Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct);

    /// <summary>向量相似度檢索,依 cosine distance 由近到遠,score = 1 - distance。</summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string tenantId, float[] queryEmbedding, int topK, CancellationToken ct);

    Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct);
}
