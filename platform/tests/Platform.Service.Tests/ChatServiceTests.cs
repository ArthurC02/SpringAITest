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

        // 取得回覆後才寫 mem0。
        Assert.Single(mem0.Remembered);
        Assert.Equal(("u1", "你好嗎", "AI 答覆"), mem0.Remembered[0]);
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

    // ---- 工作流聊天工具(已登入才掛;每個工具轉呼叫對應工作流) ----

    [Fact]
    public async Task Chat_WithUserContext_PassesKnowledgeTool_AndToolInvokesRagQa()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { Answer = "文件說 42" };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        var tool = tools!.Single(t => t.Name == "search_knowledge_base");

        // 工具實際執行:以呼叫者的租戶身分打 rag_qa,question 進 input,回 answer 字串。
        var answer = await tool.InvokeAsync("平台有哪些文件?", CancellationToken.None);
        Assert.Equal("文件說 42", answer);
        Assert.Equal("rag_qa", workflows.LastInvoke!.Value.Name);
        Assert.Equal("demo-a", workflows.LastInvoke!.Value.Ctx.TenantCode);
        Assert.Equal("平台有哪些文件?", workflows.LastInvoke!.Value.Input["question"].GetString());
    }

    [Fact]
    public async Task Chat_UserRole_GetsFourTools_WithoutAdminReport()
    {
        var agent = new FakeLlmAgent();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore());

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Equal(
            new[] { "search_knowledge_base", "verified_knowledge_query", "summarize_text", "triage_question" },
            names);
    }

    [Fact]
    public async Task Chat_AdminRole_AlsoGetsAnalysisReportTool()
    {
        var agent = new FakeLlmAgent();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore());

        var tools = await svc.BuildToolsAsync(AdminA, CancellationToken.None);

        Assert.Equal(5, tools!.Count);
        Assert.Contains(tools!, t => t.Name == "generate_analysis_report");
    }

    // 每個工具都要打對工作流、用對輸入 key(對照表驅動,一條測試涵蓋全部)。
    [Theory]
    [InlineData("search_knowledge_base", "rag_qa", "question")]
    [InlineData("verified_knowledge_query", "kb_query", "query")]
    [InlineData("summarize_text", "summarize", "text")]
    [InlineData("triage_question", "triage", "question")]
    [InlineData("generate_analysis_report", "analyze_report", "topic")]
    public async Task EachTool_InvokesItsWorkflow_WithItsInputKey(string toolName, string workflow, string inputKey)
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(AdminA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == toolName);

        await tool.InvokeAsync("輸入內容", CancellationToken.None);

        Assert.Equal(workflow, workflows.LastInvoke!.Value.Name);
        Assert.Equal("輸入內容", workflows.LastInvoke!.Value.Input[inputKey].GetString());
    }

    [Fact]
    public async Task Chat_Anonymous_BuildsNoTools()
    {
        var svc = Build(new FakeLlmAgent(), new FakeMem0Client(), new FakeConversationStore());

        // 匿名(userCtx null)→ 無路由表(Skill 需租戶身分)。
        Assert.Null(await svc.BuildToolsAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task StreamChat_WithUserContext_RoutesWithToolCatalog()
    {
        var agent = new FakeLlmAgent();
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore());

        await foreach (var _ in svc.StreamChatAsync("問題", "u1", "c1", UserA))
        {
        }

        // 串流也先路由:第一次(阻塞)CompleteAsync 的 system 目錄列出可用工具。
        Assert.Contains(agent.CompleteCalls, m => m.Count > 0 && m[0].Content.Contains("search_knowledge_base"));
    }

    [Fact]
    public async Task KbQueryTool_Abstains_FallsBackToRagQa_WithHonestLabel()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService
        {
            Answer = "rag 的答案",
            KbQueryOutput = new()
            {
                ["answer_mode"] = System.Text.Json.JsonSerializer.SerializeToElement("ABSTAIN"),
                ["final_answer"] = System.Text.Json.JsonSerializer.SerializeToElement("【無法提供答案】證據不足"),
            },
        };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "verified_knowledge_query");

        var result = await tool.InvokeAsync("寵物守則對貓的規定?", CancellationToken.None);

        // 棄答 → 確定性改打 rag_qa,結果如實註明「不含稽核保證」。
        Assert.Contains("棄答", result);
        Assert.Contains("rag 的答案", result);
        Assert.Equal(new[] { "kb_query", "rag_qa" }, workflows.Invokes.Select(i => i.Name).ToArray());
        Assert.Equal("寵物守則對貓的規定?", workflows.Invokes[1].Input["question"].GetString());
    }

    [Fact]
    public async Task KbQueryTool_AnswersNormally_ExtractsFinalAnswer_NotWholeJson()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService
        {
            KbQueryOutput = new()
            {
                ["answer_mode"] = System.Text.Json.JsonSerializer.SerializeToElement("ANSWER"),
                ["final_answer"] = System.Text.Json.JsonSerializer.SerializeToElement("有憑據的答案"),
                ["trace"] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { "一大包不該回給模型的雜訊" }),
            },
        };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "verified_knowledge_query");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("有憑據的答案", result);
        Assert.Single(workflows.Invokes); // 沒棄答就不兜底
    }

    [Fact]
    public async Task Tool_WorkflowFails_ReturnsErrorText_InsteadOfThrowing()
    {
        var agent = new FakeLlmAgent();
        var workflows = new FakeWorkflowService { ThrowOnInvoke = new InvalidOperationException("下游爆炸") };
        var svc = Build(agent, new FakeMem0Client(), new FakeConversationStore(), workflows: workflows);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "search_knowledge_base");

        // 工具失敗不往外拋(否則整輪聊天 500),回錯誤文字讓模型照實轉述。
        var result = await tool.InvokeAsync("q", CancellationToken.None);
        Assert.StartsWith("工作流 rag_qa 呼叫失敗", result);
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
