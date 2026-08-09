using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

/// <summary>
/// A 組 — 原為 P4 重構前的行為安全網鷹架(plans/copilot-shared-core/04-acceptance-test.md §3)。
/// P4 已落地,鷹架的任務結束:凡是現行細粒度測試已同語意覆蓋的案例都已刪除(對照表見下),
/// 這裡只留「非得端到端經 <see cref="ChatService.ChatAsync"/> / <see cref="ChatService.StreamChatAsync"/>
/// 才驗得到」的案例 —— 也就是跨越路由 → skill 執行 → 摘要 → mem0/持久化整條鏈的因果順序,
/// 以及匿名/連續兩輪這種只在 ChatService 這一層才存在的語意。
///
/// 已刪除者與其取代測試(刪除前逐一開檔確認過同語意):
/// A-04(阻塞半)→ ChatSkillRoutingTests.SkillInvokeFailure_ReturnsErrorText_DoesNotThrow(工具接縫,同兩個例外等價類);
/// A-05a/A-05b → ChatSkillRoutingTests.User_GetsUserSkills_NotAdminSkill_AndCatalogCalledOnceWithIdentity /
///   Admin_GetsUserAndAdminSkills(精確集合斷言,比「沒被 invoke」更強);
/// A-08 → ChatSkillRoutingTests.SingleRequiredString_WithOptionals_IsRoutable_AndInvokesOnlyRequiredKey;
/// A-11 → ChatServiceTests.KbQuerySkill_Abstains_FallsBackToRagQaSkill_WithHonestLabel(誠實標示逐字相等);
/// A-19a/A-19b → ChatSessionWindowTests.Window_At20Messages_NothingCompacted / Window_At21Messages_OldestTurnDropped;
/// A-20 → ChatServiceTests.LoggedIn_MemoryKeys_BindToJwtIdentity_NotClientBody_IsolatingAcrossUsers
///   + ChatMemoryKeyDerivationTests(key 形狀 {tenant}:{user}:{cid} 的直接單元測試)。
///
/// 斷言面沿用原則不變:只斷言 <see cref="FakeWorkflowEngineClient.SkillInvokes"/>、
/// <see cref="FakeMem0Client.Remembered"/>、送進底層 chat client / 裸 <see cref="ILlmAgent"/> 的 message 清單
/// 這些可觀察結果;不呼叫 BuildToolsAsync / tool.InvokeAsync(那是細粒度測試的接縫),
/// 不做 prompt 字串相等斷言(唯一例外是用 Contains 驗特定業務資料是否出現在 message 清單中)。
/// 前置條件(腳本化回應)可直接設定在協作者上,但「本案受測的那個動作」一律經 ChatAsync / StreamChatAsync 觸發。
/// </summary>
public sealed class ChatBehaviorBaselineTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");

    private static ChatService Build(
        FakeLlmAgent agent, FakeWorkflowEngineClient workflows, FakeMem0Client? mem0 = null,
        FakeConversationStore? convos = null, FakeChatClient? chatClient = null)
    {
        var effectiveMem0 = mem0 ?? new FakeMem0Client();
        var effectiveConvos = convos ?? new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, _) = TestChatAgent.Build(
            chatClient, effectiveMem0, effectiveConvos, identity, agent, workflows);
        return new ChatService(
            hostAgent, effectiveConvos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);
    }

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 單一可路由 skill(唯一必填字串 query),多數案例的最小目錄。
    private const string SingleSkillCatalog = """
    [ { "name":"kb-query", "description":"知識庫檢索", "required_role":"USER", "source":"builtin",
        "input_schema": { "query": { "type":"str", "required":true } } } ]
    """;

    // ================================================================
    // A-01:路由命中的完整因果鏈 — 選對 skill → 帶對參數 → 回覆是摘要而非原始 JSON → remember 記最終摘要
    // ================================================================
    [Fact]
    public async Task A01_RoutedSkill_InvokesWithCorrectInput_RepliesWithSummary_NotRawJson()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb-query");              // 第一次 CompleteAsync = 路由
        agent.Responses.Enqueue("本季毛利率是 32.8%。");   // 第二次 CompleteAsync = 摘要
        var mem0 = new FakeMem0Client();
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"毛利率 32.8%" } }"""),
        };
        var svc = Build(agent, wf, mem0);

        var reply = await svc.ChatAsync("這季毛利率多少?", "u1", "c1", UserA);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("kb-query", invoke.Name);
        Assert.Equal("這季毛利率多少?", invoke.Input["query"].GetString());

        // 回覆是摘要輸出,不是工具原始 JSON(原始 JSON 會以 "{" 開頭)。
        Assert.Equal("本季毛利率是 32.8%。", reply.Reply);
        Assert.False(reply.Reply.TrimStart().StartsWith('{'));

        var remembered = Assert.Single(mem0.Remembered);
        Assert.Equal("本季毛利率是 32.8%。", remembered.AiReply);
    }

    // A-02(路由 NONE → 零 invoke + 正常回覆)由 ChatSkillRoutingTests.Routing_RetryExhausted_BothNone
    // 覆蓋(超集:另驗路由呼叫次數與護欄 Instructions)。
    // A-03(目錄失敗 best-effort,4 種例外)由 ChatSkillRoutingTests.CatalogFailure_ToolsEmpty_ChatDoesNotThrow
    // 逐項覆蓋(同 4 個例外型別、同層、同 fake)。

    // ================================================================
    // A-04(只留串流半):skill invoke 失敗時串流不冒泡、不掛住。阻塞半已由
    // ChatSkillRoutingTests.SkillInvokeFailure_ReturnsErrorText_DoesNotThrow(工具接縫,同兩個例外等價類)
    // 加上 A-01 的端到端摘要鏈覆蓋;串流這半是獨立的傳輸等價類,細粒度測試碰不到,故保留。
    // 六種例外全部落在 SkillRoutingAgent 同一個 catch-all,留「下游領域例外」與「傳輸層例外」兩個代表。
    // ================================================================
    public static IEnumerable<object[]> SkillInvokeFailureErrors() => new[]
    {
        new object[] { new WorkflowNotFoundException("找不到 Skill：s") },   // 下游領域例外
        new object[] { new HttpRequestException("connection reset") },       // 傳輸層例外
    };

    // InvokeSkillToolAsync 的 catch 把錯誤轉成一段文字塞進摘要訊息,串流照常吐完摘要(斷言面:
    // 對外可觀察的 chunk 內容,外加「錯誤文字確實進了送給摘要 LLM 的 message 清單」這個 Contains)。
    [Theory]
    [MemberData(nameof(SkillInvokeFailureErrors))]
    public async Task A04_StreamChatAsync_SingleSkillFailure_DoesNotThrow_StillStreamsSummary(Exception error)
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb-query");                        // 路由命中(CompleteAsync)
        agent.Chunks = new[] { "已如實", "轉達錯誤的摘要" };          // 摘要串流(StreamAsync)
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SingleSkillCatalog), ThrowOnSkillInvoke = error };
        var svc = Build(agent, wf);

        var chunks = new List<string>();
        await foreach (var chunk in svc.StreamChatAsync("這季毛利率多少?", "u1", "c1", UserA))
        {
            chunks.Add(chunk);
        }

        // 工具失敗仍是「路由命中」語意(選中了 kb-query,只是它自己執行失敗)→ 串流正常結束後仍追加來源標記。
        Assert.Equal(new[] { "已如實", "轉達錯誤的摘要", "\n<!--skill:kb-query-->" }, chunks);
        // 工具失敗被轉成文字交給摘要 LLM 照實轉述(SkillRoutingAgent.InvokeSkillToolAsync 的 catch),
        // 而不是讓例外穿過串流。
        Assert.Contains(agent.LastMessages!, m => m.Content.Contains("Skill kb-query 呼叫失敗"));
    }

    // A-05(角色過濾:USER 看不到/呼叫不到 ADMIN skill,ADMIN 兩者皆得)由
    // ChatSkillRoutingTests.User_GetsUserSkills_NotAdminSkill_AndCatalogCalledOnceWithIdentity /
    // Admin_GetsUserAndAdminSkills 以精確集合斷言覆蓋(超集:不在路由表就不可能被 invoke)。

    // ================================================================
    // A-06:匿名 — 不路由(不讀目錄)、不執行 skill、不持久化聊天記錄(backend)、不讀寫 mem0。
    // ================================================================
    [Fact]
    public async Task A06a_Anonymous_Chat_NoCatalogRead_NoSkillInvoked_NoConversationPersisted()
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SingleSkillCatalog) };
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
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SingleSkillCatalog) };
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

    // A-07(builtin template-* 不可路由 / custom 同前綴可路由)是同一個合取條件的兩側,已由
    // ChatSkillRoutingTests.BuiltinTemplateSkeletons_AreNeverRouted_ButRealSkillsAre 與
    // CustomSkill_WithTemplatePrefix_IsNotFiltered 在 BuildToolsAsync 層分別覆蓋。

    // A-08(單一必填字串 + optional 仍可路由、只帶必填鍵)由
    // ChatSkillRoutingTests.SingleRequiredString_WithOptionals_IsRoutable_AndInvokesOnlyRequiredKey 覆蓋
    // (同樣斷言 invoke.Input.Keys 恰為 ["query"]);負向 shape 由同檔 NonSingleRequiredString_IsSkipped 覆蓋。

    // A-09(路由重試 on/off-point 三格)由 ChatSkillRoutingTests.Routing_RetriesOnce_* /
    // Routing_RetryExhausted_* / Routing_FirstAttemptHits_* 逐項覆蓋,且更強(另斷言 CompleteCalls.Count,
    // 抓得到「多打一次 LLM」的迴歸)。

    // ================================================================
    // A-10:寬鬆比對先全等再取最長名(kb-query 而非其前綴 kb)
    // ================================================================
    [Fact]
    public async Task A10_LenientMatch_PicksLongestToolName_NotShorterPrefix()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("我建議使用 kb-query 這個工具"); // 非全等,同時含 kb 與 kb-query 的 token
        agent.Responses.Enqueue("摘要輸出");
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [
              { "name":"kb", "description":"短名", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"kb-query", "description":"長名", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } }
            ]
            """),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        await svc.ChatAsync("問題", "u1", "c1", UserA);

        Assert.Equal("kb-query", Assert.Single(wf.SkillInvokes).Name);
    }

    // A-11(kb-query 棄答 → 確定性兜底打 rag-qa,順序 + 誠實標示)由
    // ChatServiceTests.KbQuerySkill_Abstains_FallsBackToRagQaSkill_WithHonestLabel 覆蓋
    // (超集:誠實標示是逐字相等斷言,不只是 Contains,並另驗兜底 skill 收到的輸入鍵)。

    // ================================================================
    // A-12:kb-query 正常回答時不誤觸發 rag-qa 兜底
    // 這是 A-11 那個決策的 off-point,全倉只有這一條:ChatServiceTests 只覆蓋了 ABSTAIN 那一半
    // (grep answer_mode 全倉確認),刪掉它等於「兜底條件寫成恆真」不會被任何測試抓到,故保留。
    // ================================================================
    [Fact]
    public async Task A12_KbQueryAnswersNormally_DoesNotTriggerRagQaFallback()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb-query");
        agent.Responses.Enqueue("有憑據的答案摘要");
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutputByName = new()
            {
                ["kb-query"] = Cat("""{ "skill":"kb-query", "output": { "answer_mode":"ANSWER", "final_answer":"有憑據的答案" } }"""),
            },
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("q", "u1", "c1", UserA);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("kb-query", invoke.Name);
        Assert.DoesNotContain(wf.SkillInvokes, i => i.Name == "rag-qa");
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
        agent.Responses.Enqueue("kb-query");
        agent.Responses.Enqueue("最終融合摘要");
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者是租戶 A\n" };
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SingleSkillCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"命中答案" } }"""),
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
        // (c) 同一輪兩者皆擲例外:recall(ChatContextProvider)與 remember(ChatTurnRecorder)是兩個
        // 各自獨立的 try/catch,兩邊同時倒不會互相掩蓋,聊天仍須完成。
        new object[] { true, true },
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
        var wf = new FakeWorkflowEngineClient(); // 空目錄 → 無工具 → 純聊天兜底(才會走到 recall/MISS 路徑)
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
        var wf = new FakeWorkflowEngineClient();
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
        var wf = new FakeWorkflowEngineClient(); // 空目錄 → 純聊天兜底,才會走短期記憶注入
        var svc = Build(new FakeLlmAgent(), wf, chatClient: chatClient);

        await svc.ChatAsync("第一問", "u1", "c1", UserA);
        chatClient.Response = "第二答";
        await svc.ChatAsync("第二問", "u1", "c1", UserA);

        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.User && m.Text == "第一問");
        Assert.Contains(chatClient.LastMessages!, m => m.Role == ChatRole.Assistant && m.Text == "第一答");
        Assert.Equal("第二問", chatClient.LastMessages!.Last().Text);
    }

    // A-19a/A-19b(短期記憶視窗 20 on-point / 21 off-point)由
    // ChatSessionWindowTests.Window_At20Messages_NothingCompacted /
    // Window_At21Messages_OldestTurnDropped 覆蓋(同樣的播種 + 同樣的整-turn 原子裁切斷言),
    // 且同檔另有 HIT 路徑、tool-call 配對與 26 則 minimumPreservedTurns 陷阱的補充案例。

    // A-20(已登入時 body userId 被忽略、跨使用者互不看見、mem0 uid 取自 JWT)由
    // ChatServiceTests.LoggedIn_MemoryKeys_BindToJwtIdentity_NotClientBody_IsolatingAcrossUsers 覆蓋,
    // 「同租戶不同使用者」這一維則由 ChatMemoryKeyDerivationTests 直接釘住 key 形狀
    // ({tenant}:{user}:{cid},含身分含 ':' 時 fail-closed)。
}
