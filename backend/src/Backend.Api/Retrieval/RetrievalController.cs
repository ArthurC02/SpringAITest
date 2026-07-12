using Backend.Api.Common;
using Backend.Api.Files;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Retrieval;

/// <summary>資料檢索端點:向量相似度搜尋。需 X-Tenant-Id。</summary>
[ApiController]
[Route("api/retrieval")]
public sealed class RetrievalController : ControllerBase
{
    private readonly IRagRepository _rag;
    private readonly IEmbeddingProvider _embeddings;

    public RetrievalController(IRagRepository rag, IEmbeddingProvider embeddings)
    {
        _rag = rag;
        _embeddings = embeddings;
    }

    /// <summary>檢索 — 回 { chunks: [{ document_id, title, content, score }] }。top_k 預設 4。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<SearchResponse>> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var topK = request.TopK ?? 4;

        var embedding = await _embeddings.EmbedQueryAsync(request.Query!, ct);
        var chunks = await _rag.SearchAsync(tenantId, embedding, topK, ct);
        return Ok(new SearchResponse(chunks));
    }
}
