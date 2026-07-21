using System.Collections.Concurrent;
using Backend.Api.Analysis;
using Backend.Api.Files;
using Backend.Api.Retrieval;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// rag 儲存庫的行程記憶體實作(Lite 模式)。per-tenant 隔離;追蹤 status(processing → ready / failed)。
/// 與測試 fake 的唯一實質差異:<see cref="SearchAsync"/> 算**真** cosine 相似度(取代固定 score=1.0),
/// score = cosine(query, chunk),與 pgvector cosine 查詢語義一致;維度不匹配的 chunk 直接略過(搜尋不炸)。
/// 執行緒安全:_docs 用 ConcurrentDictionary,單一 Doc 的 Chunks 列讀寫以 lock 保護。
/// </summary>
public sealed class InMemoryRagRepository : IRagRepository
{
    private sealed class Doc
    {
        public required string Id { get; init; }
        public required string TenantId { get; init; }
        public required string Title { get; init; }
        public required DateTime CreatedAt { get; init; }
        public int ChunkCount { get; set; }
        public string Status { get; set; } = "processing";
        public List<(string Content, float[] Embedding)> Chunks { get; set; } = new();
    }

    private readonly ConcurrentDictionary<string, Doc> _docs = new();
    private readonly object _lockObj = new();

    public Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct)
        => Task.FromResult(_docs.TryGetValue(documentId, out var d) && d.TenantId == tenantId ? d.Status : null);

    public Task InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct)
    {
        // ON CONFLICT (id) DO NOTHING:已存在則保留(重複投遞不覆寫既有狀態)。
        _docs.TryAdd(documentId, new Doc
        {
            Id = documentId, TenantId = tenantId, Title = title, CreatedAt = DateTime.UtcNow,
            ChunkCount = 0, Status = "processing",
        });
        return Task.CompletedTask;
    }

    public Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct)
    {
        lock (_lockObj)
        {
            if (_docs.TryGetValue(documentId, out var d) && d.TenantId == tenantId)
            {
                d.Chunks = chunks.Zip(embeddings, (c, e) => (c, e)).ToList();
                d.ChunkCount = chunks.Count;
                d.Status = "ready";
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(string documentId, string tenantId, CancellationToken ct)
    {
        if (_docs.TryGetValue(documentId, out var d) && d.TenantId == tenantId)
        {
            d.Status = "failed";
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DocumentInfo>>(
            _docs.Values.Where(d => d.TenantId == tenantId).OrderBy(d => d.CreatedAt)
                .Select(d => new DocumentInfo(d.Id, d.Title, d.ChunkCount, d.CreatedAt, d.Status)).ToList());

    public Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct)
        => Task.FromResult(_docs.TryGetValue(docId, out var d) && d.TenantId == tenantId && _docs.TryRemove(docId, out _));

    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string tenantId, float[] queryEmbedding, int topK, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var hits = _docs.Values.Where(d => d.TenantId == tenantId)
                .SelectMany(d => d.Chunks.Select(c => (d.Id, d.Title, c.Content, c.Embedding)))
                .Select(x => (x.Id, x.Title, x.Content, Score: Cosine(queryEmbedding, x.Embedding)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score!.Value)
                .Take(topK)
                .Select(x => new RetrievedChunk(x.Id, x.Title, x.Content, x.Score!.Value))
                .ToList();
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>(hits);
        }
    }

    public Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct)
    {
        var mine = _docs.Values.Where(d => d.TenantId == tenantId).ToList();
        var titles = mine.OrderByDescending(d => d.CreatedAt).Take(5).Select(d => d.Title).ToList();
        return Task.FromResult(new AnalysisSummary(mine.Count, mine.Sum(d => d.ChunkCount), titles));
    }

    /// <summary>cosine 相似度 = dot / (|a| * |b|);維度不匹配回 null(該 chunk 略過),退化(零向量)回 0。</summary>
    private static double? Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return null;
        }

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        magA = Math.Sqrt(magA);
        magB = Math.Sqrt(magB);
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }
}
