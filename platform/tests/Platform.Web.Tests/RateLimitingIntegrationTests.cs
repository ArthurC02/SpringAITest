using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class RateLimitingIntegrationTests
{
    [Fact]
    public async Task Chat_ThirtyFirstRequest_Returns429_ApiError()
    {
        await using var factory = new TestWebAppFactory(enableRateLimiting: true);
        var client = factory.CreateClient();

        // 空白訊息讓前 30 次停在模型驗證，不呼叫 LLM；第 31 次應由 limiter 先攔截。
        for (var i = 0; i < 30; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/chat", new { message = "" });
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        var response = await client.PostAsJsonAsync("/api/chat", new { message = "" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        var body = await response.ReadJsonAsync();
        Assert.Equal(4, body.AsObject().Count);
        Assert.NotNull(body["timestamp"]);
        Assert.Equal(429, body["status"]!.GetValue<int>());
        Assert.Equal("請求過於頻繁，請稍後再試", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
    }

    // partition key 是「用戶端 IP」而不是「(IP, 路徑)」:三條 LLM 路徑共用同一個 30/分鐘配額。
    // 若有人改成含路徑的 key,同一個用戶端的實際額度會變成 3 倍(違反這個中介軟體存在的目的)。
    [Fact]
    public async Task Chat_And_Stream_ShareOneBudget_PerClientIp()
    {
        await using var factory = new TestWebAppFactory(enableRateLimiting: true);
        var client = factory.CreateClient();

        // 兩條路徑各 15 次(空白訊息停在模型驗證,不呼叫 LLM),合計剛好用滿 30。
        for (var i = 0; i < 15; i++)
        {
            foreach (var path in new[] { "/api/chat", "/api/chat/stream" })
            {
                var allowed = await client.PostAsJsonAsync(path, new { message = "" });
                Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
            }
        }

        var response = await client.PostAsJsonAsync("/api/chat/stream", new { message = "" });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}