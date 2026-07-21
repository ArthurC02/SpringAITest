using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

/// <summary>
/// A 組 — 重構前行為安全網(plans/copilot-shared-core/04-acceptance-test.md §3,A-01~A-14/A-18~A-20)。
/// 釘住現行 <see cref="ChatService"/> 可觀察行為,斷言面選在「重構不會移動」的位置:
/// 一律經 <see cref="ChatService.ChatAsync"/> / <see cref="ChatService.StreamChatAsync"/> 驅動,
/// 只斷言 <see cref="FakeWorkflowService.SkillInvokes"/>、<see cref="FakeMem0Client.Remembered"/>、
/// <see cref="FakeConversationStore.Saved"/>、送進底層 chat client 的 message 清單這些可觀察結果。
/// 不呼叫 BuildToolsAsync / tool.InvokeAsync(那是會在 P4 被抽掉的接縫),不做 prompt 字串相等斷言
/// (唯一允許的例外是「送進 chat client 的 message 清單」用 Contains 驗證特定業務資料是否存在,
/// 不是驗證整段 prompt 逐字相等)。
///
/// P2(copilot-shared-core)記憶收斂後:路由命中(HIT)那一輪仍走「裸」<see cref="ILlmAgent"/>
/// (<see cref="FakeLlmAgent"/>),斷言面不變;未命中(MISS)/純聊天那一輪改跑共用的 hosted agent,
/// 斷言面從 <c>agent.LastMessages</c> 換成 <see cref="FakeChatClient"/> 收到的 messages/Instructions
/// ——這是接縫遷移,不是行為語意變更(見各案內註解)。
///
/// 精簡起見,前置條件(累積歷史筆數、預先塞入 fake 的腳本化回應)可直接設定在協作者上
/// (與既有 <c>convos.Items.Add(...)</c>、<c>ThrowOnAdd</c> 等寫法一致),
/// 但「本案受測的那個動作」一律經 ChatAsync / StreamChatAsync 觸發。
/// </summary>
public sealed class ChatBehaviorBaselineTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");
    private static readonly UserContext AdminA = new("admin-a", "demo-a", "ADMIN");

    private static (ChatService Service, AIHostAgent HostAgent, InMemoryChatHistoryProvider HistoryProvider) BuildWithAgent(
        FakeLlmAgent agent, FakeWorkflowService workflows, FakeMem0Client? mem0 = null,
        FakeConversationStore? convos = null, FakeChatClient? chatClient = null)
    {
        var effectiveMem0 = mem0 ?? new FakeMem0Client();
        var effectiveConvos = convos ?? new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, historyProvider, _) = TestChatAgent.Build(chatClient, effectiveMem0, effectiveConvos, identity, agent, workflows);
        var svc = new ChatService(hostAgent, effectiveConvos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);
        return (svc, hostAgent, historyProvider);
    }

    private static ChatService Build(
        FakeLlmAgent agent, FakeWorkflowService workflows, FakeMem0Client? mem0 = null,
        FakeConversationStore? convos = null, FakeChatClient? chatClient = null)
        => BuildWithAgent(agent, workflows, mem0, convos, chatClient).Service;

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 單一可路由 skill(唯一必填字串 query),多數案例的最小目錄。
    private const string SingleSkillCatalog = """
    [ { "name":"kb_query", "description":"知識庫檢索", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } } ]
    """;

    // ================================================================
    // A-01:路由命中的完整因果鏈 — 選對 skill → 帶對參數 → 回覆是摘要而非原始 JSON → remember 記最終摘要
    // ================================================================
    [Fact]
    public async Task A01_RoutedSkill_InvokesWithCorrectInput_RepliesWithSummary_NotRawJson()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");              // 第一次 CompleteAsync = 路由
        agent.Responses.Enqueue("本季毛利率是 32.8%。");   // 第二次 CompleteAsync = 摘要
        var mem0 = new FakeMem0Client();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"毛利率 32.8%" } }"""),
        };
        var svc = Build(agent, wf, mem0);

        var reply = await svc.ChatAsync("這季毛利率多少?", "u1", "c1", UserA);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("kb_query", invoke.Name);
        Assert.Equal("這季毛利率多少?", invoke.Input["query"].GetString());

        // 回覆是摘要輸出,不是工具原始 JSON(原始 JSON 會以 "{" 開頭)。
        Assert.Equal("本季毛利率是 32.8%。", reply.Reply);
        Assert.False(reply.Reply.TrimStart().StartsWith('{'));

        var remembered = Assert.Single(mem0.Remembered);
        Assert.Equal("本季毛利率是 32.8%。", remembered.AiReply);
    }

    // ================================================================
    // A-02:路由未命中的另一半決策表 — 零 invoke + 正常回覆,不擲例外
    // P2:純聊天兜底改跑共用 hosted agent,回覆改由 FakeChatClient 決定(FakeLlmAgent 只負責兩次路由)。
    // ================================================================
    [Fact]
    public async Task A02_RoutingNone_NoSkillInvoked_RepliesNormally()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");
        agent.Responses.Enqueue("NONE");
        var chatClient = new FakeChatClient { Response = "純聊天回覆" };

        var wf = new FakeWorkflowService { Catalog = Cat(SingleSkillCatalog) };
        var svc = Build(agent, wf, chatClient: chatClient);

        var reply = await svc.ChatAsync("你好呀", "u1", "c1", UserA);

        Assert.Empty(wf.SkillInvokes);
        Assert.Equal("純聊天回覆", reply.Reply);
    }

    // ================================================================
    // A-03:目錄失敗 best-effort(含傳輸例外等價類);四例皆回正常回覆、不擲例外、無 invoke
    // ================================================================
    public static IEnumerable<object[]> CatalogFailureErrors() => new[]
    {
        new object[] { new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 502") },
        new object[] { new HttpRequestException("connection reset") },
        new object[] { new TaskCanceledException("timeout") },
        new object[] { new WorkflowInvocationException("工作流服務呼叫失敗：回應不是有效 JSON") }, // 壞 JSON
    };

    [Theory]
    [MemberData(nameof(CatalogFailureErrors))]
    public async Task A03_CatalogFailure_BestEffort_RepliesNormally_NoSkillInvoked(Exception error)
    {
        var chatClient = new FakeChatClient { Response = "純聊天回覆" };
        var wf = new FakeWorkflowService { ThrowOnCatalog = error };
        var svc = Build(new FakeLlmAgent(), wf, chatClient: chatClient);

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
    }

    // ================================================================
    // A-04:單一工具失敗不炸整輪(含傳輸例外等價類);經 ChatAsync 驅動,不直接呼叫 tool.InvokeAsync
    // ================================================================
    public static IEnumerable<object[]> SkillInvokeFailureErrors() => new[]
    {
        new object[] { new WorkflowNotFoundException("找不到 Skill：s") },
        new object[] { new WorkflowForbiddenException("權限不足") },
        new object[] { new WorkflowBadInputException("輸入不符") },
        new object[] { new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 500") },
        new object[] { new HttpRequestException("connection reset") },
        new object[] { new TaskCanceledException("timeout") },
    };

    [Theory]
    [MemberData(nameof(SkillInvokeFailureErrors))]
    public async Task A04_SingleSkillFailure_DoesNotThrow_StillReturnsReply(Exception error)
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");                 // 路由命中
        agent.Responses.Enqueue("已如實轉達錯誤的摘要");       // 摘要(工具失敗文字被轉述,不炸)
        var wf = new FakeWorkflowService { Catalog = Cat(SingleSkillCatalog), ThrowOnSkillInvoke = error };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率多少?", "u1", "c1", UserA);

        Assert.Equal("已如實轉達錯誤的摘要", reply.Reply);
    }

    // ================================================================
    // A-05:角色過濾 — USER 看不到/呼叫不到 ADMIN skill,ADMIN 看得到/呼叫得到
    // ================================================================
    private const string RoleCatalog = """
    [
      { "name":"user_skill", "description":"一般查詢", "required_role":"USER", "source":"custom",
        "input_schema": { "query": { "type":"str", "required":true } } },
      { "name":"admin_only_skill", "description":"管理限定", "required_role":"ADMIN", "source":"custom",
        "input_schema": { "query": { "type":"str", "required":true } } }
    ]
    """;

    [Fact]
    public async Task A05a_UserRole_AdminOnlySkill_NeverInSkillInvokes()
    {
        var agent = new FakeLlmAgent();
        // USER 的路由表裡不存在 admin_only_skill,兩次路由都無法命中,退純聊天兜底(FakeChatClient 接手)。
        agent.Responses.Enqueue("admin_only_skill");
        agent.Responses.Enqueue("admin_only_skill");
        var wf = new FakeWorkflowService { Catalog = Cat(RoleCatalog) };
        var svc = Build(agent, wf);

        await svc.ChatAsync("管理報表", "u1", "c1", UserA);

        Assert.DoesNotContain(wf.SkillInvokes, i => i.Name == "admin_only_skill");
    }

    [Fact]
    public async Task A05b_AdminRole_AdminOnlySkill_Invoked()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("admin_only_skill");
        agent.Responses.Enqueue("報表摘要");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(RoleCatalog),
            SkillOutputByName = new()
            {
                ["admin_only_skill"] = Cat("""{ "skill":"admin_only_skill", "output": { "business_result":"報表內容" } }"""),
            },
        };
        var svc = Build(agent, wf);

        await svc.ChatAsync("管理報表", "u1", "c1", AdminA);

        Assert.Contains(wf.SkillInvokes, i => i.Name == "admin_only_skill");
    }

    // ================================================================
    // A-06:匿名 — 不路由(不讀目錄)、不執行 skill、不持久化聊天記錄(backend)、不讀寫 mem0。
    // ================================================================
    [Fact]
    public async Task A06a_Anonymous_Chat_NoCatalogRead_NoSkillInvoked_NoConversationPersisted()
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowService { Catalog = Cat(SingleSkillCatalog) };
        var svc = Build(new FakeLlmAgent(), wf, mem0, convos, new FakeChatClient { Response = "匿名回覆" });

        await svc.ChatAsync("嗨", "u1", "c1"); // userCtx = null

        Assert.Empty(wf.CatalogContexts);
        Assert.Empty(wf.SkillInvokes);
        Assert.Empty(convos.Saved);
        Assert.Empty(mem0.Recalled);
        Assert.Empty(mem0.Remembered);
    }

    [Fact]
    public async Task A06b_Anonymous_Stream_NoCatalogRead_NoSkillInvoked_NoConversationPersisted()
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowService { Catalog = Cat(SingleSkillCatalog) };
        var svc = Build(new FakeLlmAgent(), wf, mem0, convos, new FakeChatClient { Chunks = new[] { "甲", "乙" } });

        await foreach (var _ in svc.StreamChatAsync("嗨", "u1", "c1")) // userCtx = null
        {
        }

        Assert.Empty(wf.CatalogContexts);
        Assert.Empty(wf.SkillInvokes);
        Assert.Empty(convos.Saved);
        Assert.Empty(mem0.Recalled);
        Assert.Empty(mem0.Remembered);
    }

    // ================================================================
    // A-07:內建 template_* 骨架永不可路由,同前綴的 custom skill 可路由
    // ================================================================
    [Fact]
    public async Task A07_BuiltinTemplatePrefix_NeverRouted_CustomSamePrefix_IsRouted()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [
              { "name":"template_infer", "description":"骨架", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"template_x", "description":"自訂骨架同名前綴", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } }
            ]
            """),
            SkillOutputByName = new()
            {
                ["template_x"] = Cat("""{ "skill":"template_x", "output": { "business_result":"custom 命中" } }"""),
            },
        };

        // 第一輪:路由試圖選 builtin template_infer(它根本不在路由表裡,兩次嘗試都無法命中)。
        var agent1 = new FakeLlmAgent();
        agent1.Responses.Enqueue("template_infer");
        agent1.Responses.Enqueue("template_infer");
        await Build(agent1, wf).ChatAsync("骨架問題", "u1", "c1", UserA);

        Assert.Empty(wf.SkillInvokes);

        // 第二輪:路由選 custom template_x(同前綴但 source=custom,不受過濾)。
        var agent2 = new FakeLlmAgent();
        agent2.Responses.Enqueue("template_x");
        agent2.Responses.Enqueue("custom 摘要");
        await Build(agent2, wf).ChatAsync("custom 問題", "u2", "c2", UserA);

        Assert.Equal(new[] { "template_x" }, wf.SkillInvokes.Select(i => i.Name).ToArray());
    }

    // ================================================================
    // A-08:input_schema 天花板(SingleRequiredStringKey)— 三種形狀靜默跳過,一種可路由且只帶必填鍵
    // ================================================================
    public static IEnumerable<object[]> SchemaVariants() => new object[][]
    {
        new object[] { "null", false },                                                                       // input_schema=null
        new object[] { """{ "a": { "type":"str", "required":true }, "b": { "type":"str", "required":true } }""", false }, // 兩個必填
        new object[] { """{ "n": { "type":"int", "required":true } }""", false },                              // 必填非字串
        new object[] { """{ "query": { "type":"str", "required":true }, "top_k": { "type":"int", "required":false } }""", true }, // 單必填字串+多選填
    };

    [Theory]
    [MemberData(nameof(SchemaVariants))]
    public async Task A08_SchemaShape_GatesRouting_SilentlySkippedOrInvokedWithOnlyRequiredKey(
        string schemaJson, bool shouldInvoke)
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat($$"""
            [ { "name":"weird_skill", "description":"x", "required_role":"USER", "source":"custom",
                "input_schema": {{schemaJson}} } ]
            """),
            SkillOutput = Cat("""{ "skill":"weird_skill", "output": { "business_result":"命中" } }"""),
        };
        var chatClient = new FakeChatClient();
        var svc = Build(agent, wf, chatClient: chatClient);

        if (shouldInvoke)
        {
            agent.Responses.Enqueue("weird_skill");
            agent.Responses.Enqueue("摘要輸出");
        }
        else
        {
            chatClient.Response = "純聊天回覆"; // 工具不存在,兩次路由皆無法命中,退純聊天兜底(FakeChatClient 接手)。
        }

        var reply = await svc.ChatAsync("原文問句", "u1", "c1", UserA);

        if (shouldInvoke)
        {
            var invoke = Assert.Single(wf.SkillInvokes);
            Assert.Equal(new[] { "query" }, invoke.Input.Keys.ToArray()); // 只帶必填鍵,不捏造 optional
            Assert.Equal("原文問句", invoke.Input["query"].GetString());
            Assert.Equal("摘要輸出", reply.Reply);
        }
        else
        {
            Assert.Empty(wf.SkillInvokes);
            Assert.Equal("純聊天回覆", reply.Reply);
        }
    }

    // ================================================================
    // A-09:路由最多重試兩次的 on/off-point
    // ================================================================
    [Fact]
    public async Task A09a_FirstAttemptNone_SecondAttemptHits_RetrySucceeds()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");
        agent.Responses.Enqueue("kb_query");
        agent.Responses.Enqueue("摘要輸出");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb_query", Assert.Single(wf.SkillInvokes).Name);
    }

    [Fact]
    public async Task A09b_BothAttemptsNone_ExhaustsRetry_FallsBackToPlainChat()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");
        agent.Responses.Enqueue("NONE");
        var chatClient = new FakeChatClient { Response = "純聊天回覆" };
        var wf = new FakeWorkflowService { Catalog = Cat(SingleSkillCatalog) };
        var svc = Build(agent, wf, chatClient: chatClient);

        var reply = await svc.ChatAsync("你好呀", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
    }

    [Fact]
    public async Task A09c_FirstAttemptHits_NoWastedRetry()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");
        agent.Responses.Enqueue("摘要輸出");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb_query", Assert.Single(wf.SkillInvokes).Name);
    }

    // ================================================================
    // A-10:寬鬆比對先全等再取最長名(kb_query 而非其前綴 kb)
    // ================================================================
    [Fact]
    public async Task A10_LenientMatch_PicksLongestToolName_NotShorterPrefix()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("我建議使用 kb_query 這個工具"); // 非全等,同時含 kb 與 kb_query 的 token
        agent.Responses.Enqueue("摘要輸出");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [
              { "name":"kb", "description":"短名", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"kb_query", "description":"長名", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } }
            ]
            """),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        await svc.ChatAsync("問題", "u1", "c1", UserA);

        Assert.Equal("kb_query", Assert.Single(wf.SkillInvokes).Name);
    }

    // ================================================================
    // A-11:kb_query 棄答(ABSTAIN)→ 確定性兜底打 rag_qa,順序必須是 kb_query → rag_qa
    // ================================================================
    [Fact]
    public async Task A11_KbQueryAbstain_FallsBackToRagQa_InOrder_WithHonestLabelInSummaryMessage()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");
        agent.Responses.Enqueue("依證據不足的誠實回覆");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [
              { "name":"kb_query", "description":"稽核檢索", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"rag_qa", "description":"一般知識庫問答", "required_role":"USER", "source":"builtin",
                "input_schema": { "question": { "type":"str", "required":true } } }
            ]
            """),
            SkillOutputByName = new()
            {
                ["kb_query"] = Cat("""{ "skill":"kb_query", "output": { "answer_mode":"ABSTAIN", "final_answer":"【無法提供答案】證據不足" } }"""),
                ["rag_qa"] = Cat("""{ "skill":"rag_qa", "output": { "answer":"rag 的答案" } }"""),
            },
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("寵物守則對貓的規定?", "u1", "c1", UserA);

        Assert.Equal(new[] { "kb_query", "rag_qa" }, wf.SkillInvokes.Select(i => i.Name).ToArray());
        // 誠實標示出現在送進最後一次(摘要)LLM 呼叫的訊息中——斷言面是「送進 ILlmAgent 的 message 清單」
        // 且用 Contains 驗證業務資料是否存在,不是驗證整段 prompt 逐字相等。
        Assert.Contains(agent.CompleteCalls[^1], m => m.Content.Contains("嚴格稽核查詢因證據不足而棄答"));
        Assert.Equal("依證據不足的誠實回覆", reply.Reply);
    }

    // ================================================================
    // A-12:kb_query 正常回答時不誤觸發 rag_qa 兜底
    // ================================================================
    [Fact]
    public async Task A12_KbQueryAnswersNormally_DoesNotTriggerRagQaFallback()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");
        agent.Responses.Enqueue("有憑據的答案摘要");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutputByName = new()
            {
                ["kb_query"] = Cat("""{ "skill":"kb_query", "output": { "answer_mode":"ANSWER", "final_answer":"有憑據的答案" } }"""),
            },
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("q", "u1", "c1", UserA);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("kb_query", invoke.Name);
        Assert.DoesNotContain(wf.SkillInvokes, i => i.Name == "rag_qa");
        Assert.Equal("有憑據的答案摘要", reply.Reply);
    }

    // ================================================================
    // A-13:mem0 recall-before / remember-after,remember 記融合後的最終回覆
    // ⚠️ 與 04 §3 表格描述有出入,見測試內註解。
    // ================================================================
    [Fact]
    public async Task A13_RoutedSkillPath_RememberIsFusedFinalReply_RecallIsNotInjectedOnThisPath()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");
        agent.Responses.Enqueue("最終融合摘要");
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者是租戶 A\n" };
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中答案" } }"""),
        };
        var svc = Build(agent, wf, mem0);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        // remember 記的是融合後最終回覆,不是中間 skill JSON,且發生在完整回覆產生「之後」
        // (mem0.Remembered 只有在 ChatAsync 整輪跑完才會被寫入,見production ChatAsync 的呼叫順序)。
        var remembered = Assert.Single(mem0.Remembered);
        Assert.Equal("最終融合摘要", remembered.AiReply);
        Assert.Equal("最終融合摘要", reply.Reply);

        // ⚠️ 現行行為與規格不符:TryRouteAndExecuteAsync 命中 skill 並產生 summaryMessages 時,
        // ChatAsync 直接用「裸」LLM 潤飾摘要,不會呼叫 mem0.RecallAsync(P2 之後,MISS 路徑才會 recall)。
        // recall 內容因此不會出現在任何一次送進 ILlmAgent 的 messages 中——與規格表格所寫的
        // 「recall 內容出現在送進 chat client 的 messages 中」相反。已依現行實際行為改寫斷言。
        Assert.DoesNotContain(agent.CompleteCalls.SelectMany(c => c), m => m.Content.Contains("租戶 A"));
    }

    // ================================================================
    // A-14:mem0 recall/remember 擲例外(補 G1)
    // ⚠️ 與 04 §3 表格描述有出入,見測試內註解。
    // ================================================================
    public static IEnumerable<object[]> Mem0FailureModes() => new object[][]
    {
        new object[] { true, false },  // (a) RecallAsync 擲例外
        new object[] { false, true },  // (b) RememberAsync 擲例外
    };

    // mem0 best-effort 是 pipeline 不變式:就算替換實作違反原本的「不拋例外」契約,聊天仍須正常完成。
    [Theory]
    [MemberData(nameof(Mem0FailureModes))]
    public async Task A14_ChatAsync_Mem0Failure_IsBestEffort_StillReplies(
        bool throwOnRecall, bool throwOnRemember)
    {
        var mem0 = new FakeMem0Client
        {
            ThrowOnRecall = throwOnRecall ? new InvalidOperationException("mem0 recall 失敗") : null,
            ThrowOnRemember = throwOnRemember ? new InvalidOperationException("mem0 remember 失敗") : null,
        };
        var wf = new FakeWorkflowService(); // 空目錄 → 無工具 → 純聊天兜底(才會走到 recall/MISS 路徑)
        var svc = Build(new FakeLlmAgent(), wf, mem0, chatClient: new FakeChatClient { Response = "回覆" });

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);
        Assert.Equal("回覆", reply.Reply);
    }

    [Theory]
    [MemberData(nameof(Mem0FailureModes))]
    public async Task A14_StreamChatAsync_Mem0Failure_IsBestEffort_StillStreams(
        bool throwOnRecall, bool throwOnRemember)
    {
        var mem0 = new FakeMem0Client
        {
            ThrowOnRecall = throwOnRecall ? new InvalidOperationException("mem0 recall 失敗") : null,
            ThrowOnRemember = throwOnRemember ? new InvalidOperationException("mem0 remember 失敗") : null,
        };
        var wf = new FakeWorkflowService();
        var svc = Build(new FakeLlmAgent(), wf, mem0, chatClient: new FakeChatClient { Chunks = new[] { "甲", "乙" } });

        var chunks = new List<string>();
        await foreach (var chunk in svc.StreamChatAsync("問題", "u1", "c1", UserA))
        {
            chunks.Add(chunk);
        }
        Assert.Equal(new[] { "甲", "乙" }, chunks);
    }

    // ================================================================
    // A-18:同一 conversationId 連續兩輪,第二輪送進 chat client 的 messages 含第一輪內容
    // P2:純聊天改跑共用 hosted agent,斷言對象從 agent.LastMessages 換成 FakeChatClient 收到的 messages
    // (斷言本身不變:第二輪要看得到「第一問」「第一答」)。
    // ================================================================
    [Fact]
    public async Task A18_TwoRoundsSameConversation_SecondRoundIncludesFirstRoundExchange()
    {
        var chatClient = new FakeChatClient { Response = "第一答" };
        var wf = new FakeWorkflowService(); // 空目錄 → 純聊天兜底,才會走短期記憶注入
        var svc = Build(new FakeLlmAgent(), wf, chatClient: chatClient);

        await svc.ChatAsync("第一問", "u1", "c1", UserA);
        chatClient.Response = "第二答";
        await svc.ChatAsync("第二問", "u1", "c1", UserA);

        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.User && m.Text == "第一問");
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.Assistant && m.Text == "第一答");
        Assert.Equal("第二問", chatClient.LastMessages!.Last().Text);
    }

    // ================================================================
    // A-19:短期記憶視窗 20 則 on-point / 21 則 off-point(於 ChatService 層驗證,補 G6)
    // P2:自寫 InMemoryChatMemoryStore 已刪除,改以 historyProvider.SetMessages 直接播種
    // 「已累積出 20 則歷史」這個前置狀態(取代舊的 memory.Append 播種方式);受測動作(再發一輪)
    // 仍經 ChatAsync 驅動,斷言送進共用 hosted agent 的 messages(允許的斷言面)。
    // ================================================================
    [Fact]
    public async Task A19a_History20_OnPoint_AllRetained()
    {
        var chatClient = new FakeChatClient { Response = "本輪回覆" };
        var (svc, hostAgent, historyProvider) = BuildWithAgent(new FakeLlmAgent(), new FakeWorkflowService(), chatClient: chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("conv-a19a");
        var seeded = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"hist{i}"));
        }
        historyProvider.SetMessages(session, seeded);
        await hostAgent.SaveSessionAsync("conv-a19a", session);

        // 匿名 + 非空白 conversationId 時 cid 直接等於 conversationId(ChatService.DeriveMemoryKeys)。
        await svc.ChatAsync("本輪提問", "", "conv-a19a");

        for (var i = 0; i < 20; i++)
        {
            Assert.Contains(chatClient.LastMessages!, m => m.Text == $"hist{i}");
        }
        Assert.Equal("本輪提問", chatClient.LastMessages!.Last().Text);
    }

    [Fact]
    public async Task A19b_History21_OffPoint_OldestTrimmed_Other20Retained()
    {
        var chatClient = new FakeChatClient { Response = "第一輪回覆" };
        var (svc, hostAgent, historyProvider) = BuildWithAgent(new FakeLlmAgent(), new FakeWorkflowService(), chatClient: chatClient);

        var session = await hostAgent.GetOrCreateSessionAsync("conv-a19b");
        var seeded = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
        {
            seeded.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"hist{i}"));
        }
        historyProvider.SetMessages(session, seeded);
        await hostAgent.SaveSessionAsync("conv-a19b", session);

        // 第一輪:20(已存)+ 2(這輪 user+assistant)= 22,觸發裁切——裁切發生在「回覆之後」寫回 session
        // 的階段,這一輪自己送出的訊息列仍是裁切前的完整 20+1(spike 實測)。匿名 + 非空白 conversationId
        // 時 cid 直接等於 conversationId(ChatService.DeriveMemoryKeys)。
        await svc.ChatAsync("第一輪觸發", "", "conv-a19b");

        chatClient.Response = "第二輪回覆";
        // 第二輪才看得到裁切後的結果:最舊整個 turn(hist0/hist1)被裁掉,其餘保留。
        await svc.ChatAsync("第二輪確認", "", "conv-a19b");

        // 裁切以整個 turn 為原子單位(B-P2-03):hist0(user)+hist1(assistant)一起被裁,不是只裁一則。
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "hist0");
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "hist1");
        for (var i = 2; i < 20; i++)
        {
            Assert.Contains(chatClient.LastMessages!, m => m.Text == $"hist{i}");
        }
        Assert.Equal("第二輪確認", chatClient.LastMessages!.Last().Text);
    }

    // ================================================================
    // A-20:已登入,body 的 userId 被忽略;跨使用者互不看見;mem0 uid 取自 JWT("{tenant}:{user}" 形狀)
    // P2:純聊天改跑共用 hosted agent,隔離斷言的對象從 agent.LastMessages 換成 FakeChatClient
    // 收到的 messages——斷言本身不變(不可弱化)。
    // ================================================================
    [Fact]
    public async Task A20_LoggedIn_BodyUserIdIgnored_CrossUserIsolated_Mem0UidFromJwtIdentity()
    {
        var chatClient = new FakeChatClient { Response = "答" };
        var mem0 = new FakeMem0Client();
        var svc = Build(new FakeLlmAgent(), new FakeWorkflowService(), mem0, chatClient: chatClient);

        var userB = new UserContext("user-b", "demo-a", "USER");

        // 兩位不同使用者刻意送「相同」的 body userId 與 conversationId(撞 key 的攻擊情境)。
        await svc.ChatAsync("A 的秘密", userId: "別人", conversationId: "shared", UserA);
        await svc.ChatAsync("B 問一句", userId: "別人", conversationId: "shared", userB);

        // 短期視窗 key 綁 JWT 身分 → B 看不到 A 的前一輪。
        Assert.DoesNotContain(chatClient.LastMessages!, m => m.Text == "A 的秘密");

        // mem0 uid 一律用 JWT 身分("{tenant}:{user}"),body 的 userId="別人" 完全不採用。
        Assert.Equal("demo-a:user-a", mem0.Remembered[0].UserId);
        Assert.Equal("demo-a:user-b", mem0.Remembered[1].UserId);
    }
}
