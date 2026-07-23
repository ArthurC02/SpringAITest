using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Options;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
// Microsoft.Extensions.AI 也定義 ChatResponse;此檔的 ChatResponse 一律指 Dtos 版(對外 DTO)。
using ChatResponse = Platform.Service.Dtos.ChatResponse;

namespace Platform.Service.Tests;

public sealed class ChatServiceTests
{
    private static (ChatService Service, AIHostAgent HostAgent, InMemoryChatHistoryProvider HistoryProvider) BuildWithAgent(
        FakeLlmAgent agent, FakeMem0Client mem0, FakeConversationStore convos,
        FakeWorkflowService? workflows = null, FakeChatClient? chatClient = null)
    {
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient, mem0, convos, identity, agent, workflows);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);
        return (svc, hostAgent, historyProvider);
    }

    private static ChatService Build(
        FakeLlmAgent agent, FakeMem0Client mem0, FakeConversationStore convos,
        FakeWorkflowService? workflows = null, FakeChatClient? chatClient = null)
        => BuildWithAgent(agent, mem0, convos, workflows, chatClient).Service;

    /// <summary>
    /// P4:路由(BuildToolsAsync/tool.InvokeAsync)搬進 SkillRoutingAgent,測試斷言面不變,只搬構造——
    /// 這批既有測試直接呼叫 BuildToolsAsync/InvokeAsync(不經 ChatAsync/StreamChatAsync),
    /// 因此需要 SkillRoutingAgent 實例而非只有 ChatService。
    /// </summary>
    private static (ChatService Service, SkillRoutingAgent Routing) BuildRouting(
        FakeLlmAgent agent, FakeMem0Client mem0, FakeConversationStore convos,
        FakeWorkflowService? workflows = null, FakeChatClient? chatClient = null)
    {
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, routing) = TestChatAgent.Build(chatClient, mem0, convos, identity, agent, workflows);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);
        return (svc, routing);
    }

    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");
    private static readonly UserContext AdminA = new("admin-a", "demo-a", "ADMIN");

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 五顆內建可路由 skill(鏡射 workflow GET /skills 真實回應形狀)+ 一顆 template-* 骨架(須被濾掉)。
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
      { "name":"analyze-report", "description":"分析報告", "required_role":"ADMIN", "source":"builtin",
        "input_schema": { "topic": { "type":"str", "required":true } } },
      { "name":"template-infer", "description":"骨架,不可路由", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } }
    ]
    """;

    [Fact]
    public async Task Chat_CallsLlm_AndPersistsPromptAndReply()
    {
        var chatClient = new FakeChatClient { Response = "AI 答覆" };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent(), mem0, convos, chatClient: chatClient);

        // 持久化需要身分:對話以 (tenant_id, user_id) 隔離,匿名不持久化。
        var response = await svc.ChatAsync("你好嗎", "u1", "c1", UserA);

        Assert.Equal("AI 答覆", response.Reply);
        Assert.True(response.Id > 0);

        var saved = Assert.Single(convos.Saved);
        Assert.Equal("你好嗎", saved.Prompt);
        Assert.Equal("AI 答覆", saved.Reply);

        // 最後一則送進共用 hosted agent 的必須是本輪 user 訊息。
        Assert.Equal(ChatRole.User, chatClient.LastMessages!.Last().Role);
        Assert.Equal("你好嗎", chatClient.LastMessages!.Last().Text);

        // 取得回覆後才寫 mem0;已登入 → uid 用 JWT 身分(租戶碼:使用者),不信任 body 的 userId。
        Assert.Single(mem0.Remembered);
        Assert.Equal(("demo-a:user-a", "你好嗎", "AI 答覆"), mem0.Remembered[0]);
    }

    // 每輪都注入的固定護欄逐字(與 ChatService.ChatGuardPrompt 同步)。P2:護欄改走 ChatOptions.Instructions
    // (不再是訊息列裡的 system 訊息),因此以下兩案改斷言 chatClient.LastOptions!.Instructions。
    private const string GuardPrompt =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    [Fact]
    public async Task Chat_InjectsSystemMemory_WhenMem0HasResults()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者喜歡貓\n" };
        var svc = Build(new FakeLlmAgent(), mem0, new FakeConversationStore(), chatClient: chatClient);

        await svc.ChatAsync("問題", "u1", "c1", UserA);

        // 護欄永遠打頭;mem0 前言緊接在後,一個換行相接,同一則 Instructions 字串(不再是第二則訊息)。
        var instructions = chatClient.LastOptions!.Instructions!;
        Assert.StartsWith(GuardPrompt, instructions);
        Assert.Contains("以下是你先前記住、關於這位使用者的長期記憶", instructions);
        Assert.Contains("使用者喜歡貓", instructions);

        // Instructions 不進訊息列(N4):訊息列中不應出現護欄/mem0 文字。
        Assert.DoesNotContain(chatClient.LastMessages!, m => (m.Text ?? "").Contains(GuardPrompt));
    }

    [Fact]
    public async Task Chat_GuardIsFirstSystemMessage_WhenMem0Empty()
    {
        var chatClient = new FakeChatClient();
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client { RecallResult = string.Empty }, new FakeConversationStore(), chatClient: chatClient);

        await svc.ChatAsync("問題", "u1", "c1");

        // mem0 無記憶時,Instructions 就只有護欄本身。
        Assert.Equal(GuardPrompt, chatClient.LastOptions!.Instructions);
    }

    [Fact]
    public async Task Chat_ShortTermMemory_CarriesPriorExchange()
    {
        var chatClient = new FakeChatClient { Response = "第一答" };
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore(), chatClient: chatClient);

        await svc.ChatAsync("第一問", "u1", "c1");
        chatClient.Response = "第二答";
        await svc.ChatAsync("第二問", "u1", "c1");

        // 第二輪送進共用 agent 的訊息列應包含上一輪的 user 與 assistant(框架 session 管理,取代自寫 store)。
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.User && m.Text == "第一問");
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.Assistant && m.Text == "第一答");
        Assert.Equal("第二問", chatClient.LastMessages!.Last().Text);
    }

    [Fact]
    public async Task Chat_PersistFailure_Propagates()
    {
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var mem0 = new FakeMem0Client();
        var svc = Build(new FakeLlmAgent(), mem0, convos);

        // 阻塞式:持久化失敗照舊往上拋(對外 500)。需有身分才會走到持久化那一步。
        await Assert.ThrowsAsync<Platform.Service.Exceptions.BackendCallException>(() => svc.ChatAsync("嗨", "u1", "c1", UserA));

        // 對齊基線(阻塞路徑 AddAsync 沒有 try/catch,拋出時 RememberAsync 永遠不會執行到):persist 失敗
        // 就不該 remember。
        Assert.Empty(mem0.Remembered);
    }

    // P4(§11 步驟 13 收尾):A-15 在「路由命中」(HIT)情境同樣成立——ChatTurnRecorder 掛在
    // SkillRoutingAgent 之外,阻塞/串流的 persist 語意不分 HIT/MISS,同一段程式碼處理。
    [Fact]
    public async Task Chat_RoutedSkillHit_PersistFailure_Propagates_DoesNotRemember()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb-query");           // 路由命中
        agent.Responses.Enqueue("摘要回覆");            // 摘要
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var mem0 = new FakeMem0Client();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"kb-query", "description":"x", "required_role":"USER", "source":"builtin", "input_schema": { "query": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, mem0, convos, wf);

        await Assert.ThrowsAsync<Platform.Service.Exceptions.BackendCallException>(
            () => svc.ChatAsync("這季毛利率多少?", "u1", "c1", UserA));

        Assert.Empty(mem0.Remembered);
    }

    [Fact]
    public async Task StreamChat_YieldsChunksInOrder_AndPersistsConcatenated()
    {
        var chatClient = new FakeChatClient { Chunks = new[] { "甲", "乙", "丙" } };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent(), mem0, convos, chatClient: chatClient);

        var collected = new List<string>();
        // 持久化需要身分:對話以 (tenant_id, user_id) 隔離,匿名不持久化。
        await foreach (var chunk in svc.StreamChatAsync("嗨", "u1", "c1", UserA))
        {
            collected.Add(chunk);
        }

        Assert.Equal(new[] { "甲", "乙", "丙" }, collected);

        // 串流完成時才持久化串接後的全文。
        var saved = Assert.Single(convos.Saved);
        Assert.Equal("嗨", saved.Prompt);
        Assert.Equal("甲乙丙", saved.Reply);
        Assert.Single(mem0.Remembered);
    }

    [Fact]
    public async Task StreamChat_PersistFailure_IsBestEffort_StillYieldsAndRemembers()
    {
        var chatClient = new FakeChatClient { Chunks = new[] { "甲", "乙" } };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var svc = Build(new FakeLlmAgent(), mem0, convos, chatClient: chatClient);

        var collected = new List<string>();
        // 持久化失敗不可讓已送出的串流炸掉;需有身分才會走到持久化那一步。
        await foreach (var chunk in svc.StreamChatAsync("嗨", "u1", "c1", UserA))
        {
            collected.Add(chunk);
        }

        Assert.Equal(new[] { "甲", "乙" }, collected);
        // 持久化失敗仍照常寫 mem0。
        Assert.Single(mem0.Remembered);
    }

    // P4(§11 步驟 13 收尾):A-16 在「路由命中」(HIT)情境同樣成立——串流 best-effort 持久化失敗
    // 不影響已送出的摘要 chunks,也不影響 remember(ChatTurnRecorder 統一處理,不分 HIT/MISS)。
    [Fact]
    public async Task StreamChat_RoutedSkillHit_PersistFailure_IsBestEffort_StillStreamsAndRemembers()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "摘要", "片段" } };
        agent.Responses.Enqueue("kb-query");           // 路由命中(阻塞)
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var mem0 = new FakeMem0Client();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"kb-query", "description":"x", "required_role":"USER", "source":"builtin", "input_schema": { "query": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, mem0, convos, wf);

        var collected = new List<string>();
        await foreach (var chunk in svc.StreamChatAsync("這季毛利率多少?", "u1", "c1", UserA))
        {
            collected.Add(chunk);
        }

        Assert.Equal(new[] { "摘要", "片段" }, collected);
        Assert.Single(mem0.Remembered);
    }

    [Fact]
    public async Task History_ReturnsStoreListDesc()
    {
        var convos = new FakeConversationStore();
        var baseTime = new DateTime(2026, 7, 11, 8, 0, 0, DateTimeKind.Utc);
        convos.Items.Add(new ChatResponse(1, "r1", baseTime));
        convos.Items.Add(new ChatResponse(2, "r2", baseTime.AddMinutes(1)));
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos);

        var history = await svc.HistoryAsync(UserA);

        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].Id);
        Assert.Equal("r2", history[0].Reply);
        Assert.Equal("r1", history[1].Reply);
    }

    // ---- H1:uid / cid fallback 決策表(NormalizeUser:空白→"default";NormalizeConversation:空白→uid) ----
    // P2:短期視窗改由框架 session store 管理,鏈路 A 的 session store 不疊 isolation(見 ChatService 頂部
    // 文件注記),匿名與登入對稱地有連續性——與現行 IChatMemoryStore(不分登入與否)行為一致。

    [Fact]
    public async Task Fallback_BothBlank_SkipsMem0_AndUsesSharedWindow()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client();
        var svc = Build(new FakeLlmAgent(), mem0, new FakeConversationStore(), chatClient: chatClient);

        await svc.ChatAsync("第一問", "", "");
        await svc.ChatAsync("第二問", "", "");

        // 匿名沒有可安全歸屬的長期 identity，不能把所有訪客寫進 default mem0 uid。
        Assert.Empty(mem0.Remembered);
        // conversationId 空白 → 退回 uid("default"),兩輪共用同一短期記憶視窗。
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.User && m.Text == "第一問");
        Assert.Equal("第二問", chatClient.LastMessages!.Last().Text);
    }

    [Fact]
    public async Task Fallback_UserOnly_ConversationBlank_UsesUserIdAsWindowKey_ThenIsolatesByCid()
    {
        var chatClient = new FakeChatClient();
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore(), chatClient: chatClient);

        await svc.ChatAsync("甲", "u1", "");
        await svc.ChatAsync("乙", "u1", "");
        // cid 空白 → 短期記憶 key = "u1",兩輪共窗。
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.User && m.Text == "甲");

        // 改以明確 cid="c9" 呼叫:不同視窗,不含前兩輪。
        await svc.ChatAsync("丙", "u1", "c9");
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "甲");
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "乙");
    }

    // ---- IDOR 修正:已登入的記憶 key 綁 JWT 身分,不信任 body 的 userId/conversationId(跨租戶/跨使用者不外洩) ----

    [Fact]
    public async Task LoggedIn_MemoryKeys_BindToJwtIdentity_NotClientBody_IsolatingAcrossUsers()
    {
        var chatClient = new FakeChatClient { Response = "答" };
        var mem0 = new FakeMem0Client();
        var svc = Build(new FakeLlmAgent(), mem0, new FakeConversationStore(), chatClient: chatClient);

        var userB = new UserContext("user-b", "demo-b", "USER");

        // 兩位不同租戶/使用者刻意送「相同」的 body userId 與 conversationId(攻擊者猜/撞 key 的情境)。
        await svc.ChatAsync("A 的秘密", userId: "victim", conversationId: "shared", UserA);
        await svc.ChatAsync("B 問一句", userId: "victim", conversationId: "shared", userB);

        // 短期視窗 key = 「租戶:使用者:對話」→ B 這輪看不到 A 的前一輪(body conversationId 相同也不撞)。
        // 不可弱化:必須續驗內容不互見,不可退化成只驗 key 字串不同。
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "A 的秘密");

        // mem0 uid 一律用 JWT 身分,body 的 userId="victim" 完全不採用。
        Assert.Equal("demo-a:user-a", mem0.Remembered[0].UserId);
        Assert.Equal("demo-b:user-b", mem0.Remembered[1].UserId);
    }

    // ---- Skill 聊天工具(單軌:動態目錄,已登入才掛;每個工具轉呼叫對應 skill) ----

    [Fact]
    public async Task Chat_UserRole_GetsFourBuiltinSkills_TemplateAndAdminOnlyFiltered()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Catalog = Cat(BuiltinCatalog) };
        var (_, routing) = BuildRouting(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Equal(new[] { "kb-query", "rag-qa", "summarize", "triage" }, names);
    }

    [Fact]
    public async Task Chat_AdminRole_AlsoGetsAnalyzeReportSkill()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Catalog = Cat(BuiltinCatalog) };
        var (_, routing) = BuildRouting(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await routing.BuildToolsAsync(AdminA, CancellationToken.None);

        Assert.Equal(5, tools!.Count);
        Assert.Contains(tools!, t => t.Name == "analyze-report");
        Assert.DoesNotContain(tools!, t => t.Name == "template-infer");
    }

    // 每個內建 skill 都要用對輸入 key,走 /skills/{name}/invoke。
    [Theory]
    [InlineData("kb-query", "query")]
    [InlineData("rag-qa", "question")]
    [InlineData("summarize", "text")]
    [InlineData("triage", "question")]
    [InlineData("analyze-report", "topic")]
    public async Task EachBuiltinSkill_InvokesSkillEndpoint_WithItsInputKey(string skillName, string inputKey)
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Catalog = Cat(BuiltinCatalog) };
        var (_, routing) = BuildRouting(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await routing.BuildToolsAsync(AdminA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == skillName);

        await tool.InvokeAsync("輸入內容", CancellationToken.None);

        Assert.Equal(skillName, workflows.LastSkillInvoke!.Value.Name);
        Assert.Equal("輸入內容", workflows.LastSkillInvoke!.Value.Input[inputKey].GetString());
    }

    [Fact]
    public async Task Chat_Anonymous_BuildsNoTools()
    {
        var (_, routing) = BuildRouting(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore());

        // 匿名(userCtx null)→ 無路由表(Skill 需租戶身分)。
        Assert.Null(await routing.BuildToolsAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task StreamChat_WithUserContext_RoutesWithSkillCatalog()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Catalog = Cat(BuiltinCatalog) };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        await foreach (var _ in svc.StreamChatAsync("問題", "u1", "c1", UserA))
        {
        }

        // 串流也先路由:第一次(阻塞)CompleteAsync 的 system 目錄列出可用工具。
        Assert.Contains(agent.CompleteCalls, m => m.Count > 0 && m[0].Content.Contains("kb-query"));
    }

    [Fact]
    public async Task KbQuerySkill_Abstains_FallsBackToRagQaSkill_WithHonestLabel()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService
        {
            Catalog = Cat(BuiltinCatalog),
            SkillOutputByName = new()
            {
                ["kb-query"] = JsonSerializer.SerializeToElement(new
                {
                    skill = "kb-query",
                    output = new { answer_mode = "ABSTAIN", final_answer = "【無法提供答案】證據不足" },
                }),
                ["rag-qa"] = JsonSerializer.SerializeToElement(new
                {
                    skill = "rag-qa",
                    output = new { answer = "rag 的答案" },
                }),
            },
        };
        var (_, routing) = BuildRouting(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "kb-query");

        var result = await tool.InvokeAsync("寵物守則對貓的規定?", CancellationToken.None);

        // 棄答 → 確定性改打 rag-qa skill,結果如實註明「不含稽核保證」(文案逐字對照 InvokeSkillToolAsync)。
        Assert.Equal(
            "嚴格稽核查詢因證據不足而棄答;以下是一般知識庫檢索(不含稽核保證)的結果:rag 的答案",
            result);
        Assert.Equal(new[] { "kb-query", "rag-qa" }, workflows.SkillInvokes.Select(i => i.Name).ToArray());
        Assert.Equal("寵物守則對貓的規定?", workflows.SkillInvokes[1].Input["question"].GetString());
    }

    [Fact]
    public async Task KbQuerySkill_AnswersNormally_DoesNotFallBackToRagQa()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService
        {
            Catalog = Cat(BuiltinCatalog),
            SkillOutputByName = new()
            {
                ["kb-query"] = JsonSerializer.SerializeToElement(new
                {
                    skill = "kb-query",
                    output = new { answer_mode = "ANSWER", final_answer = "有憑據的答案" },
                }),
            },
        };
        var (_, routing) = BuildRouting(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "kb-query");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("有憑據的答案", result);
        Assert.Single(workflows.SkillInvokes); // 沒棄答就不兜底
    }

    [Fact]
    public async Task CatalogFailure_ToolsListIsEmpty_ChatStillRepliesNormally()
    {
        var chatClient = new FakeChatClient { Response = "純聊天回覆" };
        var workflows = new FakeWorkflowService { ThrowOnCatalog = new InvalidOperationException("目錄爆炸") };
        var (svc, routing) = BuildRouting(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore(), workflows: workflows, chatClient: chatClient);

        // 目錄取得失敗 → 這輪無工具(不再有靜態工具可退了),但聊天仍正常回覆(純聊天兜底接手)。
        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        Assert.Empty(tools!);

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);
        Assert.Equal("純聊天回覆", reply.Reply);
    }

    [Fact]
    public async Task Fallback_ConversationOnly_UserBlank_SkipsMem0_WindowUsesCid()
    {
        var chatClient = new FakeChatClient();
        var mem0 = new FakeMem0Client();
        var svc = Build(new FakeLlmAgent(), mem0, new FakeConversationStore(), chatClient: chatClient);

        await svc.ChatAsync("問一", "", "c1");
        Assert.Empty(mem0.Remembered);

        // 短期記憶用 "c1":同一 cid 第二輪含第一輪。
        await svc.ChatAsync("問二", "", "c1");
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.User && m.Text == "問一");
    }

    // ---- H5:串流中途失敗 → 例外傳播、半截回覆不持久化 ----

    [Fact]
    public async Task StreamChat_MidStreamFailure_Propagates_DoesNotPersistPartial()
    {
        var chatClient = new FakeChatClient { Chunks = new[] { "甲", "乙" }, ThrowAfterChunks = 1 };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent(), mem0, convos, chatClient: chatClient);

        var collected = new List<string>();
        // 帶身分(UserA)才會實際嘗試持久化,讓「半截不寫」的斷言有意義(而非匿名本就不寫)。
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in svc.StreamChatAsync("嗨", "u1", "c1", UserA))
            {
                collected.Add(chunk);
            }
        });

        // 只吐了「甲」就炸;半截回覆不寫 backend、不寫 mem0。
        Assert.Equal(new[] { "甲" }, collected);
        Assert.Empty(convos.Saved);
        Assert.Empty(mem0.Remembered);
    }

    // ---- 匿名不持久化到 backend:對話記錄以 (tenant_id, user_id) 隔離,匿名沒有身分可歸屬 ----
    // (短期記憶本身仍會延續,見 Fallback_BothBlank_UseDefault_ForMem0AndSharedWindow——這是刻意的,
    // 對稱於現行 IChatMemoryStore 不分登入與否的行為;此處驗的是「backend 持久化」這一層不同的東西)。

    [Fact]
    public async Task Chat_Anonymous_DoesNotCallAddAsync()
    {
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos, chatClient: new FakeChatClient { Response = "匿名回覆" });

        var response = await svc.ChatAsync("嗨", "u1", "c1"); // userCtx = null

        Assert.Equal("匿名回覆", response.Reply);
        Assert.Empty(convos.Saved);
        Assert.Empty(convos.AddCalledWith);
    }

    [Fact]
    public async Task StreamChat_Anonymous_DoesNotCallAddAsync()
    {
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos, chatClient: new FakeChatClient { Chunks = new[] { "甲", "乙" } });

        await foreach (var _ in svc.StreamChatAsync("嗨", "u1", "c1")) // userCtx = null
        {
        }

        Assert.Empty(convos.Saved);
        Assert.Empty(convos.AddCalledWith);
    }

    [Fact]
    public async Task History_Anonymous_ReturnsEmpty_WithoutCallingStore()
    {
        var convos = new FakeConversationStore();
        convos.Items.Add(new ChatResponse(1, "r1", DateTime.UtcNow));
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos);

        var history = await svc.HistoryAsync(); // userCtx = null

        Assert.Empty(history);
    }

    // ---- A-19 等價(補 G6):短期記憶視窗 20 則 on-point / 21 則 off-point,於 ChatService 層驗證 ----
    // 前置以 historyProvider.SetMessages 直接播種「已累積出 20 則歷史」這個前置狀態(取代已刪除的
    // InMemoryChatMemoryStore.Append 播種方式);受測動作(再發一輪)仍經 ChatAsync 驅動,斷言送進
    // 共用 hosted agent 的 messages。匿名 + 非空白 conversationId 時 cid 直接等於 conversationId
    // (ChatService.DeriveMemoryKeys),故不需計算租戶前綴格式。

    [Fact]
    public async Task A19a_History20_OnPoint_AllRetained()
    {
        var chatClient = new FakeChatClient { Response = "本輪回覆" };
        var (svc, hostAgent, historyProvider) = BuildWithAgent(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore(), chatClient: chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("conv-a19a");
        var seeded = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"hist{i}"));
        }
        historyProvider.SetMessages(session, seeded);
        await hostAgent.SaveSessionAsync("conv-a19a", session);

        await svc.ChatAsync("本輪提問", "", "conv-a19a"); // 匿名,cid == conversationId

        for (var i = 0; i < 20; i++)
        {
            Assert.Contains(chatClient.LastMessages!, m => m.Text == $"hist{i}");
        }
        Assert.Equal("本輪提問", chatClient.LastMessages!.Last().Text);
    }

    [Fact]
    public async Task A19b_History21_OffPoint_OldestTurnTrimmed_OtherRetained()
    {
        var chatClient = new FakeChatClient { Response = "第一輪回覆" };
        var (svc, hostAgent, historyProvider) = BuildWithAgent(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore(), chatClient: chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("conv-a19b");
        var seeded = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"hist{i}"));
        }
        historyProvider.SetMessages(session, seeded);
        await hostAgent.SaveSessionAsync("conv-a19b", session);

        // 第一輪:20(已存)+ 2(這輪 user+assistant)= 22,觸發裁切——但裁切發生在這輪「回覆之後」寫回
        // session 的階段,這一輪自己送出的訊息列仍是裁切前的完整 20+1(與 spike 實測一致)。
        await svc.ChatAsync("第一輪觸發", "", "conv-a19b");

        chatClient.Response = "第二輪回覆";
        // 第二輪才看得到裁切後的結果:最舊整個 turn(hist0/hist1)被裁掉,其餘 18 則 + 第一輪交換皆保留。
        await svc.ChatAsync("第二輪確認", "", "conv-a19b");

        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "hist0");
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "hist1");
        for (var i = 2; i < 20; i++)
        {
            Assert.Contains(chatClient.LastMessages!, m => m.Text == $"hist{i}");
        }
        Assert.Contains(chatClient.LastMessages!, m => m.Text == "第一輪觸發");
        Assert.Contains(chatClient.LastMessages!, m => m.Text == "第一輪回覆");
        Assert.Equal("第二輪確認", chatClient.LastMessages!.Last().Text);
    }
}
