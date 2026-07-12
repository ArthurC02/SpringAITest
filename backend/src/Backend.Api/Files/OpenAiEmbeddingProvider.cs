using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Backend.Api.Files;

/// <summary>
/// 透過 LiteLLM 閘道呼叫 OpenAI 相容的嵌入模型(text-embedding-3-small)。
/// 對應 workflow embeddings.py 的 "openai" 分支。僅在 EMBEDDINGS_PROVIDER=openai 時啟用。
/// </summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _model;

    public OpenAiEmbeddingProvider(HttpClient http, string baseUrl, string apiKey, string model)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _model = model;
    }

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => EmbedAsync(texts, ct);

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
        => (await EmbedAsync(new[] { text }, ct))[0];

    private async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> input, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("embeddings", new { model = _model, input }, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("嵌入服務回應為空");
        // 依 index 還原順序,確保與輸入一一對應。
        return payload.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray();
    }

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingData> Data);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
