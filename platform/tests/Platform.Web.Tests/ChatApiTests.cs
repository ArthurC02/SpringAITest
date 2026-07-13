using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task Stream_ChunkWithNewline_SplitsIntoMultipleDataLines()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "多行" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        // 含 \n 的單一 chunk 拆成同一 event 內兩個 data: 行,event 以空行結尾。
        Assert.Contains("data:甲\ndata:乙\n\n", raw);
    }

    // M5:空字串與全空白都是 NotBlank 該擋的等價類(全空白正是 NotBlank 存在的唯一理由)。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Chat_Returns400_WhenMessageBlank(string message)
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat", new { message });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("message 不可為空", body["fieldErrors"]!["message"]!.GetValue<string>());
    }

    // ---- 工作流工具:帶有效 JWT 的聊天掛工具,匿名不掛(工作流需要租戶身分) ----

    [Fact]
    public async Task Chat_WithBearer_EnablesWorkflowTools_UserRoleGetsFour()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "文件裡有什麼?" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
        Assert.Equal(4, agent.LastTools!.Count);
        Assert.Contains(agent.LastTools!, t => t.Name == "search_knowledge_base");
        Assert.DoesNotContain(agent.LastTools!, t => t.Name == "generate_analysis_report");
    }

    [Fact]
    public async Task Chat_WithAdminBearer_AlsoGetsAnalysisReportTool()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken(username: "admin-a", role: "ADMIN"));

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "给我一份報告" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
        Assert.Equal(5, agent.LastTools!.Count);
        Assert.Contains(agent.LastTools!, t => t.Name == "generate_analysis_report");
    }

    [Fact]
    public async Task Chat_Anonymous_HasNoTools()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
        Assert.Null(agent.LastTools);
    }

    [Fact]
    public async Task Stream_Returns400_WhenMessageBlank()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "" });

        // 驗證在進入串流前就回 400 JSON(不寫任何 SSE bytes)。
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType!.MediaType);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("message 不可為空", body["fieldErrors"]!["message"]!.GetValue<string>());
    }
}
