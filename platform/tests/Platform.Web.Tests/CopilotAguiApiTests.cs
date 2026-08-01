using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Web.Tests;

/// <summary>
/// AG-UI(CopilotKit 操作助理)端點測試。底層 IChatClient 由 TestWebAppFactory 換成 FakeChatClient,
/// 所以不打真的 LiteLLM,但仍走真正的 MapAGUI 請求解析、session store/isolation 與 SSE 序列化。
/// P1 起端點改為 RequireAuthorization():無/壞 JWT 一律 401(相對現行行為唯一的對外變更,02-spec §2.1)。
///
/// P4 起與 <see cref="ChatApiTests"/> 同一個 "EngineCalls" collection(序列化執行):兩者都會改動
/// <see cref="FakeWorkflowEngineClient.CatalogOverride"/>/<see cref="FakeWorkflowEngineClient.SkillInvokes"/> 這些
/// 靜態欄位(跨 TestWebAppFactory 實例共用),平行執行會產生競態。
/// </summary>
[Collection("EngineCalls")]
public sealed class CopilotAguiApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public CopilotAguiApiTests(TestWebAppFactory factory) => _factory = factory;

    // id 每次呼叫都給一個新的 GUID(不再固定 "m1")——固定 id 會讓「單訊息 client 同一 threadId 連發
    // 兩輪、內容不同」被 P2 的去重層誤判成同一則訊息而濾掉,見 B-P2-01 的分析與下面 UserMsg 系列輔助方法。
    private static object RunInputFor(
        string threadId, string message, object? state = null, object? forwardedProps = null, object[]? tools = null) => new
    {
        threadId,
        runId = "r1",
        state = state ?? new { },
        messages = new[] { new { id = Guid.NewGuid().ToString("N"), role = "user", content = message } },
        tools = tools ?? Array.Empty<object>(),
        context = Array.Empty<object>(),
        forwardedProps = forwardedProps ?? new { },
    };

    // AG-UI wire tool 宣告(鏡射 AGUITool:name/description/parameters,JSON Schema 物件)。
    private static object ToolDef(string name, string description, object parameters) =>
        new { name, description, parameters };

    private static readonly object[] SwitchViewClientTools =
    {
        ToolDef(
            "switchView", "切換前端視圖",
            new { type = "object", properties = new { view = new { type = "string" } }, required = new[] { "view" } }),
    };

    // 模擬真實 @ag-ui/client:呼叫端自行組出「本輪要送出的完整 messages 陣列」(client 維護的全歷史)
    // 直接傳進來,而非只送單一新訊息——B-P2-04 的盲區正是舊版 RunInputFor 從未測過這個形狀。
    private static object RunInputWithMessages(string threadId, IEnumerable<object> messages, object[]? tools = null) => new
    {
        threadId,
        runId = "r1",
        state = new { },
        messages = messages.ToArray(),
        tools = tools ?? Array.Empty<object>(),
        context = Array.Empty<object>(),
        forwardedProps = new { },
    };

    private static object UserMsg(string id, string content) => new { id, role = "user", content };

    private static object AssistantMsg(string id, string content) => new { id, role = "assistant", content };

    // Mirrors the client-side history that @ag-ui/client sends back after it has received a TOOL_CALL_*
    // event. Keeping the parent assistant id is essential: non-text messages deliberately do not use the
    // text fallback in AguiWireDedupAgent, because doing so could split a tool call/result exchange.
    // content 預設為空字串(真實 client 的 tool-call 訊息通常沒有文字);需要「文字與既存回覆相同、
    // 但形狀是 tool call」這格等價類時才傳入(見 …_NotCollapsedByTextFallback)。
    private static object AssistantToolCallMsg(
        string id, string callId, string name, string arguments, string content = "") => new
    {
        id,
        role = "assistant",
        content,
        toolCalls = new[] { new { id = callId, type = "function", function = new { name, arguments } } },
    };

    private static object ToolResultMsg(string callId, string content) => new
    {
        id = callId,
        role = "tool",
        toolCallId = callId,
        content,
    };

    /// <summary>從 AG-UI SSE 回應擷取 assistant 訊息的伺服器端 messageId 與完整文字內容(串接所有
    /// TEXT_MESSAGE_CONTENT 的 delta)——模擬真實 client 收到回覆後,會把這則訊息連同伺服器給的 id
    /// 存進自己的本地歷史,下一輪原樣回傳(見 AGUIChatMessageExtensions.AsChatMessages 反編譯)。</summary>
    private static (string MessageId, string Text) ExtractAssistantMessage(string rawSse)
    {
        var frames = rawSse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Replace("data: ", string.Empty))
            .Select(json => JsonDocument.Parse(json))
            .ToList();
        var start = frames.First(f => f.RootElement.GetProperty("type").GetString() == "TEXT_MESSAGE_START");
        var messageId = start.RootElement.GetProperty("messageId").GetString()!;
        var text = string.Concat(frames
            .Where(f => f.RootElement.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
            .Select(f => f.RootElement.GetProperty("delta").GetString()));
        return (messageId, text);
    }

    private static async Task<HttpResponseMessage> SendAguiAsync(HttpClient client, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/copilot/agui")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    private FakeChatClient ChatClient => (FakeChatClient)_factory.Services.GetRequiredService<IChatClient>();

    // ---- B-P1-01/02/03:認證(未帶 JWT / 無效 JWT → 401;有效 JWT → 200 + 既有事件序列) ----

    // B-P1-01:完全不帶 Authorization → 401,不進入 agent、不建立 session。
    [Fact]
    public async Task Agui_NoAuthorizationHeader_Returns401()
    {
        var client = _factory.CreateClient();

        var resp = await SendAguiAsync(client, RunInputFor("b-p1-01", "你好"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // B-P1-02:過期 / 簽章竄改 / 格式錯誤的 token 同屬「憑證無效」等價類,三者皆 401(鏡射 SecurityIntegrationTests 既有慣例)。
    [Theory]
    [InlineData("expired")]
    [InlineData("tampered")]
    [InlineData("malformed")]
    public async Task Agui_InvalidToken_Returns401(string kind)
    {
        var now = DateTime.UtcNow;
        var token = kind switch
        {
            "expired" => TestTokens.Mint(notBefore: now.AddMinutes(-10), expires: now.AddMinutes(-5)),
            "tampered" => TestTokens.Mint() + "x",
            "malformed" => "not-a-jwt-at-all",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var client = _factory.CreateClient().WithToken(token);

        var resp = await SendAguiAsync(client, RunInputFor("b-p1-02", "你好"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // JWT 簽章有效不代表可作為聊天 identity。缺 subject 或 tenantCode 時 provider 必回 null，讓
    // Strict session store 在建立 session 前 fail-closed；不得把空字串組成共享 key，例如 ":"。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Agui_ValidJwtMissingRequiredIdentityClaim_Returns500BeforeAgentOrSession(bool omitSubject)
    {
        var client = _factory.CreateClient().WithToken(TestTokens.MintMissingChatIdentityClaim(omitSubject));
        var runsBefore = ChatClient.Runs.Count;

        var resp = await SendAguiAsync(client, RunInputFor($"missing-claim-{omitSubject}", "不應進入 agent"));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(runsBefore, ChatClient.Runs.Count);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(500, body["status"]!.GetValue<int>());
        Assert.Equal("伺服器發生錯誤，請稍後再試", body["message"]!.GetValue<string>());
    }

    // 同一道 fail-closed 的另一格:claims 齊全但身分本身含隔離鍵分隔字元 ':' —— tenant "demo" + user "a:b"
    // 與 tenant "demo:a" + user "b" 會共用同一把 isolation key。AG-UI 走 UserContext.IsolationKey,
    // 必須在建立 session 前拒絕,不得讓兩個不同使用者共用同一個短期記憶命名空間。
    [Theory]
    [InlineData("a:b", "demo")]
    [InlineData("b", "demo:a")]
    public async Task Agui_IdentityContainingIsolationSeparator_Returns500BeforeAgentOrSession(
        string username, string tenantCode)
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username, "USER", tenantCode));
        var runsBefore = ChatClient.Runs.Count;

        var resp = await SendAguiAsync(client, RunInputFor("colon-identity", "不應進入 agent"));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(runsBefore, ChatClient.Runs.Count);
        Assert.Equal(500, (await resp.ReadJsonAsync())["status"]!.GetValue<int>());
    }

    // B-P1-03(承接現行 A-23,帶 JWT):有效 JWT → 200 + text/event-stream + 協定標準 "data: "(有空格)
    // + 既有事件序列。這是相對匿名姿態唯一改變的前置條件,事件序列本身不變。
    [Fact]
    public async Task Agui_ValidJwt_EmitsSpacedDataFrames_InExpectedEventOrder()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await SendAguiAsync(client, RunInputFor("b-p1-03", "你好"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        var raw = await resp.Content.ReadAsStringAsync();

        // 每個 frame 是 "data: <json>"(冒號後恰一個空格),與 /api/chat/stream 的 "data:<value>" 刻意不同。
        Assert.Contains("data: {", raw);
        Assert.DoesNotContain("\ndata:{", raw);

        var frames = raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var types = frames
            .Select(f => f.Replace("data: ", string.Empty))
            .Select(json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString())
            .ToList();

        Assert.Equal(
            new[]
            {
                "RUN_STARTED",
                "TEXT_MESSAGE_START",
                "TEXT_MESSAGE_CONTENT",
                "TEXT_MESSAGE_CONTENT",
                "TEXT_MESSAGE_END",
                "RUN_FINISHED",
            },
            types);
    }

    // ---- B-P1-04:租戶隔離(本案最重要)——同一 threadId,不同租戶,內容不得互見。驗內容,禁用 nullity。 ----

    [Fact]
    [Trait("EvidenceGate", "E-02")]
    public async Task Agui_TenantIsolation_SameThreadId_TenantB_CannotSeeTenantASecret()
    {
        const string threadId = "b-p1-04";
        var tenantAClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "user-a", role: "USER", tenantCode: "demo-a"));
        var tenantBClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "user-b", role: "USER", tenantCode: "demo-b"));

        var respA = await SendAguiAsync(tenantAClient, RunInputFor(threadId, "我的密語是 XYZZY"));
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        await respA.Content.ReadAsStringAsync();

        var respB = await SendAguiAsync(tenantBClient, RunInputFor(threadId, "我的密語是什麼"));
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var rawB = await respB.Content.ReadAsStringAsync();

        // 送進模型的 messages(含框架的 session 歷史注入)不含租戶 A 的密語,回覆亦不含。
        var lastRunTexts = ChatClient.Runs[^1].Select(m => m.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(lastRunTexts, t => t.Contains("XYZZY", StringComparison.Ordinal));
        Assert.DoesNotContain("XYZZY", rawB);
    }

    // B-P1-05:同租戶不同使用者、同一 threadId——P2 已將 isolation key 由「僅租戶」擴為「租戶:使用者」
    // (JwtTenantIsolationKeyProvider,見該檔案 P2 註記),兩者皆只源自 JWT claims、fail-closed 語意不變,
    // 只是粒度變細至使用者層級,因此同租戶不同使用者以同一 threadId 提問不再互見。
    [Fact]
    [Trait("EvidenceGate", "E-02")]
    public async Task Agui_SameTenantDifferentUsers_SameThreadId_UserBCannotSeeUserASecret()
    {
        const string threadId = "b-p1-05";
        var userAClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "user-a", role: "USER", tenantCode: "demo-a"));
        var userBClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "user-b", role: "USER", tenantCode: "demo-a"));

        var respA = await SendAguiAsync(userAClient, RunInputFor(threadId, "我的密語是 XYZZY"));
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        // 必須先把 A 的串流回應完整讀完(而不僅是拿到 headers)才能發 B 的請求——否則 A 的背景寫入
        // (含 session save)可能仍在進行中,B 讀 ChatClient.Runs[^1] 時會產生時序競態(與 B-P1-04 同一慣例)。
        await respA.Content.ReadAsStringAsync();

        var respB = await SendAguiAsync(userBClient, RunInputFor(threadId, "我的密語是什麼"));
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var rawB = await respB.Content.ReadAsStringAsync();

        var lastRunTexts = ChatClient.Runs[^1].Select(m => m.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(lastRunTexts, t => t.Contains("XYZZY", StringComparison.Ordinal));
        Assert.DoesNotContain("XYZZY", rawB);
    }

    // ---- B-P2-01/04:P2 記憶收斂 —— 同一 threadId 連續多輪,第二輪(以後)記得先前輪次,且訊息不重複累加 ----
    // 「第二輪記得第一輪」與「單訊息 client 三輪各出現一次」都是下面 full-array 版本的必要推論
    // (三輪後每則 user 恰出現一次且總數恰為 5),且那版才是真實 @ag-ui/client 的形狀,故只保留它。

    // B-P2-04(full-array):真實 @ag-ui/client 每輪重送「完整」訊息陣列(client 端維護的
    // 全歷史),assistant 訊息回填伺服器上一輪實際回傳的 messageId(id 對得齊)。三輪下來,FakeChatClient
    // 收到的最後一輪 messages 中每則 user 訊息仍恰出現一次,且總數只隨輪次線性成長(1→3→5),不因
    // wire 重送 + session 疊加而複合暴增(修復前反編譯/Langfuse trace 證實會長到 1→5→11)。
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Agui_SameThreadId_ThreeRounds_FullArrayResend_AlignedAssistantId_UserMessagesAppearExactlyOnce()
    {
        const string threadId = "b-p2-04-full-array-aligned";
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        var wireHistory = new List<object>();
        var userTexts = new[] { "第一輪", "第二輪", "第三輪" };

        foreach (var text in userTexts)
        {
            wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), text));

            var resp = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var raw = await resp.Content.ReadAsStringAsync();

            var (assistantId, assistantText) = ExtractAssistantMessage(raw);
            wireHistory.Add(AssistantMsg(assistantId, assistantText));
        }

        var lastRunTexts = ChatClient.Runs[^1].Select(m => m.Text ?? string.Empty).ToList();
        foreach (var text in userTexts)
        {
            Assert.Single(lastRunTexts, t => t == text);
        }
        // 修復前第三輪會是 11 則(含重複);修復後應恰為 5 則(3 則 user + 2 則先前 assistant 回覆)。
        Assert.Equal(5, lastRunTexts.Count);
    }

    // B-P2-04 的另一種真實情境(e2e 觀察到兩種都會發生):前端 echo 回來的 assistant messageId
    // 與伺服器當初送出的不一致(例如前端自行重新產生 id)。純文字 assistant 以 role+完整文字作保守
    // fallback 去重；user 一律只按 id，比對規則不會吞掉合法的相同內容重複發話。
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Agui_SameThreadId_ThreeRounds_FullArrayResend_MismatchedAssistantId_UserMessagesStillAppearExactlyOnce()
    {
        const string threadId = "b-p2-04-full-array-mismatched";
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        var wireHistory = new List<object>();
        var userTexts = new[] { "第一輪", "第二輪", "第三輪" };

        foreach (var text in userTexts)
        {
            wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), text));

            var resp = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var raw = await resp.Content.ReadAsStringAsync();

            var (_, assistantText) = ExtractAssistantMessage(raw);
            // 刻意不用伺服器回傳的 messageId,模擬前端 id 對不齊的情境。
            wireHistory.Add(AssistantMsg(Guid.NewGuid().ToString("N"), assistantText));
        }

        var lastRunTexts = ChatClient.Runs[^1].Select(m => m.Text ?? string.Empty).ToList();
        foreach (var text in userTexts)
        {
            Assert.Single(lastRunTexts, t => t == text);
        }
        // full-array 重送即使 assistant id 改寫，舊 assistant 內容也恰好各一份：3 user + 2 assistant。
        Assert.Equal(5, lastRunTexts.Count);
        Assert.Equal(2, lastRunTexts.Count(t => t == "你好世界"));
    }

    // 相同文字的 assistant 回覆可以在不同 turn 合法重複。mismatched-ID fallback 必須是 multiset：
    // session 有兩則舊「你好世界」時，wire 的三則同文 assistant 只消耗兩則舊項，第三則必須保留。
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Agui_FullArrayResend_RepeatedAssistantText_ConsumesKnownCopiesButPreservesAdditionalMessage()
    {
        const string threadId = "b-p2-04-assistant-multiset";
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        var wireHistory = new List<object>();

        foreach (var userText in new[] { "第一輪", "第二輪" })
        {
            wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), userText));
            var response = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            var (_, assistantText) = ExtractAssistantMessage(raw);
            wireHistory.Add(AssistantMsg(Guid.NewGuid().ToString("N"), assistantText));
        }

        // 這是另一則合法、內容剛好相同的 assistant message，不是前兩則的 echo。
        wireHistory.Add(AssistantMsg(Guid.NewGuid().ToString("N"), "你好世界"));
        wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), "第三輪"));

        var finalResponse = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
        Assert.Equal(HttpStatusCode.OK, finalResponse.StatusCode);
        await finalResponse.Content.ReadAsStringAsync();

        var lastRunTexts = ChatClient.Runs[^1].Select(message => message.Text ?? string.Empty).ToList();
        Assert.Equal(3, lastRunTexts.Count(text => text == "你好世界"));
        foreach (var userText in new[] { "第一輪", "第二輪", "第三輪" })
        {
            Assert.Single(lastRunTexts, text => text == userText);
        }
    }

    // 文字 fallback 只適用「純文字」assistant 訊息:AguiWireDedupAgent.GetAssistantTextFingerprint 對含
    // function call/result 的訊息一律回 null(拆散工具配對的代價遠大於多送一則重複訊息),所以 id 對不齊的
    // tool-call 形狀 assistant 訊息不會被吞掉,而是原樣多流一則進模型輸入。本案 session 只存過純文字回覆,
    // 且 callId 是伺服器從未發過的值 → 模型輸入裡的 FunctionCallContent 只可能來自這次 wire 重送。
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Agui_FullArrayResend_MismatchedId_ToolCallShapedAssistantMessage_NotCollapsedByTextFallback()
    {
        const string threadId = "b-p2-04-toolcall-mismatched-id";
        const string echoedCallId = "call-never-issued-by-server";
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        var wireHistory = new List<object> { UserMsg(Guid.NewGuid().ToString("N"), "第一輪") };

        var firstResponse = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var (_, assistantText) = ExtractAssistantMessage(await firstResponse.Content.ReadAsStringAsync());

        // 與 session 既存 assistant 回覆同文字、id 又刻意對不齊,唯一差別是它帶了 toolCalls——文字 fallback
        // 若沒有排除非文字內容,這則就會被誤判成「已知的那則回覆」而消失,工具呼叫也跟著不見。
        wireHistory.Add(AssistantToolCallMsg(
            Guid.NewGuid().ToString("N"), echoedCallId, "switchView", "{\"view\":\"documents\"}", assistantText));
        wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), "第二輪"));

        var secondResponse = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        await secondResponse.Content.ReadAsStringAsync();

        var modelInput = ChatClient.Runs[^1];
        var calls = modelInput.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToList();
        Assert.Equal(echoedCallId, Assert.Single(calls).CallId);
        // session 既存 2 則(第一輪 user + assistant)+ 本輪新收 2 則(tool-call 形狀 assistant + 第二輪 user):
        // 對不齊的那則是「多出來的一則」,既沒被吞掉,也沒取代既存那則。
        Assert.Equal(4, modelInput.Count);
        Assert.Single(modelInput, message => (message.Text ?? string.Empty) == "第二輪");
    }

    // 使用者連續兩輪送出「內容相同、id 不同」的訊息(單訊息 client 模式,每輪只送新訊息)——
    // 去重層只依 MessageId 判斷,絕不能因內容相同就誤判成重複而遺失使用者合法的重複發話。
    [Fact]
    public async Task Agui_SameThreadId_TwoRounds_SameContentDifferentIds_BothMessagesPreserved()
    {
        const string threadId = "b-p2-04-same-content-different-ids";
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp1 = await SendAguiAsync(
            client, RunInputWithMessages(threadId, new[] { UserMsg(Guid.NewGuid().ToString("N"), "你好嗎") }));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        await resp1.Content.ReadAsStringAsync();

        var resp2 = await SendAguiAsync(
            client, RunInputWithMessages(threadId, new[] { UserMsg(Guid.NewGuid().ToString("N"), "你好嗎") }));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        await resp2.Content.ReadAsStringAsync();

        var lastRunTexts = ChatClient.Runs[^1].Select(m => m.Text ?? string.Empty).ToList();
        Assert.Equal(2, lastRunTexts.Count(t => t == "你好嗎"));
    }

    // ---- B-P1-06:DI 未註冊 SessionIsolationKeyProvider → Strict=true 仍拋例外(fail-closed),對外 500,不得 200。 ----

    [Fact]
    public async Task Agui_NoSessionIsolationKeyProviderRegistered_ValidJwt_Returns500_FailClosed()
    {
        await using var factory = new TestWebAppFactory(removeSessionIsolationProvider: true);
        var client = factory.CreateClient().WithToken(factory.IssueToken());

        var resp = await SendAguiAsync(client, RunInputFor("b-p1-06", "你好"));

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(4, body.AsObject().Count);
        Assert.NotNull(body["timestamp"]);
        Assert.Equal(500, body["status"]!.GetValue<int>());
        Assert.Equal("伺服器發生錯誤，請稍後再試", body["message"]!.GetValue<string>());
        Assert.NotNull(body["fieldErrors"]);
    }

    // ---- B-P1-07:body 的 threadId/state/forwardedProps 偽造租戶 B 識別,隔離仍只依 JWT。 ----

    [Fact]
    public async Task Agui_ForgedTenantFieldsInBody_IsolationStillFromJwt_NotFromWireFields()
    {
        const string threadId = "b-p1-07";
        var tenantBClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "user-b", role: "USER", tenantCode: "demo-b"));
        await SendAguiAsync(tenantBClient, RunInputFor(threadId, "我的密語是 XYZZY"));

        var tenantAClient = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "user-a", role: "USER", tenantCode: "demo-a"));
        var forgedInput = RunInputFor(
            threadId,
            "我的密語是什麼",
            state: new { tenantCode = "demo-b", userId = "user-b" },
            forwardedProps: new { tenantCode = "demo-b" });

        var respA = await SendAguiAsync(tenantAClient, forgedInput);
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var rawA = await respA.Content.ReadAsStringAsync();

        var lastRunTexts = ChatClient.Runs[^1].Select(m => m.Text ?? string.Empty).ToList();
        Assert.DoesNotContain(lastRunTexts, t => t.Contains("XYZZY", StringComparison.Ordinal));
        Assert.DoesNotContain("XYZZY", rawA);
    }

    // ---- P1 prompt manifest(plans/agent-architecture-improvements/03-…-plan.md §3/§7)----

    // 旗標關閉(TestWebAppFactory 預設不設 PROMPT_ARTIFACTS_ENABLED)時,副駕送進模型的 Instructions 必須
    // 逐位元等於「操作助理 persona + \n + 護欄」——persona 從 ChatOptions 搬到 ChatContextProvider 之後
    // 這條組成沒有任何測試釘住過,golden 就在這裡。兩段字串皆為逐字副本,任一常數被改動本案即失敗。
    [Fact]
    public async Task Agui_PromptArtifactsDisabled_InstructionsArePersonaThenGuard_ByteForByte()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        // 低8 修復:用清單長度增量而非尾端索引斷言,免得共用清單的意外重複呼叫被 [^1] 誤判成綠燈。
        var countBefore = ChatClient.RunOptions.Count;
        var resp = await SendAguiAsync(client, RunInputFor("p1-persona-golden", "你好"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsStringAsync();

        Assert.Equal(countBefore + 1, ChatClient.RunOptions.Count);
        Assert.Equal(
            Platform.Service.PromptComposition.CopilotPersonaDefault + "\n" + Platform.Service.PromptComposition.GuardDefault,
            ChatClient.RunOptions[^1]!.Instructions);
    }

    // ---- P3(copilot-shared-core §4.3/§11 步驟 12):副駕對話會進歷史,mem0 順序 ----

    // B-P3-07:副駕聊一輪 → FakeConversationStore.Saved 出現該輪(key=(tenant,user)),
    // 且 GET /api/chat/history 依同一身分撈得到(02-spec §4.2 刻意決策:副駕與 ChatView 對話共用一份歷史)。
    [Fact]
    public async Task Agui_ChatTurn_PersistsToConversationStore_AppearsInChatHistory()
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken(username: "b-p3-07-user", role: "USER", tenantCode: "demo-a"));

        var resp = await SendAguiAsync(client, RunInputFor("b-p3-07", "副駕你好"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsStringAsync();

        // b-p3-07-user 是本案專屬身分 → 這一輪「恰好」留下一筆,不是「有包含」(共用靜態清單不重置,
        // 模糊比對會被其他測試的殘留餵成假綠)。
        var saved = Assert.Single(
            FakeConversationStore.Saved, s => s.TenantCode == "demo-a" && s.UserId == "b-p3-07-user");
        Assert.Equal("你好世界", saved.Response.Reply);

        var historyResp = await client.GetAsync("/api/chat/history");
        Assert.Equal(HttpStatusCode.OK, historyResp.StatusCode);
        var replies = (await historyResp.ReadJsonAsync()).AsArray()
            .Select(n => n!["reply"]!.GetValue<string>())
            .ToList();
        // 同一身分的歷史恰等於那一輪(副駕與 ChatView 共用同一份歷史)。
        Assert.Equal(new[] { "你好世界" }, replies);
    }

    // B-P3-01:副駕側 mem0 recall 在模型呼叫前、remember 在完整回覆後(含順序斷言)。用專屬的 recording
    // mem0 fake(非共用靜態 FakeMem0Client.Remembered)避免跨測試/平行執行的順序不確定性。
    [Fact]
    public async Task Agui_Mem0RecallBeforeModelCall_RememberAfterFullReply_InOrder()
    {
        var order = new List<string>();
        await using var factory = new TestWebAppFactory(mem0Override: new RecordingMem0Client(order));
        var client = factory.CreateClient().WithToken(factory.IssueToken());

        var resp = await SendAguiAsync(client, RunInputFor("b-p3-01", "你好"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsStringAsync();

        Assert.Equal(new[] { "recall", "remember" }, order);
    }

    /// <summary>只記錄呼叫順序 + remember 收到的完整回覆內容,不共用任何靜態狀態。</summary>
    private sealed class RecordingMem0Client : Platform.Service.Abstractions.IMem0Client
    {
        private readonly List<string> _order;

        public RecordingMem0Client(List<string> order) => _order = order;

        public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
        {
            _order.Add("recall");
            return Task.FromResult(string.Empty);
        }

        public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
        {
            // 完整回覆(FakeChatClient 預設串流兩塊「你好」+「世界」串接後),不是中途片段。
            Assert.Equal("你好世界", aiReply);
            _order.Add("remember");
            return Task.CompletedTask;
        }
    }

    // ---- P3:IMem0Client 擲例外仍是 pipeline-wide best-effort ----

    /// <summary>從 AG-UI SSE 回應擷取每個 frame 的 JsonElement,依序排列(不假設事件數量)。</summary>
    private static List<JsonElement> ExtractFrames(string rawSse) =>
        rawSse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Replace("data: ", string.Empty))
            .Select(json => JsonDocument.Parse(json).RootElement)
            .ToList();

    // RecallAsync 發生在 ChatContextProvider.ProvideAIContextAsync；替換實作拋例外時只略過 long-term
    // context，AG-UI 仍須送出正常完整回覆。
    [Fact]
    public async Task Agui_Mem0RecallThrows_IsBestEffort_EmitsNormalReply()
    {
        await using var factory = new TestWebAppFactory(
            mem0Override: new ThrowingMem0Client(onRecall: new InvalidOperationException("mem0 recall 壞掉")));
        var client = factory.CreateClient().WithToken(factory.IssueToken());

        var resp = await SendAguiAsync(client, RunInputFor("b-p3-mem0-recall-throws", "你好"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();

        // 專屬 factory + 專屬 threadId → 事件序列是確定的:精確比對整串,才抓得到「多冒出一個事件」的迴歸。
        Assert.Equal(NormalReplyEvents, EventTypes(raw));
    }

    // RememberAsync 在完整回覆後執行；失敗只能記 warning，不能將已完成的 turn 轉成 RUN_ERROR。
    [Fact]
    public async Task Agui_Mem0RememberThrows_IsBestEffort_EndsNormally()
    {
        await using var factory = new TestWebAppFactory(
            mem0Override: new ThrowingMem0Client(onRemember: new InvalidOperationException("mem0 remember 壞掉")));
        var client = factory.CreateClient().WithToken(factory.IssueToken());

        var resp = await SendAguiAsync(client, RunInputFor("b-p3-mem0-remember-throws", "你好"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(NormalReplyEvents, EventTypes(await resp.Content.ReadAsStringAsync()));
    }

    /// <summary>一輪正常(兩塊串流)回覆的完整 AG-UI 事件序列。</summary>
    private static readonly string[] NormalReplyEvents =
    {
        "RUN_STARTED",
        "TEXT_MESSAGE_START",
        "TEXT_MESSAGE_CONTENT",
        "TEXT_MESSAGE_CONTENT",
        "TEXT_MESSAGE_END",
        "RUN_FINISHED",
    };

    private static List<string?> EventTypes(string rawSse) =>
        ExtractFrames(rawSse).Select(f => f.GetProperty("type").GetString()).ToList();

    // ---- P4(copilot-shared-core §11 步驟 14):路由共用 —— 副駕取得同批 skill 能力 ----

    // T-P4-1/B-P4-14(補 G2/G3):非空 client tools + 路由 NONE(預設 catalog 無 input_schema,見
    // FakeWorkflowEngineClient 無 CatalogOverride 時的目錄形狀)→ 委派內層,FakeChatClient 腳本化吐出
    // FunctionCallContent,AGUI 編碼層應冒泡成 TOOL_CALL_START(含工具名)→ ARGS(含參數)→ END。
    [Fact]
    public async Task Agui_NonEmptyClientTools_RoutingNone_EmitsToolCallStartArgsEnd()
    {
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await SendAguiAsync(
            client, RunInputFor("t-p4-1", FakeChatClient.ToolCallTrigger, tools: SwitchViewClientTools));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        var frames = ExtractFrames(raw);
        var types = frames.Select(f => f.GetProperty("type").GetString()).ToList();

        Assert.Contains("TOOL_CALL_START", types);
        Assert.Contains("TOOL_CALL_ARGS", types);
        Assert.Contains("TOOL_CALL_END", types);

        var start = frames.First(f => f.GetProperty("type").GetString() == "TOOL_CALL_START");
        Assert.Equal("switchView", start.GetProperty("toolCallName").GetString());

        var args = frames.First(f => f.GetProperty("type").GetString() == "TOOL_CALL_ARGS");
        Assert.Contains("documents", args.GetProperty("delta").GetString());
    }

    // E-03 / C-04 deterministic driver contract: the client must be able to consume the real AG-UI
    // call id, invoke its handler once, and resend a result with that exact id. The next model input is
    // the canonical assertion point: it must contain one, and only one, matched FunctionCall/Result pair.
    // This is intentionally not a response-only check and does not emit a JWT or prompt artifact.
    [Fact]
    [Trait("EvidenceGate", "E-03")]
    public async Task Agui_ClientToolRoundTrip_UsesEmittedCallId_AndModelInputHasExactlyOneMatchedPair()
    {
        const string threadId = "e-03-tool-roundtrip";
        const string toolName = "switchView";
        const string toolArguments = "{\"view\":\"documents\"}";
        const string toolResult = "{\"handled\":true,\"view\":\"documents\"}";
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());
        var wireHistory = new List<object> { UserMsg(Guid.NewGuid().ToString("N"), FakeChatClient.ToolCallTrigger) };

        var initialResponse = await SendAguiAsync(
            client, RunInputWithMessages(threadId, wireHistory, SwitchViewClientTools));
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initialFrames = ExtractFrames(await initialResponse.Content.ReadAsStringAsync());
        var callStart = Assert.Single(initialFrames, frame =>
            frame.GetProperty("type").GetString() == "TOOL_CALL_START");
        var callId = callStart.GetProperty("toolCallId").GetString();
        var parentMessageId = callStart.GetProperty("parentMessageId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(callId));
        Assert.False(string.IsNullOrWhiteSpace(parentMessageId));
        Assert.Equal(toolName, callStart.GetProperty("toolCallName").GetString());

        // This represents the deterministic driver's registered browser-side handler. It only consumes
        // the id issued by the real SSE event; no hard-coded call id can make this test pass.
        var handlerInvocations = 0;
        string HandleSwitchView(string emittedCallId)
        {
            Assert.Equal(callId, emittedCallId);
            handlerInvocations++;
            return toolResult;
        }

        wireHistory.Add(AssistantToolCallMsg(parentMessageId!, callId!, toolName, toolArguments));
        wireHistory.Add(ToolResultMsg(callId!, HandleSwitchView(callId!)));
        wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), "工具已完成，請繼續回答"));

        var followUpResponse = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
        Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);
        await followUpResponse.Content.ReadAsStringAsync();
        Assert.Equal(1, handlerInvocations);

        var modelInput = ChatClient.Runs[^1];
        var calls = modelInput.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToList();
        var results = modelInput.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToList();
        var actualCall = Assert.Single(calls);
        var actualResult = Assert.Single(results);
        Assert.Equal(callId, actualCall.CallId);
        Assert.Equal(callId, actualResult.CallId);
        Assert.Equal(toolName, actualCall.Name);
        Assert.Contains("documents", JsonSerializer.Serialize(actualCall.Arguments));
        Assert.Contains("handled", JsonSerializer.Serialize(actualResult.Result));
    }

    // 單一可路由 skill(唯一必填字串 query),T-P4-2/T-P4-3/B-P4-12/13 共用的最小目錄。
    private const string SingleSkillCatalog = """
    [ { "name":"kb-query", "description":"知識庫檢索", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } } ]
    """;

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Platform.Service.Abstractions.ILlmAgent RoutingAgent
        => _factory.Services.GetRequiredService<Platform.Service.Abstractions.ILlmAgent>();

    // T-P4-2(路由命中 → 無任何 skill 名的 TOOL_CALL_* 事件、使用者仍拿到答案)是
    // Agui_AgenticSkillRouted_NoToolCallEvents_FinalContentFromAnswerKey(同斷言 + invoke 輸入 + answer 鍵萃取)
    // 與 Agui_RoutingHitWithClientToolsPresent_*(同斷言 + client tools 維度)的共同子集,不另留一份。

    // T-P4-3/B-P4-13(已知限制的驗收,非 bug):路由命中 + 前端同時提供非空 client tools → 該輪 client tool
    // 不被呼叫(短路天花板,01-plan §5.2/02-spec §5.2),使用者仍拿到 skill 的答案。即使這次的觸發訊息與
    // T-P4-1 完全相同(FakeChatClient.ToolCallTrigger,原本會讓 FakeChatClient 腳本化吐出 function call),
    // 一旦路由命中就直接短路,FakeChatClient 這一輪完全不會被呼叫到。
    [Fact]
    public async Task Agui_RoutingHitWithClientToolsPresent_ClientToolNotInvoked_UserStillGetsSkillAnswer()
    {
        FakeWorkflowEngineClient.CatalogOverride = Cat(SingleSkillCatalog);
        var routingAgent = (FakeLlmAgent)RoutingAgent;
        var originalResponse = routingAgent.Response;
        routingAgent.Response = "kb-query";
        try
        {
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await SendAguiAsync(
                client, RunInputFor("t-p4-3", FakeChatClient.ToolCallTrigger, tools: SwitchViewClientTools));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var raw = await resp.Content.ReadAsStringAsync();
            var frames = ExtractFrames(raw);
            var types = frames.Select(f => f.GetProperty("type").GetString()).ToList();

            Assert.DoesNotContain(types, t => t == "TOOL_CALL_START" || t == "TOOL_CALL_ARGS" || t == "TOOL_CALL_END");
            Assert.Contains("TEXT_MESSAGE_CONTENT", types);
        }
        finally
        {
            routingAgent.Response = originalResponse;
            FakeWorkflowEngineClient.CatalogOverride = null;
        }
    }

    // P4 code review 發現 2:HIT 輪手動寫回 session 的 user ChatMessage 原本沒有 MessageId,
    // AguiWireDedupAgent 的去重(比對 stored.MessageId)因此永遠認不出它已入史——client 下一輪原樣重送
    // 整包 messages 時,那句話會被誤判成新訊息、重複疊加進送給模型的訊息列,且逐輪複合暴增。
    // 修法:HIT 輪寫回時 user 端沿用 wire 傳入的原始 MessageId。驗收:HIT 一輪 → 接續兩輪一般對話(MISS),
    // 每輪都模擬真實 client 全陣列重送;HIT 那句 user 訊息在後續每一輪送進 FakeChatClient 的訊息列中都
    // 恰好出現一次,且訊息數只線性成長(不因 wire 重送 + session 疊加而複合暴增)。
    [Fact]
    public async Task Agui_HitRound_ThenTwoMissRounds_FullArrayResend_HitUserMessageAppearsExactlyOnce_LinearGrowth()
    {
        FakeWorkflowEngineClient.CatalogOverride = Cat(SingleSkillCatalog);
        var routingAgent = (FakeLlmAgent)RoutingAgent;
        var originalResponse = routingAgent.Response;
        const string hitQuestion = "這季毛利率多少?";
        try
        {
            const string threadId = "p4-review-hit-then-miss";
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());
            var wireHistory = new List<object>();

            // FakeChatClient 是跨測試共用的 Singleton(同一個 _factory 貫穿整個測試類別),.Runs 會累積
            // 其他測試留下的紀錄,故只看本案「新增」的部分,不假設從 0 開始(比照本檔其餘案例的 threadId 隔離慣例)。
            var runsBefore = ChatClient.Runs.Count;

            // 第一輪:路由 HIT(kb-query),摘要走「裸」LLM 串流,完全不經過 FakeChatClient。
            routingAgent.Response = "kb-query";
            wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), hitQuestion));
            var resp1 = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
            var raw1 = await resp1.Content.ReadAsStringAsync();
            Assert.Equal(runsBefore, ChatClient.Runs.Count);   // HIT 短路,FakeChatClient 這一輪完全沒被呼叫。
            var (assistantId1, assistantText1) = ExtractAssistantMessage(raw1);
            wireHistory.Add(AssistantMsg(assistantId1, assistantText1));

            // 第二、三輪:路由 NONE → 純聊天(MISS),真正打 FakeChatClient,client 每輪重送完整歷史陣列。
            routingAgent.Response = "NONE";
            foreach (var text in new[] { "第二輪", "第三輪" })
            {
                wireHistory.Add(UserMsg(Guid.NewGuid().ToString("N"), text));
                var resp = await SendAguiAsync(client, RunInputWithMessages(threadId, wireHistory));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var raw = await resp.Content.ReadAsStringAsync();
                var (assistantId, assistantText) = ExtractAssistantMessage(raw);
                wireHistory.Add(AssistantMsg(assistantId, assistantText));
            }

            var newRuns = ChatClient.Runs.Skip(runsBefore).ToList();
            Assert.Equal(2, newRuns.Count);   // 兩輪 MISS,各觸發一次 FakeChatClient。

            // 核心斷言:HIT 那句 user 訊息在後續每一輪送進模型的訊息列中都恰好出現一次(未修復前會逐輪重複疊加)。
            foreach (var run in newRuns)
            {
                var texts = run.Select(m => m.Text ?? string.Empty).ToList();
                Assert.Single(texts, t => t == hitQuestion);
            }

            // 訊息數只線性成長,不複合暴增(第三輪頂多比第二輪多出「本輪新 user/assistant 訊息」這幾則,
            // 不會是倍數放大)。
            var round2Count = newRuns[0].Count;
            var round3Count = newRuns[1].Count;
            Assert.True(round3Count > round2Count, $"expected round3({round3Count}) > round2({round2Count})");
            Assert.True(
                round3Count <= round2Count + 3,
                $"訊息數不應複合暴增,round2={round2Count}, round3={round3Count}");
        }
        finally
        {
            routingAgent.Response = originalResponse;
            FakeWorkflowEngineClient.CatalogOverride = null;
        }
    }

    // AST-P1-012:一個「單必填字串」的 agentic skill 經聊天路由命中 → 事件流不得出現任何 skill 名的
    // TOOL_CALL_*(skill 從未註冊成 AITool,結構上不可能洩漏),且最終內容正確由固定的 answer 鍵萃取
    // (invoke 輸出 {output:{answer:"42"}} → 摘要如實帶出 "42")。kind:agentic 不影響路由。
    private const string AgenticSkillCatalog = """
    [ { "name":"sales-helper", "description":"銷售助理", "required_role":"USER", "source":"custom", "kind":"agentic",
        "input_schema": { "question": { "type":"str", "required":true } } } ]
    """;

    [Fact]
    public async Task Agui_AgenticSkillRouted_NoToolCallEvents_FinalContentFromAnswerKey()
    {
        FakeWorkflowEngineClient.CatalogOverride = Cat(AgenticSkillCatalog);
        var routingAgent = (FakeLlmAgent)RoutingAgent;
        var originalResponse = routingAgent.Response;
        routingAgent.Response = "sales-helper";   // 路由命中 agentic skill。
        try
        {
            var client = _factory.CreateClient().WithToken(_factory.IssueToken());

            var resp = await SendAguiAsync(client, RunInputFor("ast-p1-012", "這季毛利率多少?"));

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var raw = await resp.Content.ReadAsStringAsync();
            var frames = ExtractFrames(raw);
            var types = frames.Select(f => f.GetProperty("type").GetString()).ToList();

            // (a) 無任何 server-skill 的 TOOL_CALL_* 事件。
            Assert.DoesNotContain(types, t => t == "TOOL_CALL_START" || t == "TOOL_CALL_ARGS" || t == "TOOL_CALL_END");

            // (b) agentic skill 確實被路由並執行(以使用者原訊息為 question 輸入)。
            var invoke = Assert.Single(FakeWorkflowEngineClient.SkillInvokes, i => i.Name == "sales-helper");
            Assert.Equal("這季毛利率多少?", invoke.Input["question"].GetString());

            // (c) 最終內容由 answer 鍵萃取(FakeWorkflowEngineClient 預設 output.answer = "42"),摘要如實帶出。
            var finalText = string.Concat(frames
                .Where(f => f.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
                .Select(f => f.GetProperty("delta").GetString()));
            Assert.Equal("42", finalText);
        }
        finally
        {
            routingAgent.Response = originalResponse;
            FakeWorkflowEngineClient.CatalogOverride = null;
        }
    }

    // B-P4-12:副駕取得同批能力——同一份目錄,ChatView(/api/chat)與副駕(/api/copilot/agui)各問同一個
    // 問題,兩邊呼叫 skill 時的 (Name, Input) 必須完全相同(兩條鏈路共用同一顆 SkillRoutingAgent 邏輯)。
    [Fact]
    public async Task Agui_And_ChatView_SameCatalog_SameQuestion_SkillInvokes_NameAndInputMatch()
    {
        FakeWorkflowEngineClient.CatalogOverride = Cat(SingleSkillCatalog);
        var routingAgent = (FakeLlmAgent)RoutingAgent;
        var originalResponse = routingAgent.Response;
        routingAgent.Response = "kb-query";
        const string question = "這季毛利率多少?";
        try
        {
            var before = FakeWorkflowEngineClient.SkillInvokes.Count;

            var chatClient = _factory.CreateClient().WithToken(_factory.IssueToken());
            var chatResp = await chatClient.PostAsJsonAsync("/api/chat", new { message = question });
            Assert.Equal(HttpStatusCode.OK, chatResp.StatusCode);

            var aguiClient = _factory.CreateClient().WithToken(_factory.IssueToken());
            var aguiResp = await SendAguiAsync(aguiClient, RunInputFor("b-p4-12", question));
            Assert.Equal(HttpStatusCode.OK, aguiResp.StatusCode);
            await aguiResp.Content.ReadAsStringAsync();

            var invokes = FakeWorkflowEngineClient.SkillInvokes.Skip(before).ToList();
            Assert.Equal(2, invokes.Count);
            Assert.Equal(invokes[0].Name, invokes[1].Name);
            Assert.Equal(
                invokes[0].Input["query"].GetString(),
                invokes[1].Input["query"].GetString());
        }
        finally
        {
            routingAgent.Response = originalResponse;
            FakeWorkflowEngineClient.CatalogOverride = null;
        }
    }

    // T-P4-4(AG-UI 側,補 A-17 在鏈路 A 的等價):串流中途 IChatClient 拋例外(FakeChatClient 的
    // "串流爆炸" 腳本,§9.3 鐵律 —— SkillRoutingAgent/ChatTurnRecorder 皆不得吞掉這個例外)→ 冒泡到
    // AGUI 編碼層產生 RUN_ERROR frame(無 RUN_FINISHED);半截回覆不得持久化(AddAsync/RememberAsync 皆零呼叫)。
    [Fact]
    public async Task Agui_MidStreamChatClientThrows_EmitsRunErrorFrame_DoesNotPersistOrRemember()
    {
        var savedBefore = FakeConversationStore.Saved.Count;
        var rememberedBefore = FakeMem0Client.Remembered.Count;
        var client = _factory.CreateClient().WithToken(_factory.IssueToken());

        var resp = await SendAguiAsync(client, RunInputFor("t-p4-4-agui", "串流爆炸"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        var frames = ExtractFrames(raw);
        var types = frames.Select(f => f.GetProperty("type").GetString()).ToList();

        Assert.Contains("RUN_ERROR", types);
        Assert.DoesNotContain("RUN_FINISHED", types);
        Assert.Equal(savedBefore, FakeConversationStore.Saved.Count);
        Assert.Equal(rememberedBefore, FakeMem0Client.Remembered.Count);
    }

    // ---- D6:canary 打開時,副駕這條鏈路同樣由 Root Orchestrator 作答(先前 Web 層整層空白) ----

    /// <summary>只回固定答案的 IAgentChatRuntime;真實實作的選擇閘另在 AgentChatRuntimeTests 驗。</summary>
    private sealed class StubAgentChatRuntime : Platform.Service.Abstractions.IAgentChatRuntime
    {
        public string? Reply { get; init; }

        public List<string> ConversationIds { get; } = new();

        public Task<Microsoft.Agents.AI.AgentResponse?> RunAsync(
            string message, string conversationId, Guid? requestedOrchestratorId,
            Platform.Service.Abstractions.IChatIdentityAccessor identity,
            string? logicalAttemptId = null, CancellationToken ct = default)
        {
            ConversationIds.Add(conversationId);
            return Task.FromResult<Microsoft.Agents.AI.AgentResponse?>(Reply is null
                ? null
                : new Microsoft.Agents.AI.AgentResponse(
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, Reply)));
        }
    }

    [Fact]
    public async Task Agui_AgentChatEnabled_EmitsOrchestratorAnswer_AndPersistsTurn()
    {
        const string answer = "Root Orchestrator 的副駕答案";
        var runtime = new StubAgentChatRuntime { Reply = answer };
        await using var factory = new TestWebAppFactory(
            agentChatEnabled: true, agentChatTenantAllowlist: "demo-a");
        var client = factory
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Platform.Service.Abstractions.IAgentChatRuntime>();
                services.AddSingleton<Platform.Service.Abstractions.IAgentChatRuntime>(runtime);
            }))
            .CreateClient()
            .WithToken(factory.IssueToken(username: "d6-agui-user", role: "USER", tenantCode: "demo-a"));

        var resp = await SendAguiAsync(client, RunInputFor("d6-agui", "這季毛利率多少?"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var raw = await resp.Content.ReadAsStringAsync();
        var types = EventTypes(raw);

        // 短路答案照 AG-UI 事件格式送出(單一 CONTENT frame),沒有任何 TOOL_CALL_* 洩漏,也沒有 RUN_ERROR。
        Assert.Equal(
            new[] { "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED" },
            types);
        Assert.Equal(answer, ExtractAssistantMessage(raw).Text);

        // AG-UI 的 session/D6 conversationId 只能來自 JWT 身分,不是 wire 的 threadId。
        Assert.Equal("demo-a:d6-agui-user", Assert.Single(runtime.ConversationIds));

        // 短路輪照樣進共用歷史(與 /api/chat* 同一份)。
        var saved = Assert.Single(
            FakeConversationStore.Saved, s => s.UserId == "d6-agui-user");
        Assert.Equal(answer, saved.Response.Reply);
    }

    /// <summary>可注入 recall/remember 例外的 IMem0Client fake，驗證 pipeline 邊界仍會降級。</summary>
    private sealed class ThrowingMem0Client : Platform.Service.Abstractions.IMem0Client
    {
        private readonly Exception? _onRecall;
        private readonly Exception? _onRemember;

        public ThrowingMem0Client(Exception? onRecall = null, Exception? onRemember = null)
        {
            _onRecall = onRecall;
            _onRemember = onRemember;
        }

        public Task<string> RecallAsync(string userId, string query, CancellationToken ct = default)
            => _onRecall is not null ? throw _onRecall : Task.FromResult(string.Empty);

        public Task RememberAsync(string userId, string userMessage, string aiReply, CancellationToken ct = default)
            => _onRemember is not null ? throw _onRemember : Task.CompletedTask;
    }
}
