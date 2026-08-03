using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Platform.Web.Tests;

internal static class ApiTestHelpers
{
    /// <summary>組一個測試請求:任意動詞 + 可選 If-Match 前置條件 + 可選 JSON body。</summary>
    public static HttpRequestMessage Request(
        string method, string path, string? ifMatch = null, object? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>掛上 Bearer token,回傳同一個 client 以便串接。</summary>
    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>ADMIN 身分(admin-a / demo-a)的已授權 client。</summary>
    public static HttpClient AdminClient(this TestWebAppFactory factory)
        => factory.CreateClient().WithToken(factory.IssueToken("admin-a", "ADMIN", "demo-a"));

    /// <summary>預設 USER 身分(user-a / demo-a,IssueToken 的預設值)的已授權 client。</summary>
    public static HttpClient UserClient(this TestWebAppFactory factory)
        => factory.CreateClient().WithToken(factory.IssueToken());

    /// <summary>把回應 body 解析成 JsonNode(獨立、不需釋放)。</summary>
    public static async Task<JsonNode> ReadJsonAsync(this HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())
           ?? throw new InvalidOperationException("回應 body 不是有效 JSON");

    /// <summary>
    /// 完整 ApiError envelope 的唯一斷言點(02-spec §5):恰好六個欄位
    /// { timestamp, status, code, message, correlationId, fieldErrors },
    /// code 依狀態碼是穩定機器碼、correlationId 非空、fieldErrors 一定存在。
    /// 六個欄位逐一比對比 Count 更值錢的部分在 code/correlationId —— 少了它們就沒測到 P1 的新契約。
    /// </summary>
    public static void AssertApiError(this JsonNode body, int status, string code)
    {
        var obj = body.AsObject();
        Assert.Equal(6, obj.Count);
        Assert.NotNull(body["timestamp"]);
        Assert.Equal(status, body["status"]!.GetValue<int>());
        Assert.Equal(code, body["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(body["correlationId"]!.GetValue<string>()));
        Assert.NotNull(body["fieldErrors"]);
    }
}
