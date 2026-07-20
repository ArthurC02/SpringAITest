using System.Net;

namespace Platform.Web.Tests;

/// <summary>
/// P1 審查建議的釘子:兩顆 hosted agent("OperationsAssistant"、"ChatAssistant")與其 <c>AIHostAgent</c>
/// 皆宣告 <c>ServiceLifetime.Singleton</c>——MapAGUI 在啟動期從 root provider 一次性解析 agent
/// (反編譯實證),若誤宣告 Scoped,Development 環境的 <c>ValidateScopes=true</c> 會在 <c>dotnet run</c>
/// 啟動期就炸(captive dependency)。此測試用真正的 "Development" 環境啟動並打一個端點,
/// 釘住「這個崩潰不會再發生」。
/// </summary>
public sealed class DevelopmentEnvironmentSmokeTests
{
    [Fact]
    public async Task DevelopmentEnvironment_StartsUp_AndServesHealthCheck_WithoutValidateScopesCrash()
    {
        await using var factory = new TestWebAppFactory(useDevelopmentEnvironment: true);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/actuator/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // 同一釘子的更直接版本:實際打副駕端點(觸發 hosted agent 解析 + session store 存取),
    // 而不只是健康檢查(健康檢查不經過 agent pipeline)。
    [Fact]
    public async Task DevelopmentEnvironment_AguiEndpoint_ResolvesHostedAgent_WithoutValidateScopesCrash()
    {
        await using var factory = new TestWebAppFactory(useDevelopmentEnvironment: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/copilot/agui")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                threadId = "dev-smoke",
                runId = "r1",
                state = new { },
                messages = new[] { new { id = "m1", role = "user", content = "你好" } },
                tools = Array.Empty<object>(),
                context = Array.Empty<object>(),
                forwardedProps = new { },
            }),
        };
        req.Headers.Accept.ParseAdd("text/event-stream");

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
