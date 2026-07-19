using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

public sealed class ChatServiceTests
{
    private static ChatService Build(
        FakeLlmAgent agent, FakeMem0Client mem0, FakeConversationStore convos, InMemoryChatMemoryStore? memory = null,
        FakeWorkflowService? workflows = null)
        => new(agent, memory ?? new InMemoryChatMemoryStore(), mem0, convos, workflows ?? new FakeWorkflowService(),
            new LlmOptions(), NullLogger<ChatService>.Instance);

    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");
    private static readonly UserContext AdminA = new("admin-a", "demo-a", "ADMIN");

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 五顆內建可路由 skill(鏡射 workflow GET /skills 真實回應形狀)+ 一顆 template_* 骨架(須被濾掉)。
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
        "input_schema": { "topic": { "type":"str", "required":true } } },
      { "name":"template_infer", "description":"骨架,不可路由", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } }
    ]
    """;

    [Fact]
    public async Task Chat_CallsLlm_AndPersistsPromptAndReply()
    {
        var agent = new FakeLlmAgent { Response = "AI 答覆" };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(agent, mem0, convos);

        // 持久化需要身分:對話以 (tenant_id, user_id) 隔離,匿名不持久化。
        var response = await svc.ChatAsync("你好嗎", "u1", "c1", UserA);

        Assert.Equal("AI 答覆", response.Reply);
        Assert.True(response.Id > 0);

        var saved = Assert.Single(convos.Saved);
        Assert.Equal("你好嗎", saved.Prompt);
        Assert.Equal("AI 答覆", saved.Reply);

        // 最後一則送進 LLM 的必須是本輪 user 訊息。
        Assert.Equal("user", agent.LastMessages!.Last().Role);
        Assert.Equal("你好嗎", agent.LastMessages!.Last().Content);

        // 取得回覆後才寫 mem0;已登入 → uid 用 JWT 身分(租戶碼:使用者),不信任 body 的 userId。
        Assert.Single(mem0.Remembered);
        Assert.Equal(("demo-a:user-a", "你好嗎", "AI 答覆"), mem0.Remembered[0]);
    }

    // 每輪都注入的固定護欄逐字(與 ChatService.ChatGuardPrompt 同步)。
    private const string GuardPrompt =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    [Fact]
    public async Task Chat_InjectsSystemMemory_WhenMem0HasResults()
    {
        var agent = new FakeLlmAgent();
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者喜歡貓\n" };
        var svc = Build(agent, mem0, new FakeConversationStore());

        await svc.ChatAsync("問題", "u1", "c1");

        // 護欄永遠第一;mem0 前言緊接在後(獨立的第二則 system)。
        Assert.Equal("system", agent.LastMessages![0].Role);
        Assert.Equal(GuardPrompt, agent.LastMessages![0].Content);

        var mem0Msg = agent.LastMessages![1];
        Assert.Equal("system", mem0Msg.Role);
        Assert.Contains("以下是你先前記住、關於這位使用者的長期記憶", mem0Msg.Content);
        Assert.Contains("使用者喜歡貓", mem0Msg.Content);
    }

    [Fact]
    public async Task Chat_GuardIsFirstSystemMessage_WhenMem0Empty()
    {
        var agent = new FakeLlmAgent();
        var svc = Build(agent, new FakeMem0Client { RecallResult = string.Empty }, new FakeConversationStore());

        await svc.ChatAsync("問題", "u1", "c1");

        // mem0 無記憶時,唯一的 system 訊息就是護欄,且排在最前。
        Assert.Equal("system", agent.LastMessages![0].Role);
        Assert.Equal(GuardPrompt, agent.LastMessages![0].Content);
        Assert.Single(agent.LastMessages!, m => m.Role == "system");
    }

    [Fact]
    public async Task Chat_ShortTermMemory_CarriesPriorExchange()
    {
        var agent = new FakeLlmAgent { Response = "第一答" };
        var memory = new InMemoryChatMemoryStore();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), memory);

        await svc.ChatAsync("第一問", "u1", "c1");
        agent.Response = "第二答";
        await svc.ChatAsync("第二問", "u1", "c1");

        // 第二輪的訊息列應包含上一輪的 user 與 assistant。
        Assert.Contains(agent.LastMessages!, m => m is { Role: "user", Content: "第一問" });
        Assert.Contains(agent.LastMessages!, m => m is { Role: "assistant", Content: "第一答" });
        Assert.Equal("第二問", agent.LastMessages!.Last().Content);
    }

    [Fact]
    public async Task Chat_PersistFailure_Propagates()
    {
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), convos);

        // 阻塞式:持久化失敗照舊往上拋(對外 500)。需有身分才會走到持久化那一步。
        await Assert.ThrowsAsync<Platform.Service.Exceptions.BackendCallException>(() => svc.ChatAsync("嗨", "u1", "c1", UserA));
    }

    [Fact]
    public async Task StreamChat_YieldsChunksInOrder_AndPersistsConcatenated()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "甲", "乙", "丙" } };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(agent, mem0, convos);

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
        var agent = new FakeLlmAgent { Chunks = new[] { "甲", "乙" } };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore { ThrowOnAdd = true };
        var svc = Build(agent, mem0, convos);

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

    [Fact]
    public async Task Fallback_BothBlank_UseDefault_ForMem0AndSharedWindow()
    {
        var agent = new FakeLlmAgent();
        var mem0 = new FakeMem0Client();
        var svc = Build(agent, mem0, new FakeConversationStore());

        await svc.ChatAsync("第一問", "", "");
        await svc.ChatAsync("第二問", "", "");

        // userId 空白 → mem0 收到 uid="default"。
        Assert.Equal("default", mem0.Remembered[0].UserId);
        // conversationId 空白 → 退回 uid("default"),兩輪共用同一短期記憶視窗。
        Assert.Contains(agent.LastMessages!, m => m is { Role: "user", Content: "第一問" });
        Assert.Equal("第二問", agent.LastMessages!.Last().Content);
    }

    [Fact]
    public async Task Fallback_UserOnly_ConversationBlank_UsesUserIdAsWindowKey_ThenIsolatesByCid()
    {
        var agent = new FakeLlmAgent();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore());

        await svc.ChatAsync("甲", "u1", "");
        await svc.ChatAsync("乙", "u1", "");
        // cid 空白 → 短期記憶 key = "u1",兩輪共窗。
        Assert.Contains(agent.LastMessages!, m => m is { Role: "user", Content: "甲" });

        // 改以明確 cid="c9" 呼叫:不同視窗,不含前兩輪。
        await svc.ChatAsync("丙", "u1", "c9");
        Assert.DoesNotContain(agent.LastMessages!, m => m.Content == "甲");
        Assert.DoesNotContain(agent.LastMessages!, m => m.Content == "乙");
    }

    // ---- IDOR 修正:已登入的記憶 key 綁 JWT 身分,不信任 body 的 userId/conversationId(跨租戶/跨使用者不外洩) ----

    [Fact]
    public async Task LoggedIn_MemoryKeys_BindToJwtIdentity_NotClientBody_IsolatingAcrossUsers()
    {
        var agent = new FakeLlmAgent { Response = "答" };
        var mem0 = new FakeMem0Client();
        var memory = new InMemoryChatMemoryStore();
        var svc = Build(agent, mem0, new FakeConversationStore(), memory);

        var userB = new UserContext("user-b", "demo-b", "USER");

        // 兩位不同租戶/使用者刻意送「相同」的 body userId 與 conversationId(攻擊者猜/撞 key 的情境)。
        await svc.ChatAsync("A 的秘密", userId: "victim", conversationId: "shared", UserA);
        await svc.ChatAsync("B 問一句", userId: "victim", conversationId: "shared", userB);

        // 短期視窗 key = 「租戶:使用者:對話」→ B 這輪看不到 A 的前一輪(body conversationId 相同也不撞)。
        Assert.DoesNotContain(agent.LastMessages!, m => m.Content == "A 的秘密");

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
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Equal(new[] { "kb_query", "rag_qa", "summarize", "triage" }, names);
    }

    [Fact]
    public async Task Chat_AdminRole_AlsoGetsAnalyzeReportSkill()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Catalog = Cat(BuiltinCatalog) };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(AdminA, CancellationToken.None);

        Assert.Equal(5, tools!.Count);
        Assert.Contains(tools!, t => t.Name == "analyze_report");
        Assert.DoesNotContain(tools!, t => t.Name == "template_infer");
    }

    // 每個內建 skill 都要用對輸入 key,走 /skills/{name}/invoke。
    [Theory]
    [InlineData("kb_query", "query")]
    [InlineData("rag_qa", "question")]
    [InlineData("summarize", "text")]
    [InlineData("triage", "question")]
    [InlineData("analyze_report", "topic")]
    public async Task EachBuiltinSkill_InvokesSkillEndpoint_WithItsInputKey(string skillName, string inputKey)
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Catalog = Cat(BuiltinCatalog) };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(AdminA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == skillName);

        await tool.InvokeAsync("輸入內容", CancellationToken.None);

        Assert.Equal(skillName, workflows.LastSkillInvoke!.Value.Name);
        Assert.Equal("輸入內容", workflows.LastSkillInvoke!.Value.Input[inputKey].GetString());
    }

    [Fact]
    public async Task Chat_Anonymous_BuildsNoTools()
    {
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore());

        // 匿名(userCtx null)→ 無路由表(Skill 需租戶身分)。
        Assert.Null(await svc.BuildToolsAsync(null, CancellationToken.None));
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
        Assert.Contains(agent.CompleteCalls, m => m.Count > 0 && m[0].Content.Contains("kb_query"));
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
                ["kb_query"] = JsonSerializer.SerializeToElement(new
                {
                    skill = "kb_query",
                    output = new { answer_mode = "ABSTAIN", final_answer = "【無法提供答案】證據不足" },
                }),
                ["rag_qa"] = JsonSerializer.SerializeToElement(new
                {
                    skill = "rag_qa",
                    output = new { answer = "rag 的答案" },
                }),
            },
        };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "kb_query");

        var result = await tool.InvokeAsync("寵物守則對貓的規定?", CancellationToken.None);

        // 棄答 → 確定性改打 rag_qa skill,結果如實註明「不含稽核保證」(文案逐字對照 InvokeSkillToolAsync)。
        Assert.Equal(
            "嚴格稽核查詢因證據不足而棄答;以下是一般知識庫檢索(不含稽核保證)的結果:rag 的答案",
            result);
        Assert.Equal(new[] { "kb_query", "rag_qa" }, workflows.SkillInvokes.Select(i => i.Name).ToArray());
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
                ["kb_query"] = JsonSerializer.SerializeToElement(new
                {
                    skill = "kb_query",
                    output = new { answer_mode = "ANSWER", final_answer = "有憑據的答案" },
                }),
            },
        };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "kb_query");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("有憑據的答案", result);
        Assert.Single(workflows.SkillInvokes); // 沒棄答就不兜底
    }

    [Fact]
    public async Task CatalogFailure_ToolsListIsEmpty_ChatStillRepliesNormally()
    {
        var agent = new FakeLlmAgent { Response = "純聊天回覆" };
        var workflows = new FakeWorkflowService { ThrowOnCatalog = new InvalidOperationException("目錄爆炸") };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        // 目錄取得失敗 → 這輪無工具(不再有靜態工具可退),但聊天仍正常回覆(純聊天兜底接手)。
        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        Assert.Empty(tools!);

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);
        Assert.Equal("純聊天回覆", reply.Reply);
    }

    [Fact]
    public async Task Fallback_ConversationOnly_UserBlank_Mem0UsesDefault_WindowUsesCid()
    {
        var agent = new FakeLlmAgent();
        var mem0 = new FakeMem0Client();
        var svc = Build(agent, mem0, new FakeConversationStore());

        await svc.ChatAsync("問一", "", "c1");
        // userId 空白 → mem0 用 "default"。
        Assert.Equal("default", mem0.Remembered[0].UserId);

        // 短期記憶用 "c1":同一 cid 第二輪含第一輪。
        await svc.ChatAsync("問二", "", "c1");
        Assert.Contains(agent.LastMessages!, m => m is { Role: "user", Content: "問一" });
    }

    // ---- H5:串流中途失敗 → 例外傳播、半截回覆不持久化 ----

    [Fact]
    public async Task StreamChat_MidStreamFailure_Propagates_DoesNotPersistPartial()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "甲", "乙" }, ThrowAfterChunks = 1 };
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var svc = Build(agent, mem0, convos);

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

    // ---- 匿名不持久化:對話以 (tenant_id, user_id) 隔離,匿名沒有身分可歸屬 ----

    [Fact]
    public async Task Chat_Anonymous_DoesNotCallAddAsync()
    {
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent { Response = "匿名回覆" }, new FakeMem0Client(), convos);

        var response = await svc.ChatAsync("嗨", "u1", "c1"); // userCtx = null

        Assert.Equal("匿名回覆", response.Reply);
        Assert.Empty(convos.Saved);
        Assert.Empty(convos.AddCalledWith);
    }

    [Fact]
    public async Task StreamChat_Anonymous_DoesNotCallAddAsync()
    {
        var convos = new FakeConversationStore();
        var svc = Build(new FakeLlmAgent { Chunks = new[] { "甲", "乙" } }, new FakeMem0Client(), convos);

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
}
