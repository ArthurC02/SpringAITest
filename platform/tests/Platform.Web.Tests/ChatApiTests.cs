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
        // 匿名聊天不持久化(對話以 (tenant_id, user_id) 隔離),id 一律為 0;帶身分才驗證 backend 產生的 id。
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

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

    // 串流中途失敗:LLM 在已送出 token 後拋錯 → 補一個 event:error 終止 frame(無空格 data: 風格),回應仍正常結束(200)。
    // 釘住終止語意:已送出的 token 在前、error frame 在後、通用訊息不含例外細節。
    [Fact]
    public async Task Stream_MidStreamFailure_EmitsErrorFrame_ThenEndsNormally()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "串流爆炸" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Equal("data:半截\n\nevent:error\ndata:回覆過程發生錯誤，請稍後再試\n\n", raw);
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

    // 五顆內建可路由 skill(鏡射 workflow GET /skills 真實回應形狀),供以下三個路由目錄端到端測試共用。
    private const string BuiltinCatalog = """
    [
      { "name":"kb_query", "description":"可稽核的知識查詢", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } },
      { "name":"rag_qa", "description":"一般文件知識庫問答", "required_role":"USER", "source":"builtin",
        "input_schema": { "question": { "type":"str", "required":true } } },
      { "name":"summarize", "description":"文字摘要", "required_role":"USER", "source":"builtin",
        "input_schema": { "text": { "type":"str", "required":true } } },
      { "name":"triage", "description":"問題分流", "required_role":"USER", "source":"builtin",
        "input_schema": { "question": { "type":"str", "required":true } } },
      { "name":"analyze_report", "description":"分析報告", "required_role":"ADMIN", "source":"builtin",
        "input_schema": { "topic": { "type":"str", "required":true } } }
    ]
    """;

    [Fact]
    public async Task Chat_WithBearer_EnablesSkillTools_UserRoleGetsFour()
    {
        FakeWorkflowService.CatalogOverride = System.Text.Json.JsonDocument.Parse(BuiltinCatalog).RootElement.Clone();
        try
        {
            var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
            agent.Reset();
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await client.PostAsJsonAsync("/api/chat", new { message = "文件裡有什麼?" });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var catalog = agent.LastRoutingCatalog!;
            Assert.Equal(4, ToolLineCount(catalog));
            Assert.Contains("kb_query", catalog);
            Assert.DoesNotContain("analyze_report", catalog);
        }
        finally
        {
            FakeWorkflowService.CatalogOverride = null;
        }
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
            // 單軌後路由目錄恰等於動態 Skill 目錄(builtin + custom),沒有殘留靜態工具混入。
            Assert.Contains("kb_query", catalog);
            Assert.Contains("tenant_a_private_search", catalog);
            Assert.Equal(2, ToolLineCount(catalog));
        }
        finally
        {
            FakeWorkflowService.CatalogOverride = null;
        }
    }

    [Fact]
    public async Task Chat_WithAdminBearer_AlsoGetsAnalyzeReportTool()
    {
        FakeWorkflowService.CatalogOverride = System.Text.Json.JsonDocument.Parse(BuiltinCatalog).RootElement.Clone();
        try
        {
            var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
            agent.Reset();
            var client = _factory.CreateClient().WithToken(_factory.IssueToken(username: "admin-a", role: "ADMIN"));

            var resp = await client.PostAsJsonAsync("/api/chat", new { message = "给我一份報告" });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var catalog = agent.LastRoutingCatalog!;
            Assert.Equal(5, ToolLineCount(catalog));
            Assert.Contains("analyze_report", catalog);
        }
        finally
        {
            FakeWorkflowService.CatalogOverride = null;
        }
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

    // ---- X-Auth-Invalid：AllowAnonymous 端點永遠不回 401,靠這個 header 讓前端全域登出機制打得到 ----

    [Fact]
    public async Task Chat_WithValidToken_HasNoAuthInvalidHeader()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("X-Auth-Invalid"));
    }

    [Fact]
    public async Task Chat_WithoutToken_HasNoAuthInvalidHeader()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("X-Auth-Invalid"));
    }

    [Fact]
    public async Task Chat_WithInvalidToken_HasAuthInvalidHeader()
    {
        var token = TestTokens.Mint() + "x"; // 竄改簽章尾段。
        var client = _factory.CreateClient().WithToken(token);

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("1", resp.Headers.GetValues("X-Auth-Invalid").Single());
    }

    // ---- 聊天歷史依身分過濾:匿名回空陣列(不是 401,維持 AllowAnonymous 契約) ----

    [Fact]
    public async Task History_Anonymous_ReturnsEmptyArray()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/chat/history");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Empty(arr);
    }

    [Fact]
    public async Task Stream_WithInvalidToken_HasAuthInvalidHeader()
    {
        var token = TestTokens.Mint() + "x"; // 竄改簽章尾段。
        var client = _factory.CreateClient().WithToken(token);

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "嗨" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("1", resp.Headers.GetValues("X-Auth-Invalid").Single());
    }
}
