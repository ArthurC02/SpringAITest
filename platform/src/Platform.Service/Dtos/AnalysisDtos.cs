using System.Text.Json.Serialization;

namespace Platform.Service.Dtos;

/// <summary>
/// 租戶文件統計摘要。原樣轉發 backend 的回應。
/// JSON:{ document_count, chunk_count, latest_titles }(snake_case)。
/// </summary>
public sealed record AnalysisSummary(
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("chunk_count")] int ChunkCount,
    [property: JsonPropertyName("latest_titles")] IReadOnlyList<string> LatestTitles);
