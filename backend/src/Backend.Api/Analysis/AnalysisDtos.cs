using System.Text.Json.Serialization;

namespace Backend.Api.Analysis;

/// <summary>租戶文件統計摘要。JSON:{ document_count, chunk_count, latest_titles }。</summary>
public sealed record AnalysisSummary(
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("latest_titles")] IReadOnlyList<string> LatestTitles);
