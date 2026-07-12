using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Retrieval;

/// <summary>向量相似度檢索請求:{ query, top_k(預設 4) }。</summary>
public sealed record SearchRequest(
    [NotBlank(ErrorMessage = "query 不可為空")]
    string? Query,

    // 缺省(null)時 controller 退回 4;有給則須介於 1~50(負值進真 SQL LIMIT 會 500、0 回空結果 — 一律當非法輸入擋成 400)。
    [property: JsonPropertyName("top_k")]
    [Range(1, 50, ErrorMessage = "top_k 必須介於 1 到 50 之間")]
    int? TopK);

/// <summary>檢索命中的單一片段。JSON:{ document_id, title, content, score }。
/// score = 1 - cosine distance,與 pgvector cosine 查詢一致(此查詢現由 backend 持有,workflow 經 HTTP 取用)。</summary>
public sealed record RetrievedChunk(
    [property: JsonPropertyName("document_id")] string DocumentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("score")] double Score);

/// <summary>檢索回應。JSON:{ chunks: [...] }。</summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("chunks")] IReadOnlyList<RetrievedChunk> Chunks);
