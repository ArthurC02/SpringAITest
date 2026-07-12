using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Platform.Web.Tests;

internal static class ApiTestHelpers
{
    /// <summary>掛上 Bearer token,回傳同一個 client 以便串接。</summary>
    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>把回應 body 解析成 JsonNode(獨立、不需釋放)。</summary>
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())
           ?? throw new InvalidOperationException("回應 body 不是有效 JSON");
}
