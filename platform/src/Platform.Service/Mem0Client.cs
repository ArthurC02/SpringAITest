using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Service.Abstractions;
using Platform.Service.Options;
using Microsoft.Extensions.Logging;

namespace Platform.Service;

/// <summary>
/// mem0 長期記憶 client。所有錯誤都吞掉(記憶是加分項,絕不讓聊天失敗)。
/// </summary>
public sealed class Mem0Client : IMem0Client
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Mem0Options _options;
    private readonly ILogger<Mem0Client> _logger;

    public Mem0Client(HttpClient http, Mem0Options options, ILogger<Mem0Client> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    private string BaseUrl => _options.BaseUrl.TrimEnd('/');

    public async Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
    {
        try
        {
            // POST {base}/search  body: { query, user_id, top_k: 5 }
            var body = new { query, user_id = userId, top_k = 5 };
            using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/search", body, JsonOpts, ct);
            resp.EnsureSuccessStatusCode();

            var doc = await resp.Content.ReadFromJsonAsync<Mem0SearchResponse>(JsonOpts, ct);
            if (doc?.Results is null || doc.Results.Count == 0)
            {
                return string.Empty;
            }

            // 每則記憶組成 "- memory\n" 後串接。
            var sb = new StringBuilder();
            foreach (var item in doc.Results)
            {
                sb.Append("- ").Append(item.Memory).Append('\n');
            }

            return sb.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 呼叫端主動取消,原樣拋出(不算 mem0 失敗)。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("mem0 recall 失敗，改用空記憶：{訊息}", ex.Message);
            return string.Empty;
        }
    }

    public async Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
    {
        try
        {
            // POST {base}/memories  body: { user_id, messages: [ {role:user}, {role:assistant} ] }
            var body = new
            {
                user_id = userId,
                messages = new[]
                {
                    new { role = "user", content = userMessage },
                    new { role = "assistant", content = aiReply },
                },
            };
            using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/memories", body, JsonOpts, ct);
            resp.EnsureSuccessStatusCode();
            // 回應忽略。
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 呼叫端主動取消,原樣拋出(不算 mem0 失敗)。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("mem0 remember 失敗，跳過本輪記憶：{訊息}", ex.Message);
        }
    }

    // mem0 /search 回應形狀:{ "results": [ { "memory": "..." }, ... ] }
    private sealed record Mem0SearchResponse(
        [property: JsonPropertyName("results")] List<Mem0Memory>? Results);

    private sealed record Mem0Memory(
        [property: JsonPropertyName("memory")] string? Memory);
}
