using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Web.Tests;

[Collection("EngineCalls")]
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

    // CSR-P1-026 / W3:以 raw bytes 斷言精確 wire contract — body 精確為 data:<value>\n\n 串接,
    // data: 後無空格、每事件空行結尾、無自訂 event type / tool JSON / trace。
    [Fact]
    public async Task Stream_ExactWireContract_RawBytes_NoSpaceAfterData()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "嗨" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var expected = System.Text.Encoding.UTF8.GetBytes("data:你好\n\ndata:世界\n\n");
        Assert.Equal(expected, bytes);
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

    // ---- 路由目錄:帶有效 JWT 的聊天以工具目錄做路由,匿名不路由(工作流需要租戶身分) ----
    // 新流程:工具改以「路由目錄」文字經路由呼叫傳入,故斷言 LastRoutingCatalog 而非原生 tools 引數。

    /// <summary>路由目錄中的工具行數(排除路由指令段)。目錄格式為「指令\n\n名稱: 說明(每行一支)」。</summary>
    private static int ToolLineCount(string catalog)
        => catalog.Split("\n\n")[^1].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    [Fact]
    public async Task Chat_WithBearer_EnablesWorkflowTools_UserRoleGetsFour()
    {
        var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
        agent.Reset();
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "文件裡有什麼?" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var catalog = agent.LastRoutingCatalog!;
        Assert.Equal(4, ToolLineCount(catalog));
        Assert.Contains("search_knowledge_base", catalog);
        Assert.DoesNotContain("generate_analysis_report", catalog);
    }

    // W1:帶有效 JWT + 可路由目錄 → 動態 Skill 進入路由目錄(端到端經 DI 走 BuildToolsAsync → SkillCatalogToTools)。
    [Fact]
    public async Task Chat_WithBearer_RoutesDynamicSkillsFromCatalog()
    {
        FakeWorkflowService.CatalogOverride = System.Text.Json.JsonDocument.Parse("""
        [
          { "name":"kb_query", "description":"內建檢索", "required_role":"USER", "source":"builtin",
            "input_schema": { "query": { "type":"str", "required":true } } },
          { "name":"tenant_a_private_search", "description":"自訂檢索", "required_role":"USER", "source":"custom",
            "input_schema": { "question_text": { "type":"str", "required":true } } }
        ]
        """).RootElement.Clone();
        try
        {
            var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
            agent.Reset();
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await client.PostAsJsonAsync("/api/chat", new { message = "文件裡有什麼?" });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var catalog = agent.LastRoutingCatalog!;
            // 2 支動態 Skill + 4 支殘留靜態(USER)。
            Assert.Contains("kb_query", catalog);
            Assert.Contains("tenant_a_private_search", catalog);
            Assert.Contains("search_knowledge_base", catalog);
            Assert.Equal(6, ToolLineCount(catalog));
        }
        finally
        {
            FakeWorkflowService.CatalogOverride = null;
        }
    }

    [Fact]
    public async Task Chat_WithAdminBearer_AlsoGetsAnalysisReportTool()
    {
        var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
        agent.Reset();
        var client = _factory.CreateClient().WithToken(_factory.IssueToken(username: "admin-a", role: "ADMIN"));

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "给我一份報告" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var catalog = agent.LastRoutingCatalog!;
        Assert.Equal(5, ToolLineCount(catalog));
        Assert.Contains("generate_analysis_report", catalog);
    }

    [Fact]
    public async Task Chat_Anonymous_HasNoTools()
    {
        var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
        agent.Reset();
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // 匿名不路由:沒有路由目錄,只有一次純聊天呼叫。
        Assert.Null(agent.LastRoutingCatalog);
        Assert.Equal(1, agent.CompleteCallCount);
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
