using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class RateLimitingIntegrationTests
{
    [Fact]
    public async Task Chat_ThirtyFirstRequest_Returns429_ApiError()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        var client = factory.CreateClient().WithToken(factory.IssueToken());

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
        body.AssertApiError(429, "rate_limited");
        Assert.Equal("請求過於頻繁，請稍後再試", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
    }

    // partition key 是「用戶端 IP」而不是「(IP, 路徑)」:三條 LLM 路徑共用同一個 30/分鐘配額。
    // 若有人改成含路徑的 key,同一個用戶端的實際額度會變成 3 倍(違反這個中介軟體存在的目的)。
    [Fact]
    public async Task Chat_And_Stream_ShareOneBudget_PerClientIp()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        var client = factory.CreateClient().WithToken(factory.IssueToken());

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

    // 第三條受限路徑 /api/copilot/agui(GlobalLimiter 的 StartsWithSegments 那一格)。它雖然是
    // RequireAuthorization(),節流卻刻意掛在認證之前(Program.cs:UseRateLimiter 早於 UseAuthentication),
    // 所以被 401 擋下的匿名請求同樣吃掉配額——這正是這個中介軟體存在的目的(匿名迴圈也壓得動連線)。
    // 混合 15 次 /api/chat + 15 次 /api/copilot/agui 剛好用滿同一個 per-IP 配額:
    // 若副駕不在受限清單、或 partition key 含路徑,第 31 次會是 401 而不是 429。
    [Fact]
    public async Task Chat_And_Agui_ShareOneBudget_PerClientIp()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        var client = factory.CreateClient().WithToken(factory.IssueToken());
        var anonymousClient = factory.CreateClient();

        for (var i = 0; i < 15; i++)
        {
            var chat = await client.PostAsJsonAsync("/api/chat", new { message = "" });
            Assert.Equal(HttpStatusCode.BadRequest, chat.StatusCode);

            // 不帶 JWT:副駕端點回 401(不進入 agent),但配額已在認證之前先扣。
            var agui = await anonymousClient.PostAsJsonAsync("/api/copilot/agui", new { });
            Assert.Equal(HttpStatusCode.Unauthorized, agui.StatusCode);
        }

        var response = await anonymousClient.PostAsJsonAsync("/api/copilot/agui", new { });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // GlobalLimiter 的另一半:不在那三條路徑上的請求一律走 GetNoLimiter("__unlimited")。
    // 聊天配額燒光後,未列管端點不得被牽連——容器探針不能因為有人狂打 /api/chat 就開始失敗。
    [Fact]
    public async Task UnlistedPath_AfterChatBudgetExhausted_StillReturns200()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        var client = factory.CreateClient().WithToken(factory.IssueToken());

        for (var i = 0; i < 30; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/chat", new { message = "" });
            Assert.Equal(HttpStatusCode.BadRequest, allowed.StatusCode);
        }

        // 前置條件:同一個 client 的配額確實已用完(否則下面的斷言會空過)。
        var exhausted = await client.PostAsJsonAsync("/api/chat", new { message = "" });
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        var health = await client.GetAsync("/actuator/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
