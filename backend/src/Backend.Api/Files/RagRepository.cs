using System.Globalization;
using System.Text;
using Backend.Api.Analysis;
using Backend.Api.Retrieval;
using Dapper;
using Npgsql;

namespace Backend.Api.Files;

/// <summary>
/// 以 Dapper + Npgsql 實作 rag_documents / rag_chunks 存取。SQL 逐條翻譯自
/// workflow/app/vectorstore.py 的 PgVectorStore,表結構與查詢語意完全一致(舊資料直接可用)。
/// 向量參數以文字 '[...]' 加 ::vector cast 傳入,避免額外相依 pgvector-dotnet。
/// </summary>
public sealed class RagRepository : IRagRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RagRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<string?> GetDocumentStatusAsync(string documentId, string tenantId, CancellationToken ct)
    {
        var docId = Guid.Parse(documentId);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT status FROM rag_documents WHERE id = @docId AND tenant_id = @tenantId",
            new { docId, tenantId }, cancellationToken: ct));
    }

    public async Task InsertProcessingDocumentAsync(string documentId, string tenantId, string title, CancellationToken ct)
    {
        var docId = Guid.Parse(documentId);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO rag_documents (id, tenant_id, title, chunk_count, status)"
            + " VALUES (@docId, @tenantId, @title, 0, 'processing')"
            + " ON CONFLICT (id) DO NOTHING",
            new { docId, tenantId, title }, cancellationToken: ct));
    }

    public async Task CompleteDocumentAsync(
        string documentId, string tenantId, IReadOnlyList<string> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct)
    {
        var docId = Guid.Parse(documentId);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 重跑冪等:先清掉上一次(可能部分)寫入的切塊,避免重複投遞造成 chunk 累積。
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM rag_chunks WHERE document_id = @docId AND tenant_id = @tenantId",
            new { docId, tenantId }, tx, cancellationToken: ct));

        // 單一多行 INSERT(一次 round-trip),取代逐 chunk 各一次往返。交易語意不變。
        // ponytail: 每列 3 個參數,受 PostgreSQL 65535 參數上限約束(約 21k chunks/文件);
        // 文件切塊數遠低於此,超過再分批。
        if (chunks.Count > 0)
        {
            var sql = new StringBuilder(
                "INSERT INTO rag_chunks (id, document_id, tenant_id, content, embedding) VALUES ");
            var p = new DynamicParameters();
            p.Add("docId", docId);
            p.Add("tenantId", tenantId);
            for (var i = 0; i < chunks.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(',');
                }

                sql.Append("(@id").Append(i)
                    .Append(", @docId, @tenantId, @content").Append(i)
                    .Append(", @embedding").Append(i).Append("::vector)");
                p.Add($"id{i}", Guid.NewGuid());
                p.Add($"content{i}", chunks[i]);
                p.Add($"embedding{i}", FormatVector(embeddings[i]));
            }

            await conn.ExecuteAsync(new CommandDefinition(sql.ToString(), p, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rag_documents SET chunk_count = @chunkCount, status = 'ready'"
            + " WHERE id = @docId AND tenant_id = @tenantId",
            new { chunkCount = chunks.Count, docId, tenantId }, tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task MarkFailedAsync(string documentId, string tenantId, CancellationToken ct)
    {
        var docId = Guid.Parse(documentId);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rag_documents SET status = 'failed' WHERE id = @docId AND tenant_id = @tenantId",
            new { docId, tenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<DocumentInfo>(new CommandDefinition(
            "SELECT id::text AS Id, title AS Title, chunk_count AS ChunkCount, created_at AS CreatedAt, status AS Status"
            + " FROM rag_documents WHERE tenant_id = @tenantId ORDER BY created_at",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<bool> DeleteDocumentAsync(string tenantId, string docId, CancellationToken ct)
    {
        if (!Guid.TryParse(docId, out var id))
        {
            return false;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM rag_documents WHERE id = @id AND tenant_id = @tenantId",
            new { id, tenantId }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string tenantId, float[] queryEmbedding, int topK, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RetrievedChunk>(new CommandDefinition(
            "SELECT c.document_id::text AS DocumentId, d.title AS Title, c.content AS Content,"
            + " 1 - (c.embedding <=> @query::vector) AS Score"
            + " FROM rag_chunks c JOIN rag_documents d ON d.id = c.document_id"
            + " WHERE c.tenant_id = @tenantId"
            + " ORDER BY c.embedding <=> @query::vector"
            + " LIMIT @topK",
            new { query = FormatVector(queryEmbedding), tenantId, topK }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchScopedAsync(
        string tenantId,
        float[] queryEmbedding,
        int topK,
        IReadOnlyCollection<Guid> allowedDocumentIds,
        CancellationToken ct)
    {
        if (allowedDocumentIds.Count == 0)
        {
            return Array.Empty<RetrievedChunk>();
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RetrievedChunk>(new CommandDefinition(
            "SELECT c.document_id::text AS DocumentId, d.title AS Title, c.content AS Content,"
            + " 1 - (c.embedding <=> @query::vector) AS Score"
            + " FROM rag_chunks c JOIN rag_documents d ON d.id = c.document_id"
            + " WHERE c.tenant_id = @tenantId AND d.tenant_id = @tenantId"
            + " AND c.document_id = ANY(@allowedDocumentIds)"
            + " ORDER BY c.embedding <=> @query::vector"
            + " LIMIT @topK",
            new
            {
                query = FormatVector(queryEmbedding),
                tenantId,
                topK,
                allowedDocumentIds = allowedDocumentIds.ToArray(),
            },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<AnalysisSummary> SummaryAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var counts = await conn.QuerySingleAsync<(long DocumentCount, long ChunkCount)>(new CommandDefinition(
            "SELECT count(*) AS DocumentCount, coalesce(sum(chunk_count), 0) AS ChunkCount"
            + " FROM rag_documents WHERE tenant_id = @tenantId",
            new { tenantId }, cancellationToken: ct));

        var titles = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT title FROM rag_documents WHERE tenant_id = @tenantId ORDER BY created_at DESC LIMIT 5",
            new { tenantId }, cancellationToken: ct));

        return new AnalysisSummary((int)counts.DocumentCount, (int)counts.ChunkCount, titles.AsList());
    }

    /// <summary>把 float 向量格式化成 pgvector 文字字面值 '[0.1,0.2,...]'(invariant,避免地區小數點)。</summary>
    private static string FormatVector(float[] v) =>
        "[" + string.Join(',', v.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]";
}
