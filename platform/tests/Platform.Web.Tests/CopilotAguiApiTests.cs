using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

/// <summary>
/// AG-UI(CopilotKit 操作助理)端點冒煙測試。底層 IChatClient 由 TestWebAppFactory 換成 FakeChatClient,
/// 所以不打真的 LiteLLM,但仍走真正的 MapAGUI 請求解析與 SSE 序列化。
/// </summary>
public sealed class CopilotAguiApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public CopilotAguiApiTests(TestWebAppFactory factory) => _factory = factory;

    private static object RunInput() => new
    {
        threadId = "t1",
        runId = "r1",
        state = new { },
        messages = new[] { new { id = "m1", role = "user", content = "你好" } },
        tools = Array.Empty<object>(),
        context = Array.Empty<object>(),
        forwardedProps = new { },
    };

    [Fact]
    public async Task Agui_Endpoint_Exists()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/copilot/agui", RunInput());

        // 端點存在(有掛 MapAGUI):POST 不該是 404。
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Agui_Streams_Sse_Events()
    {
        var client = _factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/copilot/agui")
        {
            Content = JsonContent.Create(RunInput()),
        };
        req.Headers.Accept.ParseAdd("text/event-stream");

        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var raw = await resp.Content.ReadAsStringAsync();
        // AG-UI 協定事件:一定有 RUN_STARTED,且 fake 的兩塊文字會成為 TEXT_MESSAGE_CONTENT。
        Assert.Contains("RUN_STARTED", raw);
        Assert.Contains("TEXT_MESSAGE_CONTENT", raw);
    }
}
