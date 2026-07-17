using System.Text.Json;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Service.Tests;

/// <summary>
/// 聊天 → Skill 路由(CSR-P1)。工具來源為動態 Skill 目錄(BuildToolsAsync)。
/// 註:新流程下 LLM 不再拿到原生 tools 引數;路由表(BuildToolsAsync 產出)改由測試直接驗證,
/// 端到端的「路由 → 執行 → 摘要」編排另見本檔末的 orchestration 區。
/// template_* 內建骨架是空殼、不可路由;可路由範例用非 template 名(builtin kb_query、custom tenant_a_private_search)。
/// </summary>
public sealed class ChatSkillRoutingTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");
    private static readonly UserContext AdminA = new("admin-a", "demo-a", "ADMIN");

    private static ChatService Build(FakeLlmAgent agent, FakeWorkflowService workflows)
        => new(agent, new InMemoryChatMemoryStore(), new FakeMem0Client(), new FakeConversationStore(),
            workflows, new LlmOptions(), NullLogger<ChatService>.Instance);

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 04 §2.2 的目錄樣本(template_retrieval → kb_query 以符合 template_* 過濾規則)。
    private const string SampleCatalog = """
    [
      { "name":"kb_query", "description":"從知識庫檢索答案", "required_role":"USER", "source":"builtin", "revision":1,
        "input_schema": { "query": { "type":"str", "required":true, "min_length":1 } } },
      { "name":"tenant_a_private_search", "description":"租戶 A 的專用檢索", "required_role":"USER", "source":"custom", "revision":3,
        "input_schema": { "question_text": { "type":"str", "required":true } } },
      { "name":"admin_report", "description":"管理報表", "required_role":"ADMIN", "source":"custom", "revision":2,
        "input_schema": { "prompt": { "type":"str", "required":true } } }
    ]
    """;

    // ---- T1 / CSR-P1-001:USER 只取得可用 Skill ----
    [Fact]
    public async Task User_GetsUserSkills_NotAdminSkill_AndCatalogCalledOnceWithIdentity()
    {
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Contains("kb_query", names);
        Assert.Contains("tenant_a_private_search", names);
        Assert.DoesNotContain("admin_report", names);

        // catalog 恰呼叫一次,並收到 tenant A / USER 身分。
        var ctx = Assert.Single(wf.CatalogContexts);
        Assert.Equal("demo-a", ctx.TenantCode);
        Assert.Equal("USER", ctx.Role);
    }

    // ---- T2 / CSR-P1-002:ADMIN 取得 USER 與 ADMIN Skill ----
    [Fact]
    public async Task Admin_GetsUserAndAdminSkills()
    {
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(AdminA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Contains("kb_query", names);
        Assert.Contains("tenant_a_private_search", names);
        Assert.Contains("admin_report", names);
    }

    // ---- T3 / CSR-P1-003:匿名裸聊且不讀目錄 ----
    [Fact]
    public async Task Anonymous_ReturnsNullTools_AndNeverReadsCatalog()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        await svc.ChatAsync("問題", "u1", "c1"); // userCtx = null

        Assert.Null(agent.LastTools);
        Assert.Empty(wf.CatalogContexts);   // 目錄一次都沒讀
        Assert.Empty(wf.SkillInvokes);
    }

    [Fact]
    public async Task Anonymous_Stream_NeverReadsCatalog()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        await foreach (var _ in svc.StreamChatAsync("問題", "u1", "c1"))
        {
        }

        Assert.Null(agent.LastTools);
        Assert.Empty(wf.CatalogContexts);
    }

    // ---- T4 / CSR-P1-004:builtin 與 custom 路由地位相同 ----
    [Fact]
    public async Task BuiltinAndCustom_BothBecomeTools_SourceDoesNotMatter()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [
              { "name":"kb_query", "description":"內建檢索", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"tenant_a_private_search", "description":"自訂檢索", "required_role":"USER", "source":"custom",
                "input_schema": { "question_text": { "type":"str", "required":true } } }
            ]
            """),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Contains("kb_query", names);
        Assert.Contains("tenant_a_private_search", names);
    }

    // ---- T5 / CSR-P1-009,010:schema 天花板,非單一必填字串一律跳過 ----
    [Theory]
    [InlineData("null")]                                                                     // input_schema=null
    [InlineData("{}")]                                                                       // 空 object
    [InlineData("""{ "q": { "type":"str", "required":false } }""")]                          // 只有 optional str
    [InlineData("""{ "n": { "type":"int", "required":true } }""")]                           // 只有 required int
    [InlineData("""{ "a": { "type":"str", "required":true }, "b": { "type":"str", "required":true } }""")]   // 兩個 required str
    [InlineData("""{ "a": { "type":"str", "required":true }, "n": { "type":"int", "required":true } }""")]   // required str + required int
    public async Task NonSingleRequiredString_IsSkipped(string schema)
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat($$"""
            [ { "name":"weird_skill", "description":"x", "required_role":"USER", "source":"custom", "input_schema": {{schema}} } ]
            """),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.DoesNotContain(tools!, t => t.Name == "weird_skill");
    }

    // ---- CSR-P1-011:optional 欄位不破壞單參資格 ----
    [Fact]
    public async Task SingleRequiredString_WithOptionals_IsRoutable_AndInvokesOnlyRequiredKey()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [ { "name":"has_optionals", "description":"x", "required_role":"USER", "source":"custom",
                "input_schema": {
                  "query": { "type":"str", "required":true },
                  "top_k": { "type":"int", "required":false },
                  "lang": { "type":"str", "required":false }
                } } ]
            """),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "has_optionals");
        await tool.InvokeAsync("原文問句", CancellationToken.None);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("has_optionals", invoke.Name);
        // 只帶必填 str,不捏造 optional 值。
        Assert.Equal(new[] { "query" }, invoke.Input.Keys.ToArray());
        Assert.Equal("原文問句", invoke.Input["query"].GetString());
    }

    // ---- CSR-P1-012:非陣列 catalog 不產生動態工具(單軌後即無任何工具,不拋例外) ----
    [Theory]
    [InlineData("""{ "not":"an-array" }""")]
    [InlineData("\"just-a-string\"")]
    [InlineData("null")]
    public async Task NonArrayCatalog_ProducesNoTools_DoesNotThrow(string catalogJson)
    {
        var wf = new FakeWorkflowService { Catalog = Cat(catalogJson) };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.Empty(tools!);
    }

    // ---- 跨案關鍵修正:template_* 內建骨架不可被路由 ----
    [Fact]
    public async Task BuiltinTemplateSkeletons_AreNeverRouted_ButRealSkillsAre()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [
              { "name":"template_retrieval", "description":"檢索骨架", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"template_stats", "description":"統計骨架", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"kb_query", "description":"真的內建檢索", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } }
            ]
            """),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.DoesNotContain("template_retrieval", names);
        Assert.DoesNotContain("template_stats", names);
        Assert.Contains("kb_query", names);
    }

    // 過濾條件是 source=="builtin" 且 template_ 前綴的合取:custom 的 template_ 前綴不被剝除。
    [Fact]
    public async Task CustomSkill_WithTemplatePrefix_IsNotFiltered()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""
            [ { "name":"template_custom_thing", "description":"x", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } } ]
            """),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.Contains(tools!, t => t.Name == "template_custom_thing");
    }

    // ---- T7 / CSR-P1-008,015:呼叫正確 Skill、輸入鍵與身分 ----
    [Fact]
    public async Task SelectedTool_InvokesCorrectSkill_WithInputKeyAndIdentity()
    {
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "tenant_a_private_search");

        await tool.InvokeAsync("比較 Q1 與 Q2", CancellationToken.None);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("tenant_a_private_search", invoke.Name);
        // input_schema 的必填鍵是 question_text(非模型看到的 question)。
        Assert.Equal("比較 Q1 與 Q2", invoke.Input["question_text"].GetString());
        Assert.Equal("demo-a", invoke.Ctx.TenantCode);
        Assert.Equal("USER", invoke.Ctx.Role);
    }

    // ---- T8 / CSR-P1-016:標準 output key 取值 ----
    [Theory]
    [InlineData("answer", "A")]
    [InlineData("final_answer", "B")]
    [InlineData("report", "C")]
    [InlineData("summary", "D")]
    public async Task StandardOutputKey_IsExtracted_NotWholeJson(string key, string value)
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat($$"""{ "skill":"s", "output": { "{{key}}": "{{value}}" } }"""),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal(value, result);
    }

    // ---- business_result 優先:nl_logic 套規則後的權威答案須先於 final_answer(套規則前) ----
    [Fact]
    public async Task BusinessResult_TakesPriorityOver_FinalAnswer()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "final_answer":"套規則前", "business_result":"套規則後" } }"""),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("套規則後", result);
    }

    // 只有 business_result(infer/inspire/compare/stats 型)→ 取它,不落 GetRawText 倒整包 state JSON。
    [Fact]
    public async Task BusinessResultOnly_IsExtracted_NotRawStateJson()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "business_result":"答案" } }"""),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("答案", result);
    }

    // ---- CSR-P1-017:非標準輸出回傳 raw JSON(不遺失資訊、不拋例外) ----
    [Fact]
    public async Task NonStandardOutput_ReturnsRawJsonOfOutputObject()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "rows":[1,2], "count":2 } }"""),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        using var parsed = JsonDocument.Parse(result); // 合法 JSON
        Assert.Equal(2, parsed.RootElement.GetProperty("count").GetInt32());
        Assert.False(parsed.RootElement.TryGetProperty("skill", out _)); // 取的是 output 內層
    }

    [Fact]
    public async Task OutputWithoutWrapper_FallsBackToRootRawJson()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "foo":"bar" }"""),   // 沒有 output 外層、也無標準 key
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        using var parsed = JsonDocument.Parse(result);
        Assert.Equal("bar", parsed.RootElement.GetProperty("foo").GetString());
    }

    // ---- T9 / CSR-P1-023:單一 Skill invoke 失敗不炸整輪 ----
    public static IEnumerable<object[]> SkillInvokeErrors() => new[]
    {
        new object[] { new WorkflowNotFoundException("找不到 Skill：s") },
        new object[] { new WorkflowForbiddenException("權限不足") },
        new object[] { new WorkflowBadInputException("輸入不符") },
        new object[] { new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 500") },
        new object[] { new HttpRequestException("connection reset") },
        new object[] { new TaskCanceledException("timeout") },
    };

    [Theory]
    [MemberData(nameof(SkillInvokeErrors))]
    public async Task SkillInvokeFailure_ReturnsErrorText_DoesNotThrow(Exception error)
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            ThrowOnSkillInvoke = error,
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);
        Assert.StartsWith("Skill s 呼叫失敗", result);
    }

    // ---- T10 / CSR-P1-020,021:catalog 抓取失敗 → best-effort 回空工具清單,聊天仍不炸 ----
    public static IEnumerable<object[]> CatalogErrors() => new[]
    {
        new object[] { new WorkflowInvocationException("工作流服務呼叫失敗：HTTP 502") },
        new object[] { new HttpRequestException("dns failure") },
        new object[] { new TaskCanceledException("timeout") },
        new object[] { new WorkflowInvocationException("工作流服務呼叫失敗：回應不是有效 JSON") },
    };

    [Theory]
    [MemberData(nameof(CatalogErrors))]
    public async Task CatalogFailure_ToolsEmpty_ChatDoesNotThrow(Exception error)
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService { ThrowOnCatalog = error };
        var svc = Build(agent, wf);

        // 聊天不炸(路由表為空 → 純聊天兜底)。
        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);
        Assert.Equal("測試回覆", reply.Reply);

        // 沒有靜態工具可退了:目錄失敗這輪就是空清單。
        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        Assert.Empty(tools!);
    }

    // ---- T11 / CSR-P1-019:路由回 NONE → 純聊天 ----
    [Fact]
    public async Task NoToolSelected_PlainChatReply_ZeroSkillInvokes()
    {
        var agent = new FakeLlmAgent { Response = "純聊天回覆" };  // 路由回覆 = 此字串 → 不匹配任何工具 → NONE
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("寒暄", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);   // 未命中工具 → 不執行任何 skill
    }

    // ---- CSR-P1-014:description 帶正確輸入提示 ----
    [Fact]
    public async Task ToolDescription_KeepsOriginal_AndAddsInputHint()
    {
        var wf = new FakeWorkflowService
        {
            Catalog = Cat("""[ { "name":"tenant_a_private_search", "description":"租戶 A 的專用檢索", "required_role":"USER", "source":"custom", "input_schema": { "question_text": { "type":"str", "required":true } } } ]"""),
        };
        var svc = Build(new FakeLlmAgent(), wf);

        var tools = await svc.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "tenant_a_private_search");

        Assert.Contains("租戶 A 的專用檢索", tool.Description);
        Assert.Contains("question_text", tool.Description);
        Assert.Contains("一段自然語言", tool.Description);
    }

    // ---- T12 / CSR-P1-028:mem0 順序與記憶內容(recall 前、remember 後、記融合後 reply) ----
    // 註:此輪路由回 "最終答案" → 不匹配任何工具 → 走純聊天兜底(BuildPromptAsync 注入 recall)。
    [Fact]
    public async Task Mem0_RecallBefore_RememberAfter_WithFusedReply()
    {
        var agent = new FakeLlmAgent { Response = "最終答案" };
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者是租戶 A\n" };
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = new ChatService(agent, new InMemoryChatMemoryStore(), mem0, new FakeConversationStore(),
            wf, new LlmOptions(), NullLogger<ChatService>.Instance);

        await svc.ChatAsync("問題", "u1", "c1", UserA);

        // recall 在(兜底)agent 呼叫「前」:recall 內容已組進 agent 看到的 system 前言。
        Assert.Contains(agent.LastMessages!, m => m.Role == "system" && m.Content.Contains("租戶 A"));
        // remember 在「後」:記的是使用者原訊息 + 融合後最終答案(非中間 skill JSON)。
        Assert.Equal(("u1", "問題", "最終答案"), Assert.Single(mem0.Remembered));
    }

    // ---- CSR-P1-030:無快取的 P1 邊界 — 每輪一次、兩輪合計兩次 ----
    [Fact]
    public async Task TwoRounds_CatalogFetchedExactlyTwice()
    {
        var agent = new FakeLlmAgent();
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        await svc.ChatAsync("第一問", "u1", "c1", UserA);
        await svc.ChatAsync("第二問", "u1", "c1", UserA);

        Assert.Equal(2, wf.CatalogContexts.Count);
    }

    // ============================================================================
    // 路由 → 執行 → 摘要 orchestration(新流程:LLM 只 ROUTE + SUMMARIZE,不心算)
    // ============================================================================

    // 護欄逐字(僅純聊天兜底路徑使用;與 ChatService.ChatGuardPrompt 同步)。
    private const string GuardPrompt =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    // 路由命中 skill → 確定性執行 → LLM 只潤飾;數字原封帶入摘要輸入,回覆是摘要輸出。
    [Fact]
    public async Task RoutedPath_SelectsSkill_ExecutesDeterministically_SummarizesResult()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");            // 第一次 CompleteAsync = 路由 → 選 kb_query
        agent.Responses.Enqueue("本季毛利率是 32.8%。"); // 第二次 CompleteAsync = 摘要(只潤飾)
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"毛利率 32.8%" } }"""),
        };
        var svc = new ChatService(agent, new InMemoryChatMemoryStore(), mem0, convos,
            wf, new LlmOptions(), NullLogger<ChatService>.Instance);

        var reply = await svc.ChatAsync("這季毛利率多少?", "u1", "c1", UserA);

        // 第一次呼叫是路由:system 為路由指令、user 為原訊息、不含護欄/歷史。
        Assert.StartsWith("你是一個路由器", agent.CompleteCalls[0][0].Content);
        Assert.DoesNotContain(agent.CompleteCalls[0], m => m.Content == GuardPrompt);

        // (a) 選中的 skill 以「使用者原訊息」為輸入被呼叫(input key 由 schema 挑出 = query)。
        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("kb_query", invoke.Name);
        Assert.Equal("這季毛利率多少?", invoke.Input["query"].GetString());

        // (b) 摘要呼叫:system 為禁改數字指令、user 帶入工具的確定性結果(數字原封)。
        Assert.Contains("一字都不得更改", agent.CompleteCalls[1][0].Content);
        var summaryUser = agent.CompleteCalls[1].Last();
        Assert.Equal("user", summaryUser.Role);
        Assert.Contains("毛利率 32.8%", summaryUser.Content);

        // (c) 對外回覆是摘要輸出,不是工具原始字串;remember 記最終摘要。
        Assert.Equal("本季毛利率是 32.8%。", reply.Reply);
        Assert.Equal(("u1", "這季毛利率多少?", "本季毛利率是 32.8%。"), Assert.Single(mem0.Remembered));
    }

    // 路由回 NONE → 純聊天兜底:不執行任何 skill,兜底使用護欄 prompt。
    [Fact]
    public async Task RoutedPath_NoneReply_FallsBackToGuardedPlainChat()
    {
        var agent = new FakeLlmAgent { Response = "純聊天回覆" };
        agent.Responses.Enqueue("NONE");   // 路由 = NONE
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("你好呀", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
        // 兜底走 BuildPromptAsync:最後一次呼叫第一則是護欄。
        Assert.Equal("system", agent.CompleteCalls[^1][0].Role);
        Assert.Equal(GuardPrompt, agent.CompleteCalls[^1][0].Content);
    }

    // 路由指令必須把「數字/YoY/比較」意圖導向工具(與 ChatGuardPrompt 同一組語義):
    // 少了這一步,revenue_qa 這類 YoY 問題會被路由判成 NONE → 純聊天兜底吐「查無此數據」。
    [Fact]
    public async Task RoutingInstruction_SteersNumericIntent_TowardTool_NotNone()
    {
        var agent = new FakeLlmAgent { Response = "NONE" };
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        await svc.ChatAsync("晴光科技 2025 相比 2024 的營收 YoY 年增率是多少?", "u1", "c1", UserA);

        // 第一次 CompleteAsync 是路由;其 system prompt 必須帶「年增率(YoY)」與「跨期間比較」的正向導引。
        var routingSystem = agent.CompleteCalls[0][0].Content;
        Assert.Contains("年增率", routingSystem);
        Assert.Contains("跨期間比較", routingSystem);
        Assert.Contains("不要因為題目像在算數學就輸出 NONE", routingSystem);
    }

    // 匿名:不路由、不讀目錄、不執行 skill,只有一次純聊天呼叫。
    [Fact]
    public async Task AnonymousPath_NoRouting_PlainChat_NoSkillInvoked()
    {
        var agent = new FakeLlmAgent { Response = "匿名回覆" };
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("嗨", "u1", "c1");  // userCtx = null

        Assert.Equal("匿名回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
        Assert.Empty(wf.CatalogContexts);       // 沒讀目錄 = 沒路由
        Assert.Single(agent.CompleteCalls);     // 只有純聊天一次
    }

    // 路由呼叫拋例外 → 退純聊天,整輪不炸。
    [Fact]
    public async Task RoutingCallThrows_FallsBackToPlainChat_ChatDoesNotThrow()
    {
        var agent = new FakeLlmAgent { Response = "兜底純聊天", ThrowOnFirstComplete = true };
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);

        Assert.Equal("兜底純聊天", reply.Reply);   // 第二次 CompleteAsync(純聊天)成功
        Assert.Empty(wf.SkillInvokes);
    }

    // 串流版路由路徑:路由 + 執行在串流「前」完成,串流吐出的是摘要 chunks;摘要輸入帶工具結果。
    [Fact]
    public async Task RoutedPath_Streaming_SummaryStreamedFromToolResult()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "本季", "毛利率", "32.8%" } };
        agent.Responses.Enqueue("kb_query");   // 路由(阻塞)
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"毛利率 32.8%" } }"""),
        };
        var svc = new ChatService(agent, new InMemoryChatMemoryStore(), new FakeMem0Client(), convos,
            wf, new LlmOptions(), NullLogger<ChatService>.Instance);

        var collected = new List<string>();
        await foreach (var c in svc.StreamChatAsync("這季毛利率?", "u1", "c1", UserA))
        {
            collected.Add(c);
        }

        // 路由 + 執行在串流前完成:skill 以原訊息被呼叫。
        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("這季毛利率?", invoke.Input["query"].GetString());
        // 串流吐出摘要 chunks;摘要 LLM 的 user 訊息帶入工具結果(StreamAsync 的最後一則)。
        Assert.Equal(new[] { "本季", "毛利率", "32.8%" }, collected);
        Assert.Contains("毛利率 32.8%", agent.LastMessages!.Last().Content);
        // 串流結束後持久化串接全文。
        Assert.Equal("本季毛利率32.8%", Assert.Single(convos.Saved).Reply);
    }

    // 寬鬆比對:路由回覆含工具名稱 token(非全等)仍能命中。
    [Fact]
    public async Task RoutedPath_LenientMatch_ReplyContainsToolName_StillRoutes()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("我建議使用 kb_query 這個工具");   // 非全等,含 token
        agent.Responses.Enqueue("摘要輸出");
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb_query", Assert.Single(wf.SkillInvokes).Name);
    }

    // ---- 路由重試(NONE/無命中一次後再試一次;最多兩次) ----

    // 第一次路由回 NONE、第二次選中 skill → 重試命中,回覆是摘要。
    [Fact]
    public async Task Routing_RetriesOnce_SecondAttemptHitsSkill_SummarizesResult()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");        // 第一次路由 = NONE
        agent.Responses.Enqueue("kb_query");    // 第二次路由(重試)= 選中
        agent.Responses.Enqueue("摘要輸出");    // 摘要
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb_query", Assert.Single(wf.SkillInvokes).Name);
        // 兩次路由 + 一次摘要 = 三次 CompleteAsync。
        Assert.Equal(3, agent.CompleteCalls.Count);
    }

    // 兩次路由皆 NONE → 退純聊天兜底,不執行任何 skill。
    [Fact]
    public async Task Routing_RetryExhausted_BothNone_FallsBackToPlainChat()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");        // 第一次路由
        agent.Responses.Enqueue("NONE");        // 第二次路由(重試)
        agent.Responses.Enqueue("純聊天回覆");  // 純聊天兜底
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("你好呀", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
        // 兩次路由 + 一次純聊天 = 三次;兜底走護欄 prompt。
        Assert.Equal(3, agent.CompleteCalls.Count);
        Assert.Equal(GuardPrompt, agent.CompleteCalls[^1][0].Content);
    }

    // 第一次路由就命中 → 不浪費重試(兩次呼叫:路由 + 摘要)。
    [Fact]
    public async Task Routing_FirstAttemptHits_NoWastedRetry()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb_query");    // 第一次路由即命中
        agent.Responses.Enqueue("摘要輸出");    // 摘要
        var wf = new FakeWorkflowService
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb_query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb_query", Assert.Single(wf.SkillInvokes).Name);
        // 路由一次 + 摘要一次 = 兩次;沒有第二次路由。
        Assert.Equal(2, agent.CompleteCalls.Count);
    }

    // SSE 與阻塞路徑共用同一路由表規則;各輪 catalog 恰取一次。
    [Fact]
    public async Task BlockingAndStream_SameRoutingCatalog_CatalogFetchedOncePerRound()
    {
        var wf = new FakeWorkflowService { Catalog = Cat(SampleCatalog) };

        var blockAgent = new FakeLlmAgent();
        await Build(blockAgent, wf).ChatAsync("問題", "u1", "c1", UserA);

        var streamAgent = new FakeLlmAgent();
        await foreach (var _ in Build(streamAgent, wf).StreamChatAsync("問題", "u1", "c1", UserA))
        {
        }

        // 兩路徑的路由 system 目錄(第一次 CompleteAsync)相同。
        Assert.Equal(blockAgent.CompleteCalls[0][0].Content, streamAgent.CompleteCalls[0][0].Content);
        Assert.Equal(2, wf.CatalogContexts.Count);   // 兩輪各取一次
    }
}
