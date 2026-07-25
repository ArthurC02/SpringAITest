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
        var scoped = request.ScopeContractVersion is not null
                     || request.KnowledgeSources is not null;
        IReadOnlyList<Guid>? allowedDocumentIds = null;
        if (scoped)
        {
            if (request.ScopeContractVersion != 1 || request.KnowledgeSources is null)
            {
                throw InvalidScope(
                    "scope_contract_version=1 and knowledge_sources must be supplied together");
            }

            var parsed = new HashSet<Guid>();
            foreach (var source in request.KnowledgeSources)
            {
                if (!Guid.TryParseExact(source, "D", out var id)
                    || !string.Equals(source, id.ToString("D"), StringComparison.Ordinal))
                {
                    throw InvalidScope(
                        "knowledge_sources must contain canonical lowercase document UUIDs");
                }
                parsed.Add(id);
            }
            allowedDocumentIds = parsed.ToArray();
            if (allowedDocumentIds.Count == 0)
            {
                return Ok(new SearchResponse(Array.Empty<RetrievedChunk>()));
            }
        }

        var embedding = await _embeddings.EmbedQueryAsync(request.Query!, ct);
        var chunks = allowedDocumentIds is null
            ? await _rag.SearchAsync(tenantId, embedding, topK, ct)
            : await _rag.SearchScopedAsync(
                tenantId,
                embedding,
                topK,
                allowedDocumentIds,
                ct);
        return Ok(new SearchResponse(chunks));
    }

    private static ApiException InvalidScope(string message)
        => new(StatusCodes.Status400BadRequest, message)
        {
            FieldErrors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["knowledge_sources"] = message,
            },
        };
}
