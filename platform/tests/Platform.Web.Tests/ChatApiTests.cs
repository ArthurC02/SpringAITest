using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    // CSR-P1-026 / W3:以 raw bytes 斷言精確 wire contract — body 精確為 data:<value>\n\n 串接,
    // data: 後無空格、每事件空行結尾、無自訂 event type / tool JSON / trace。
    // (逐 byte 全等已嚴格涵蓋舊 Stream_Returns_EventStream_WithUnspacedDataFrame 的 Contains 版斷言。)
    [Fact]
    public async Task Stream_ExactWireContract_RawBytes_NoSpaceAfterData()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "嗨" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var expected = System.Text.Encoding.UTF8.GetBytes("data:你好\n\ndata:世界\n\n");
        Assert.Equal(expected, bytes);
    }

    // A-17/T-P4-4:串流中途失敗:LLM 在已送出 token 後拋錯 → 補一個 event:error 終止 frame(無空格 data: 風格),
    // 回應仍正常結束(200)。釘住終止語意:已送出的 token 在前、error frame 在後、通用訊息不含例外細節;
    // 且半截回覆不得持久化(AddAsync 與 mem0 RememberAsync 皆零呼叫)——用已登入身分跑(持久化「本該」
    // 會被嘗試),證明例外在抵達 ChatTurnRecorder 的持久化/remember 之前就已經中止了整個方法
    // (§9.3 鐵律:SkillRoutingAgent/ChatTurnRecorder 皆不得吞掉導致此幀的例外,見兩者類別 XML doc)。
    [Fact]
    public async Task Stream_MidStreamFailure_EmitsErrorFrame_ThenEndsNormally()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        var savedBefore = FakeConversationStore.Saved.Count;
        var rememberedBefore = FakeMem0Client.Remembered.Count;

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "串流爆炸" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.Equal("data:半截\n\nevent:error\ndata:回覆過程發生錯誤，請稍後再試\n\n", raw);
        // 半截回覆不得持久化:中途失敗的例外在抵達持久化程式碼之前就已經中止整個 StreamChatAsync。
        Assert.Equal(savedBefore, FakeConversationStore.Saved.Count);
        Assert.Equal(rememberedBefore, FakeMem0Client.Remembered.Count);
    }

    // ---- A-15 / A-16:持久化失敗的決策表兩半 —— 阻塞 500 vs 串流 best-effort,刻意不同,必須成對驗 ----

    // A-15:阻塞式 /api/chat 的持久化失敗往上拋 → 500 + ApiError envelope 六鍵齊全,通用中文訊息不洩漏例外細節。
    [Fact]
    public async Task Chat_Returns500_WithGenericApiError_WhenPersistenceFails()
    {
        FakeConversationStore.ThrowOnAdd = true;
        try
        {
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await client.PostAsJsonAsync("/api/chat", new { message = "你好" });

            Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
            var body = await resp.ReadJsonAsync();
            body.AssertApiError(500, "internal_error");
            Assert.Equal("伺服器發生錯誤，請稍後再試", body["message"]!.GetValue<string>());
            // 通用泛化訊息:例外細節（測試腳本用的字樣）不得外洩。
            Assert.DoesNotContain("持久化失敗", body.ToJsonString());
        }
        finally
        {
            FakeConversationStore.ThrowOnAdd = false;
        }
    }

    // A-16:串流式持久化失敗只記 warning——chunks 照常全數送達、無 event:error 幀、mem0 remember 仍執行。
    // 與 A-15 合為決策表兩半:同一種下游失敗,阻塞/串流故意給出不同的對外行為。
    [Fact]
    public async Task Stream_DeliversChunksNormally_NoErrorFrame_WhenPersistenceFails()
    {
        FakeConversationStore.ThrowOnAdd = true;
        var rememberedBefore = FakeMem0Client.Remembered.Count;
        try
        {
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "嗨" });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var raw = await resp.Content.ReadAsStringAsync();
            Assert.Equal("data:你好\n\ndata:世界\n\n", raw);
            Assert.DoesNotContain("event:error", raw);
            Assert.Equal(rememberedBefore + 1, FakeMem0Client.Remembered.Count);
        }
        finally
        {
            FakeConversationStore.ThrowOnAdd = false;
        }
    }

    // A-22:串流 chunk 含換行 → raw bytes 逐一驗每項契約(冒號後無空格、空行結尾、單一 chunk 拆多行 data:)。
    [Fact]
    public async Task Stream_ChunkWithNewline_SplitsIntoMultipleDataLines()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "多行" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        // 含 \n 的單一 chunk 拆成同一 event 內兩個 data: 行(冒號後無空格),event 以空行結尾;逐 byte 全等,不留其他 frame。
        var expected = System.Text.Encoding.UTF8.GetBytes("data:甲\ndata:乙\n\n");
        Assert.Equal(expected, bytes);
    }

    // M5 + A-24:空字串與全空白都是 NotBlank 該擋的等價類,
    // 阻塞與串流兩條路徑皆 400 + ApiError envelope 六鍵齊全;串流端點在寫任何 SSE bytes 之前就回 JSON。
    [Theory]
    [InlineData("/api/chat", "")]
    [InlineData("/api/chat", "   ")]
    [InlineData("/api/chat/stream", "")]
    [InlineData("/api/chat/stream", "   ")]
    public async Task ChatAndStream_Return400_WithFullApiErrorShape_WhenMessageBlank(string path, string message)
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(path, new { message });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType!.MediaType);
        var body = await resp.ReadJsonAsync();
        body.AssertApiError(400, "validation_failed");
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("message 不可為空", body["fieldErrors"]!["message"]!.GetValue<string>());
    }

    // message 的另一個界:StringLength(4000)。上面的 NotBlank 只釘住下界,這裡補 on-point(4000 受理)
    // 與 off-point(4001 → 400 + fieldErrors.message)——用「安全內部」的短字串測不出數字打錯。
    [Fact]
    public async Task Chat_MessageLengthBoundary_Accepts4000_Rejects4001()
    {
        var client = _factory.CreateClient();

        var atLimit = await client.PostAsJsonAsync("/api/chat", new { message = new string('a', 4000) });
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);

        var overLimit = await client.PostAsJsonAsync("/api/chat", new { message = new string('a', 4001) });

        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        var body = await overLimit.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("message 長度不可超過 4000 字", body["fieldErrors"]!["message"]!.GetValue<string>());
    }

    // 兩個選填欄位共用 StringLength(128) 的同一等價類與邊界:on-point 受理、off-point 回對應的 fieldErrors key。
    [Theory]
    [InlineData("userId")]
    [InlineData("conversationId")]
    public async Task Chat_OptionalIdLengthBoundary_Accepts128_Rejects129(string field)
    {
        var client = _factory.CreateClient();

        var atLimit = await client.PostAsJsonAsync(
            "/api/chat",
            new Dictionary<string, object> { ["message"] = "你好", [field] = new string('a', 128) });
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);

        var overLimit = await client.PostAsJsonAsync(
            "/api/chat",
            new Dictionary<string, object> { ["message"] = "你好", [field] = new string('a', 129) });

        Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        var body = await overLimit.ReadJsonAsync();
        Assert.Equal($"{field} 長度不可超過 128 字", body["fieldErrors"]![field]!.GetValue<string>());
    }

    // ---- 路由目錄:帶有效 JWT 的聊天以工具目錄做路由,匿名不路由(工作流需要租戶身分) ----
    // 新流程:工具改以「路由目錄」文字經路由呼叫傳入,故斷言 LastRoutingCatalog 而非原生 tools 引數。

    /// <summary>路由目錄中的工具行數(排除路由指令段)。目錄格式為「指令\n\n名稱: 說明(每行一支)」。</summary>
    private static int ToolLineCount(string catalog)
        => catalog.Split("\n\n")[^1].Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    // 內建(builtin)+ 自訂(custom)+ 一顆 ADMIN 限定,鏡射 workflow GET /skills 的真實回應形狀。
    private const string BuiltinCatalog = """
    [
      { "name":"kb-query", "description":"可稽核的知識查詢", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } },
      { "name":"rag-qa", "description":"一般文件知識庫問答", "required_role":"USER", "source":"builtin",
        "input_schema": { "question": { "type":"str", "required":true } } },
      { "name":"summarize", "description":"文字摘要", "required_role":"USER", "source":"builtin",
        "input_schema": { "text": { "type":"str", "required":true } } },
      { "name":"triage", "description":"問題分流", "required_role":"USER", "source":"builtin",
        "input_schema": { "question": { "type":"str", "required":true } } },
      { "name":"tenant-a-private-search", "description":"自訂檢索", "required_role":"USER", "source":"custom",
        "input_schema": { "question_text": { "type":"str", "required":true } } },
      { "name":"analyze-report", "description":"分析報告", "required_role":"ADMIN", "source":"builtin",
        "input_schema": { "topic": { "type":"str", "required":true } } }
    ]
    """;

    // W1:帶有效 JWT → JWT role claim → UserContext → 目錄過濾的 DI 全鏈(Web 層特有的接線)。
    // builtin 與 custom 都進路由目錄、ADMIN 限定的不進,且路由目錄「恰等於」動態 Skill 目錄
    // (行數精確,沒有殘留靜態工具混入)。ADMIN 拿得到 analyze-report 是同一段程式碼、只差 role 字串,
    // 已在 Service 層兩處覆蓋,不在 Web 層重複。
    [Fact]
    public async Task Chat_WithBearer_EnablesSkillTools_BuiltinAndCustom_AdminOnlyFiltered()
    {
        FakeWorkflowEngineClient.CatalogOverride = System.Text.Json.JsonDocument.Parse(BuiltinCatalog).RootElement.Clone();
        try
        {
            var agent = (FakeLlmAgent)_factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();
            agent.Reset();
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await client.PostAsJsonAsync("/api/chat", new { message = "文件裡有什麼?" });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var catalog = agent.LastRoutingCatalog!;
            Assert.Equal(5, ToolLineCount(catalog));
            Assert.Contains("kb-query", catalog);
            Assert.Contains("tenant-a-private-search", catalog);
            Assert.DoesNotContain("analyze-report", catalog);
        }
        finally
        {
            FakeWorkflowEngineClient.CatalogOverride = null;
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
        // 匿名不路由:沒有路由目錄。P2:純聊天改跑共用的 hosted agent(不再共用 ILlmAgent),
        // 故匿名這輪路由專用的 ILlmAgent 完全不會被呼叫(比舊斷言「恰一次純聊天呼叫」更直接地
        // 證明「匿名不路由」——ILlmAgent 現在只服務路由/摘要,呼叫次數為 0 才是正確語意)。
        Assert.Null(agent.LastRoutingCatalog);
        Assert.Equal(0, agent.CompleteCallCount);
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
    public async Task HistoryPage_Anonymous_ReturnsEmptyCamelCaseEnvelope()
    {
        var response = await _factory.CreateClient().GetAsync("/api/chat/history/page?before=bad");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Empty(body["items"]!.AsArray());
        Assert.Null(body["nextCursor"]);
        Assert.False(body["hasMore"]!.GetValue<bool>());
    }

    [Fact]
    public async Task HistoryPage_Authenticated_FirstAndNextPageUseEnvelope()
    {
        var client = _factory.CreateClient().WithToken(
            _factory.IssueToken(username: "page-web-user", tenantCode: "demo-a"));
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/chat", new { message = $"m{i}" });
            ids.Add((await response.ReadJsonAsync())["id"]!.GetValue<long>());
        }

        var first = await (await client.GetAsync("/api/chat/history/page?limit=2")).ReadJsonAsync();
        Assert.Equal(ids.TakeLast(2).Reverse(), first["items"]!.AsArray().Select(x => x!["id"]!.GetValue<long>()));
        Assert.True(first["hasMore"]!.GetValue<bool>());
        var cursor = first["nextCursor"]!.GetValue<string>();

        var next = await (await client.GetAsync(
            "/api/chat/history/page?limit=2&before=" + Uri.EscapeDataString(cursor))).ReadJsonAsync();
        Assert.Equal(new[] { ids[0] }, next["items"]!.AsArray().Select(x => x!["id"]!.GetValue<long>()));
        Assert.False(next["hasMore"]!.GetValue<bool>());
        Assert.Null(next["nextCursor"]);
    }

    [Fact]
    public async Task HistoryPage_MalformedCursor_ReturnsStableApiError400()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var response = await client.GetAsync("/api/chat/history/page?before=bad");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("validation_failed", body["code"]!.GetValue<string>());
        Assert.Equal("聊天歷史游標無效", body["message"]!.GetValue<string>());
        Assert.NotNull(body["correlationId"]);
        Assert.NotNull(body["fieldErrors"]);
    }

    [Fact]
    public async Task HistoryPage_LimitAbove100_ReturnsValidationApiError()
    {
        var response = await _factory.CreateClient().GetAsync("/api/chat/history/page?limit=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
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

    // SetAuthInvalidHeaderIfNeeded 由兩個 action 各自呼叫,故三種認證狀態要在兩個端點都成對驗:
    // 串流端點的「有效 JWT」與「完全匿名」兩格補上(header 必須不存在),與上面無效 token 那格合成決策表。
    [Theory]
    [InlineData(true)]   // 有效 JWT
    [InlineData(false)]  // 真匿名(完全沒帶 Authorization)
    public async Task Stream_WithValidTokenOrAnonymous_HasNoAuthInvalidHeader(bool withToken)
    {
        var client = withToken
            ? _factory.CreateClient().WithToken(_factory.IssueToken())
            : _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "嗨" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(resp.Headers.Contains("X-Auth-Invalid"));
    }

    // A-21(b):兩個租戶各有歷史時,租戶 A 的 JWT 只讀得到租戶 A 自己的紀錄,讀不到租戶 B 的——
    // 驗內容(id 是否出現),不是驗 nullity(見 04-acceptance-test.md §1.2 的隔離斷言原則)。
    [Fact]
    public async Task History_JwtTenant_ReturnsOwnRecords_NotOtherTenants()
    {
        var tenantAClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "hist-a", role: "USER", tenantCode: "demo-a"));
        var tenantBClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "hist-b", role: "USER", tenantCode: "demo-b"));

        var postA = await tenantAClient.PostAsJsonAsync("/api/chat", new { message = "租戶 A 的訊息" });
        var idA = (await postA.ReadJsonAsync())["id"]!.GetValue<long>();

        var postB = await tenantBClient.PostAsJsonAsync("/api/chat", new { message = "租戶 B 的訊息" });
        var idB = (await postB.ReadJsonAsync())["id"]!.GetValue<long>();

        var historyResp = await tenantAClient.GetAsync("/api/chat/history");

        Assert.Equal(HttpStatusCode.OK, historyResp.StatusCode);
        var ids = (await historyResp.ReadJsonAsync()).AsArray()
            .Select(n => n!["id"]!.GetValue<long>())
            .ToList();

        // hist-a 是本案專屬身分 → 它的歷史「恰好」只有自己那一筆(精確集合,不是「有包含」)。
        Assert.Equal(new[] { idA }, ids);
        Assert.DoesNotContain(idB, ids);
    }

    // ---- D6:AgentChatRoutingAgent 是無條件掛在兩條 pipeline 內的,旗標關閉只讓 runtime 提早回 null。 ----
    // 因此它在呼叫 runtime「之前」讀的兩個 header(Idempotency-Key / X-Orchestrator-Id)即使在
    // AGENT_CHAT_ENABLED=false 時也會驗證並拋例外 —— 這裡釘住那個例外對外變成什麼。

    private static HttpRequestMessage ChatRequest(string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { message = "你好" }),
        };
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    [Theory]
    [InlineData("one", "two", "Idempotency-Key must contain exactly one value")]  // 重複 header
    [InlineData(null, null, "Idempotency-Key is invalid")]                        // 513 字元(off-point)
    public async Task Chat_AmbiguousOrOversizedIdempotencyKey_Returns400_EvenWhenAgentChatDisabled(
        string? first, string? second, string expectedMessage)
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        using var request = first is null
            ? ChatRequest("/api/chat", ("Idempotency-Key", new string('x', 513)))
            : ChatRequest("/api/chat", ("Idempotency-Key", first), ("Idempotency-Key", second!));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        body.AssertApiError(400, "validation_failed");
        Assert.Equal(expectedMessage, body["message"]!.GetValue<string>());
    }

    // MaxLogicalAttemptIdLength = 512 的 on-point:剛好 512 字元是合法值、照常放行(不是 400),
    // 與上面 513 的 off-point 合成邊界對(只驗超界那半邊抓不到把 512 誤寫成 511 的錯)。
    [Fact]
    public async Task Chat_IdempotencyKeyAtMaxLength_IsAccepted()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        using var request = ChatRequest("/api/chat", ("Idempotency-Key", new string('x', 512)));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("測試回覆", (await resp.ReadJsonAsync())["reply"]!.GetValue<string>());
    }

    // 同一個壞 header 在串流端點刻意是另一個結果:ChatController 的 try/catch 已接管整個 await foreach,
    // 所以對外是 200 + event:error 終止 frame(不是 400),且一個 data: chunk 都不會送出。
    [Fact]
    public async Task Stream_OversizedIdempotencyKey_EmitsErrorFrameOnly_EvenWhenAgentChatDisabled()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        using var request = ChatRequest("/api/chat/stream", ("Idempotency-Key", new string('x', 513)));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            "event:error\ndata:回覆過程發生錯誤，請稍後再試\n\n",
            await resp.Content.ReadAsStringAsync());
    }

    // 同一組壞 header 的另外兩格(重複 Idempotency-Key、格式錯誤的 X-Orchestrator-Id)在串流端點也必須
    // 是 200 + 只有一個 event:error 終止 frame:任何「提早 return 而繞過 try/catch」的回歸會在這裡露餡。
    [Theory]
    [InlineData("Idempotency-Key", "one", "two")]          // 重複 header
    [InlineData("X-Orchestrator-Id", "not-a-guid", null)]  // 格式錯誤
    public async Task Stream_AmbiguousKeyOrMalformedOrchestratorId_EmitsErrorFrameOnly(
        string name, string first, string? second)
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        using var request = second is null
            ? ChatRequest("/api/chat/stream", (name, first))
            : ChatRequest("/api/chat/stream", (name, first), (name, second));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(
            "event:error\ndata:回覆過程發生錯誤，請稍後再試\n\n",
            await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_MalformedOrchestratorIdHeader_Returns400()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        using var request = ChatRequest("/api/chat", ("X-Orchestrator-Id", "not-a-guid"));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        body.AssertApiError(400, "validation_failed");
        Assert.Equal("orchestratorId 格式錯誤", body["message"]!.GetValue<string>());
    }

    // D6 鐵律的對外那半邊:格式正確但 canary 不可用(旗標關閉/租戶不在白名單)時,明確指定的
    // Orchestrator 必須 fail-closed —— AgentChatRuntime 拋 WorkflowNotFoundException,對外恰為 404
    // (絕不悄悄退回 legacy 回 200)。上面那格只驗了格式錯誤的 400,兩格都要有才算把決策表收完。
    [Fact]
    public async Task Chat_WellFormedOrchestratorIdHeader_Returns404_WhenAgentChatUnavailable()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        using var request = ChatRequest(
            "/api/chat", ("X-Orchestrator-Id", Guid.NewGuid().ToString("D")));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        body.AssertApiError(404, "not_found");
        Assert.Equal("Orchestrator is unavailable", body["message"]!.GetValue<string>());
    }

    // ---- D6 canary 打開時的 Web 層 happy path(先前整層空白) ----

    /// <summary>只回固定答案的 IAgentChatRuntime(真實實作的選擇閘另在 AgentChatRuntimeTests 驗);
    /// 這裡要證明的是「Root Orchestrator 的答案照樣走完 Web 層的回應格式與持久化」。</summary>
    private sealed class StubAgentChatRuntime : Platform.Service.Abstractions.IAgentChatRuntime
    {
        public string? Reply { get; init; }

        public List<(string Message, string ConversationId, Guid? OrchestratorId)> Calls { get; } = new();

        public Task<Microsoft.Agents.AI.AgentResponse?> RunAsync(
            string message, string conversationId, Guid? requestedOrchestratorId,
            Platform.Service.Abstractions.IChatIdentityAccessor identity,
            string? logicalAttemptId = null, CancellationToken ct = default)
        {
            Calls.Add((message, conversationId, requestedOrchestratorId));
            return Task.FromResult<Microsoft.Agents.AI.AgentResponse?>(Reply is null
                ? null
                : new Microsoft.Agents.AI.AgentResponse(
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, Reply)));
        }
    }

    private static HttpClient AgentChatClient(TestWebAppFactory factory, StubAgentChatRuntime runtime) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Platform.Service.Abstractions.IAgentChatRuntime>();
            services.AddSingleton<Platform.Service.Abstractions.IAgentChatRuntime>(runtime);
        })).CreateClient().WithToken(factory.IssueToken());

    [Fact]
    public async Task AgentChatEnabled_Chat_ReturnsOrchestratorAnswer_AndPersistsTurn()
    {
        const string answer = "Root Orchestrator 的阻塞答案";
        var runtime = new StubAgentChatRuntime { Reply = answer };
        await using var factory = new TestWebAppFactory(
            agentChatEnabled: true, agentChatTenantAllowlist: "demo-a");
        var client = AgentChatClient(factory, runtime);

        var resp = await client.PostAsJsonAsync("/api/chat", new { message = "這季毛利率多少?" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(answer, body["reply"]!.GetValue<string>());
        Assert.True(body["id"]!.GetValue<long>() > 0);   // 短路輪照樣持久化,拿得到 backend id

        // 短路層收到的是伺服器推導出來的 conversationId(body 沒帶 → 退回 {tenant}:{user}),不是 wire 值。
        var call = Assert.Single(runtime.Calls);
        Assert.Equal("這季毛利率多少?", call.Message);
        Assert.Equal("demo-a:user-a", call.ConversationId);
        Assert.Null(call.OrchestratorId);

        Assert.Single(FakeConversationStore.Saved, s => s.Response.Reply == answer);
    }

    [Fact]
    public async Task AgentChatEnabled_Stream_WritesOrchestratorAnswerAsSseFrame()
    {
        const string answer = "Root Orchestrator 的串流答案";
        var runtime = new StubAgentChatRuntime { Reply = answer };
        await using var factory = new TestWebAppFactory(
            agentChatEnabled: true, agentChatTenantAllowlist: "demo-a");
        var client = AgentChatClient(factory, runtime);

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = "這季毛利率多少?" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // D6 的答案也走同一套無空格 data: wire contract,且沒有 event:error。
        Assert.Equal($"data:{answer}\n\n", await resp.Content.ReadAsStringAsync());
        Assert.Single(FakeConversationStore.Saved, s => s.Response.Reply == answer);
    }

    // 其餘 Orchestrator 測試都只走 X-Orchestrator-Id header,但 body 的 orchestratorId 是另一個、
    // 而且優先度更高的輸入面:ChatService 把它寫進 HttpContext.Items,HttpChatIdentityAccessor 先讀
    // Items、沒有才退回 header。同時帶不同值,證明短路層收到的是 body 值、header 被忽略。
    [Fact]
    public async Task AgentChatEnabled_BodyOrchestratorId_TakesPrecedenceOverHeader()
    {
        var bodyId = Guid.NewGuid();
        var runtime = new StubAgentChatRuntime { Reply = "指定 Orchestrator 的答案" };
        await using var factory = new TestWebAppFactory(
            agentChatEnabled: true, agentChatTenantAllowlist: "demo-a");
        var client = AgentChatClient(factory, runtime);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message = "這季毛利率多少?", orchestratorId = bodyId }),
        };
        request.Headers.TryAddWithoutValidation("X-Orchestrator-Id", Guid.NewGuid().ToString("D"));

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(runtime.Calls);
        Assert.Equal(bodyId, call.OrchestratorId);
    }

    // AGENT_CHAT_TENANT_ALLOWLIST 的解析是這道安全閘的輸入,先前零覆蓋:逗號分隔 + trim,
    // 空項/控制字元/超長(128 為上限)一律不得進入白名單。
    [Fact]
    public async Task AgentChatTenantAllowlist_TrimsEntries_RejectsEmptyOversizedAndControlChars()
    {
        var onPoint = new string('t', 128);
        var offPoint = new string('t', 129);
        await using var factory = new TestWebAppFactory(
            agentChatEnabled: true,
            agentChatTenantAllowlist: $" demo-a , ,demo-b ,{onPoint},{offPoint},badctrl");

        var options = factory.Services.GetRequiredService<Platform.Service.Options.AgentChatOptions>();

        Assert.True(options.IsCanaryTenant("demo-a"));          // 前後空白被 trim 掉
        Assert.True(options.IsCanaryTenant("demo-b"));
        Assert.True(options.IsCanaryTenant(onPoint));           // 128 on-point:收
        Assert.False(options.IsCanaryTenant(offPoint));         // 129 off-point:丟
        Assert.False(options.IsCanaryTenant("badctrl"));  // 控制字元:丟
        Assert.False(options.IsCanaryTenant(""));               // 空項不得成為成員
        Assert.False(options.IsCanaryTenant(" demo-a "));       // 存的是 trim 後的值
    }
}
