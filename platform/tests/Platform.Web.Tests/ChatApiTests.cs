using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class ChatApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ChatApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Chat_Returns_IdAndReply_WithoutPromptField()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好嗎" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.True(body["id"]!.GetValue<long>() > 0);
        Assert.Equal("測試回覆", body["reply"]!.GetValue<string>());
        // ChatResponse 刻意沒有 prompt 欄位。
        Assert.Null(body["prompt"]);
    }

    [Fact]
    public async Task Stream_Returns_EventStream_WithUnspacedDataFrame()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "嗨" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var raw = await resp.Content.ReadAsStringAsync();
        // 每個 chunk 是一個 "data:<value>" frame(冒號後不加空格),event 以空行結尾。
        Assert.Contains("data:你好\n\n", raw);
        Assert.Contains("data:世界\n\n", raw);
        Assert.DoesNotContain("data: 你好", raw);
    }

    [Fact]
    public async Task Chat_Returns400_WhenMessageBlank()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("message 不可為空", body["fieldErrors"]!["message"]!.GetValue<string>());
    }
}
