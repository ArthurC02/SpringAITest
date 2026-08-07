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
/// 聊天 → Skill 路由(CSR-P1)。工具來源為動態 Skill 目錄(BuildToolsAsync)。
/// 註:新流程下 LLM 不再拿到原生 tools 引數;路由表(BuildToolsAsync 產出)改由測試直接驗證,
/// 端到端的「路由 → 執行 → 摘要」編排另見本檔末的 orchestration 區。
/// template-* 內建骨架是空殼、不可路由;可路由範例用非 template 名(builtin kb-query、custom tenant-a-private-search)。
///
/// P2(copilot-shared-core)記憶收斂後:路由/摘要(HIT 路徑)仍走「裸」<c>ILlmAgent</c>
/// (<see cref="FakeLlmAgent"/>,斷言面不變);未命中(MISS)/純聊天那一輪改跑共用的 hosted agent,
/// 斷言面從 <c>agent.CompleteCalls</c> 換成 <see cref="FakeChatClient"/> 收到的 messages/Instructions
/// ——這是接縫遷移,不是行為語意變更(見各案內註解)。
/// </summary>
public sealed class ChatSkillRoutingTests
{
    private static readonly UserContext UserA = new("user-a", "demo-a", "USER");
    private static readonly UserContext AdminA = new("admin-a", "demo-a", "ADMIN");

    private static ChatService Build(FakeLlmAgent agent, FakeWorkflowEngineClient workflows, FakeChatClient? chatClient = null)
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0, convos, identity, agent, workflows);
        return new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);
    }

    /// <summary>
    /// P4:路由(BuildToolsAsync/tool.InvokeAsync)搬進 SkillRoutingAgent,測試斷言面不變,只搬構造——
    /// 這批既有測試直接呼叫 BuildToolsAsync/InvokeAsync(不經 ChatAsync/StreamChatAsync),
    /// 因此需要 SkillRoutingAgent 實例而非只有 ChatService。
    /// </summary>
    private static (ChatService Service, SkillRoutingAgent Routing) BuildRouting(
        FakeLlmAgent agent, FakeWorkflowEngineClient workflows, FakeChatClient? chatClient = null)
    {
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, routing) = TestChatAgent.Build(chatClient, mem0, convos, identity, agent, workflows);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);
        return (svc, routing);
    }

    private static JsonElement Cat(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // 04 §2.2 的目錄樣本(template-retrieval → kb-query 以符合 template-* 過濾規則)。
    private const string SampleCatalog = """
    [
      { "name":"kb-query", "description":"從知識庫檢索答案", "required_role":"USER", "source":"builtin", "revision":1,
        "input_schema": { "query": { "type":"str", "required":true, "min_length":1 } } },
      { "name":"tenant-a-private-search", "description":"租戶 A 的專用檢索", "required_role":"USER", "source":"custom", "revision":3,
        "input_schema": { "question_text": { "type":"str", "required":true } } },
      { "name":"admin-report", "description":"管理報表", "required_role":"ADMIN", "source":"custom", "revision":2,
        "input_schema": { "prompt": { "type":"str", "required":true } } }
    ]
    """;

    // ---- T1 / CSR-P1-001:USER 只取得可用 Skill ----
    [Fact]
    public async Task User_GetsUserSkills_NotAdminSkill_AndCatalogCalledOnceWithIdentity()
    {
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        // 精確集合 + 順序(不是只驗「有/沒有」):多一支、少一支、順序被打亂都要紅。
        // builtin(kb-query)與 custom(tenant-a-private-search)在路由地位相同,ADMIN 的那支必須不在。
        Assert.Equal(new[] { "kb-query", "tenant-a-private-search" }, tools!.Select(t => t.Name).ToArray());

        // catalog 恰呼叫一次,並收到 tenant A / USER 身分。
        var ctx = Assert.Single(wf.CatalogContexts);
        Assert.Equal("demo-a", ctx.TenantCode);
        Assert.Equal("USER", ctx.Role);
    }

    // ---- T2 / CSR-P1-002:ADMIN 取得 USER 與 ADMIN Skill ----
    [Fact]
    public async Task Admin_GetsUserAndAdminSkills()
    {
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(AdminA, CancellationToken.None);

        Assert.Equal(
            new[] { "kb-query", "tenant-a-private-search", "admin-report" },
            tools!.Select(t => t.Name).ToArray());
    }

    // required_role 鍵整個缺席(不是空字串、也不是 "USER")→ 落在隱含預設 "USER" 那條 else 分支,
    // 一般 USER 身分照樣可路由;本檔其餘目錄樣本每一筆都明寫 required_role,碰不到這個預設。
    [Fact]
    public async Task MissingRequiredRole_DefaultsToUser_IsRoutable()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [ { "name":"no-role-skill", "description":"沒有 required_role 鍵", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } } ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.Equal(new[] { "no-role-skill" }, tools!.Select(t => t.Name).ToArray());
    }

    // 匿名不讀目錄/不執行 skill(阻塞 + 串流兩半)由 ChatBehaviorBaselineTests.A06a/A06b 覆蓋(超集);
    // 「匿名時 BuildToolsAsync 回 null(不是空清單)」由 ChatServiceTests.Chat_Anonymous_BuildsNoTools 覆蓋。
    // builtin 與 custom 路由地位相同已由上面兩案的精確集合斷言涵蓋(SampleCatalog 兩種 source 都有)。

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
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat($$"""
            [ { "name":"weird-skill", "description":"x", "required_role":"USER", "source":"custom", "input_schema": {{schema}} } ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.DoesNotContain(tools!, t => t.Name == "weird-skill");
    }

    // ---- CSR-P1-011:optional 欄位不破壞單參資格 ----
    [Fact]
    public async Task SingleRequiredString_WithOptionals_IsRoutable_AndInvokesOnlyRequiredKey()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [ { "name":"has-optionals", "description":"x", "required_role":"USER", "source":"custom",
                "input_schema": {
                  "query": { "type":"str", "required":true },
                  "top_k": { "type":"int", "required":false },
                  "lang": { "type":"str", "required":false }
                } } ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "has-optionals");
        await tool.InvokeAsync("原文問句", CancellationToken.None);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("has-optionals", invoke.Name);
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
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(catalogJson) };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.Empty(tools!);
    }

    // 上一案是「整包 catalog 不是陣列」;這一案是陣列合法、但個別 entry 壞掉(非物件元素、缺 name 鍵、
    // name 非字串)——逐項跳過而非整包放棄,同一陣列裡的正常 entry 仍要照常路由,且不拋例外。
    [Fact]
    public async Task MalformedCatalogEntries_AreSkipped_ValidSiblingStillRouted()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [
              { "description":"缺 name 鍵", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } },
              "just-a-string-item",
              123,
              { "name": 42, "description":"name 非字串", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"kb-query", "description":"唯一正常的一筆", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } }
            ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.Equal(new[] { "kb-query" }, tools!.Select(t => t.Name).ToArray());
    }

    // ---- 跨案關鍵修正:template-* 內建骨架不可被路由 ----
    [Fact]
    public async Task BuiltinTemplateSkeletons_AreNeverRouted_ButRealSkillsAre()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [
              { "name":"template-retrieval", "description":"檢索骨架", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"template-stats", "description":"統計骨架", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } },
              { "name":"kb-query", "description":"真的內建檢索", "required_role":"USER", "source":"builtin",
                "input_schema": { "query": { "type":"str", "required":true } } }
            ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.DoesNotContain("template-retrieval", names);
        Assert.DoesNotContain("template-stats", names);
        Assert.Contains("kb-query", names);
    }

    // 過濾條件是 source=="builtin" 且 template- 前綴的合取:custom 的 template- 前綴不被剝除。
    [Fact]
    public async Task CustomSkill_WithTemplatePrefix_IsNotFiltered()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [ { "name":"template-custom-thing", "description":"x", "required_role":"USER", "source":"custom",
                "input_schema": { "query": { "type":"str", "required":true } } } ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        Assert.Contains(tools!, t => t.Name == "template-custom-thing");
    }

    // ---- AST-P1-011 / R6 / D6:kind 不得拓寬路由 —— agentic 只是普通 catalog entry,路由規則一字不變 ----
    // 恰一個必填字串 input 的 agentic skill 沿用既有路由;多參 agentic 不被路由(但仍可 explicit invoke)。
    // 兩個 entry 都帶 kind:agentic,證明 SingleRequiredStringKey/角色/template 過濾完全無視 kind。
    [Fact]
    public async Task Agentic_SingleRequiredString_IsRoutable_MultiParam_IsNotRouted_KindIgnored()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""
            [
              { "name":"sales-helper", "description":"銷售助理", "required_role":"USER", "source":"custom", "kind":"agentic",
                "input_schema": { "question": { "type":"str", "required":true } } },
              { "name":"trip-planner", "description":"行程規劃", "required_role":"USER", "source":"custom", "kind":"agentic",
                "input_schema": { "origin": { "type":"str", "required":true }, "destination": { "type":"str", "required":true } } }
            ]
            """),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);

        var names = tools!.Select(t => t.Name).ToArray();
        Assert.Contains("sales-helper", names);        // 單必填字串 agentic → 可路由(與 flow 同一路徑)。
        Assert.DoesNotContain("trip-planner", names);  // 多參 agentic → 不路由(kind 未使其成為例外)。
    }

    // (刪除:原「多參 agentic 仍可 explicit invoke」一案直接呼叫 FakeWorkflowEngineClient.InvokeSkillAsync,
    //  完全沒有執行到任何 production 程式碼,只驗證 fake 會回傳自己被設定的值——套套邏輯。
    //  「多參 agentic 不被路由」這一半由上一案覆蓋。)

    // ---- T7 / CSR-P1-008,015:呼叫正確 Skill、輸入鍵與身分 ----
    [Fact]
    public async Task SelectedTool_InvokesCorrectSkill_WithInputKeyAndIdentity()
    {
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "tenant-a-private-search");

        await tool.InvokeAsync("比較 Q1 與 Q2", CancellationToken.None);

        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("tenant-a-private-search", invoke.Name);
        // input_schema 的必填鍵是 question_text(非模型看到的 question)。
        Assert.Equal("比較 Q1 與 Q2", invoke.Input["question_text"].GetString());
        Assert.Equal("demo-a", invoke.Ctx.TenantCode);
        Assert.Equal("USER", invoke.Ctx.Role);
        Assert.Equal(
            ArtifactUsageOrigin.WorkflowUnifiedInvoke,
            Assert.Single(wf.SkillInvokeOrigins));
    }

    // ---- T8 / CSR-P1-016:標準 output key 取值 ----
    [Theory]
    [InlineData("answer", "A")]
    [InlineData("final_answer", "B")]
    [InlineData("report", "C")]
    [InlineData("summary", "D")]
    public async Task StandardOutputKey_IsExtracted_NotWholeJson(string key, string value)
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat($$"""{ "skill":"s", "output": { "{{key}}": "{{value}}" } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal(value, result);
    }

    // 標準 output key 存在但值不是字串(數字/物件/陣列)→ 該鍵不算命中,繼續往下一個標準 key 找;
    // 上一案只餵字串值,踩不到 v.ValueKind == String 這道守衛(不可把 123 當答案、也不可炸)。
    [Fact]
    public async Task NonStringStandardOutputKey_IsSkipped_NextKeyWins()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "answer": 123, "final_answer":"文字答案" } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("文字答案", result);
    }

    // ---- business_result 優先:nl_logic 套規則後的權威答案須先於 final_answer(套規則前) ----
    [Fact]
    public async Task BusinessResult_TakesPriorityOver_FinalAnswer()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "final_answer":"套規則前", "business_result":"套規則後" } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("套規則後", result);
    }

    // 只有 business_result(infer/inspire/compare/stats 型)→ 取它,不落 GetRawText 倒整包 state JSON。
    [Fact]
    public async Task BusinessResultOnly_IsExtracted_NotRawStateJson()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "business_result":"答案" } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("答案", result);
    }

    // ---- CSR-P1-017:非標準輸出回傳 raw JSON(不遺失資訊、不拋例外) ----
    [Fact]
    public async Task NonStandardOutput_ReturnsRawJsonOfOutputObject()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "rows":[1,2], "count":2 } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        using var parsed = JsonDocument.Parse(result); // 合法 JSON
        Assert.Equal(2, parsed.RootElement.GetProperty("count").GetInt32());
        Assert.False(parsed.RootElement.TryGetProperty("skill", out _)); // 取的是 output 內層
    }

    // ---- agentic fatal run(遞迴/逾時/套件讀取錯誤):無 answer 鍵 + fatal_error/errors → 友善訊息,不倒內部 JSON ----
    [Theory]
    [InlineData("""{ "skill":"s", "output": { "trace":["n1","n2"], "fatal_error":"RecursionError", "errors":[] } }""")]
    [InlineData("""{ "skill":"s", "output": { "trace":["n1"], "errors":["timeout at node n1"] } }""")]
    public async Task FatalRunWithoutAnswerKey_ReturnsFriendlyMessage_NotInternalJson(string skillOutput)
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat(skillOutput),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.Equal("回覆過程發生錯誤，請稍後再試", result);
        // 內部欄位名絕不外洩給聊天模型改寫。
        Assert.DoesNotContain("trace", result);
        Assert.DoesNotContain("fatal_error", result);
        Assert.DoesNotContain("errors", result);
    }

    // 無 answer 鍵但也非 fatal(errors 空、無 fatal_error)→ 仍維持原 raw JSON fallback,不誤判成友善訊息。
    [Fact]
    public async Task NoAnswerKeyNonFatal_StillUsesRawJsonFallback()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "rows":[1,2], "errors":[] } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        using var parsed = JsonDocument.Parse(result);
        Assert.Equal(2, parsed.RootElement.GetProperty("rows").GetArrayLength());
    }

    // fatal_error 三態的中間值:上面兩案分別覆蓋「有值字串」(fatal)與「整個缺鍵」(非 fatal),
    // 這一案是「鍵在、值是 JSON null」——判定看的是 ValueKind 不是「鍵存不存在」,故仍非 fatal,
    // 維持 raw JSON fallback,不可誤判成友善訊息把 output 吞掉。
    [Fact]
    public async Task FatalErrorExplicitNull_IsNotFatalRun_StillUsesRawJsonFallback()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "skill":"s", "output": { "trace":["n1"], "fatal_error": null, "errors":[] } }"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        Assert.NotEqual("回覆過程發生錯誤，請稍後再試", result);
        using var parsed = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("fatal_error").ValueKind);
    }

    [Fact]
    public async Task OutputWithoutWrapper_FallsBackToRootRawJson()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            SkillOutput = Cat("""{ "foo":"bar" }"""),   // 沒有 output 外層、也無標準 key
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "s");

        var result = await tool.InvokeAsync("q", CancellationToken.None);

        using var parsed = JsonDocument.Parse(result);
        Assert.Equal("bar", parsed.RootElement.GetProperty("foo").GetString());
    }

    // ---- T9 / CSR-P1-023:單一 Skill invoke 失敗不炸整輪 ----
    // 六種例外都落在 InvokeSkillToolAsync 同一個 catch-all,留「下游領域例外」與「傳輸層例外」兩個代表。
    public static IEnumerable<object[]> SkillInvokeErrors() => new[]
    {
        new object[] { new WorkflowNotFoundException("找不到 Skill：s") },   // 下游領域例外
        new object[] { new HttpRequestException("connection reset") },       // 傳輸層例外
    };

    [Theory]
    [MemberData(nameof(SkillInvokeErrors))]
    public async Task SkillInvokeFailure_ReturnsErrorText_DoesNotThrow(Exception error)
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"s", "description":"x", "required_role":"USER", "source":"custom", "input_schema": { "q": { "type":"str", "required":true } } } ]"""),
            ThrowOnSkillInvoke = error,
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
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
        var wf = new FakeWorkflowEngineClient { ThrowOnCatalog = error };
        var (svc, routing) = BuildRouting(agent, wf);

        // 聊天不炸(路由表為空 → 純聊天兜底,由共用 hosted agent 回覆——FakeChatClient 預設回覆
        // 與 FakeLlmAgent 舊預設同為「測試回覆」,巧合但不需要額外設定)。
        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);
        Assert.Equal("測試回覆", reply.Reply);

        // 沒有靜態工具可退了:目錄失敗這輪就是空清單。
        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        Assert.Empty(tools!);
    }

    // 傳輸維度的另一半:同一個目錄抓取失敗走串流時也必須 best-effort 退純聊天——委派共用 hosted agent
    // 串流,不冒泡、不吞成靜默空話,串流正常結束後整段回覆照常持久化。
    // (串流 × skill invoke 失敗那一格由 ChatBehaviorBaselineTests.A04_StreamChatAsync_SingleSkillFailure_
    //  DoesNotThrow_StillStreamsSummary 覆蓋,不重複。)
    [Fact]
    public async Task CatalogFailure_Streaming_FallsBackToPlainChatStream_AndPersistsFullReply()
    {
        var agent = new FakeLlmAgent();
        var chatClient = new FakeChatClient { Chunks = new[] { "純聊天", "串流" } };
        var wf = new FakeWorkflowEngineClient { ThrowOnCatalog = new HttpRequestException("dns failure") };
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, convos: convos, identity: identity, llmAgent: agent, workflows: wf);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);

        var collected = new List<string>();
        await foreach (var c in svc.StreamChatAsync("這季毛利率?", "u1", "c1", UserA))
        {
            collected.Add(c);
        }

        Assert.Single(wf.CatalogContexts);      // 串流路徑確實嘗試過取目錄(不是跳過路由)
        Assert.Equal(new[] { "純聊天", "串流" }, collected);
        Assert.Empty(wf.SkillInvokes);
        Assert.Empty(agent.CompleteCalls);      // 工具清單為空 → 連路由那一刀都不打
        Assert.Equal("純聊天串流", Assert.Single(convos.Saved).Reply);
    }

    // T11 / CSR-P1-019(路由未命中 → 純聊天、零 skill invoke)由本檔
    // RoutedPath_NoneReply_FallsBackToGuardedPlainChat(另驗護欄 Instructions)與
    // Routing_RetryExhausted_BothNone(另驗路由呼叫次數)覆蓋。

    // ---- CSR-P1-014:description 帶正確輸入提示 ----
    [Fact]
    public async Task ToolDescription_KeepsOriginal_AndAddsInputHint()
    {
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat("""[ { "name":"tenant-a-private-search", "description":"租戶 A 的專用檢索", "required_role":"USER", "source":"custom", "input_schema": { "question_text": { "type":"str", "required":true } } } ]"""),
        };
        var (_, routing) = BuildRouting(new FakeLlmAgent(), wf);

        var tools = await routing.BuildToolsAsync(UserA, CancellationToken.None);
        var tool = tools!.Single(t => t.Name == "tenant-a-private-search");

        Assert.Contains("租戶 A 的專用檢索", tool.Description);
        Assert.Contains("question_text", tool.Description);
        Assert.Contains("一段自然語言", tool.Description);
    }

    // ---- T12 / CSR-P1-028:mem0 順序與記憶內容(recall 前、remember 後、記融合後 reply) ----
    // 註:此輪路由回 "最終答案" → 不匹配任何工具 → 走純聊天兜底,recall 前言改注入 Instructions(P2)。
    [Fact]
    public async Task Mem0_RecallBefore_RememberAfter_WithFusedReply()
    {
        var agent = new FakeLlmAgent { Response = "最終答案" };
        var chatClient = new FakeChatClient { Response = "最終答案" };
        var mem0 = new FakeMem0Client { RecallResult = "- 使用者是租戶 A\n" };
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var convos = new FakeConversationStore();
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, _) = TestChatAgent.Build(chatClient, mem0, convos, identity, agent, wf);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);

        await svc.ChatAsync("問題", "u1", "c1", UserA);

        // recall 在(兜底)agent 呼叫「前」:recall 內容已組進共用 hosted agent 看到的 Instructions。
        Assert.Contains("租戶 A", chatClient.LastOptions!.Instructions);
        // remember 在「後」:記的是使用者原訊息 + 融合後最終答案(非中間 skill JSON);已登入 → uid 為 JWT 身分。
        Assert.Equal(("demo-a:user-a", "問題", "最終答案"), Assert.Single(mem0.Remembered));
    }

    // CSR-P1-030(無快取:每輪取一次、兩輪合計兩次)由本檔
    // BlockingAndStream_SameRoutingCatalog_CatalogFetchedOncePerRound 的 Equal(2, CatalogContexts.Count) 覆蓋。

    // ============================================================================
    // 路由 → 執行 → 摘要 orchestration(新流程:LLM 只 ROUTE + SUMMARIZE,不心算)
    // ============================================================================

    // 護欄逐字(僅純聊天兜底路徑使用;與 ChatService.ChatGuardPrompt 同步)。P2:護欄改走
    // ChatOptions.Instructions,不再是訊息列裡的 system 訊息。
    private const string GuardPrompt =
        "回答前先判斷問題類型，不要急著搶答。若問題涉及任何數字、金額、比率、年增率（YoY）、統計、排名或跨期間比較，你「必須」先呼叫對應的 skill 工具，並只依工具回傳的結果作答。嚴禁在未呼叫工具的情況下自行給出數字；嚴禁自己做任何算術（加減乘除、百分比、成長率）——這類計算一律交給工具，因為你自行心算常常算錯。若沒有合適的工具、文件未提供該數據、或你無法確定，請直接說「查無此數據」，不要編造或估算。只有純聊天或不涉及數字的問題，才可直接回答。";

    // P4 guardrail:這是唯一刻意逐字釘住的 prompt，避免摘要階段放寬任何「工具數字不可改」限制。
    private const string SummaryInstruction =
        "把以下『工具結果』改寫成給使用者的自然、完整中文回覆。數字、金額、比率、百分比一字都不得更改、刪除或新增，只做語言潤飾與說明。若工具結果表示查無資料或發生錯誤，如實轉達，不要編造。";

    // 路由命中 skill → 確定性執行 → LLM 只潤飾;數字原封帶入摘要輸入,回覆是摘要輸出(HIT 路徑,不受 P2 影響)。
    [Fact]
    public async Task RoutedPath_SelectsSkill_ExecutesDeterministically_SummarizesResult()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb-query");            // 第一次 CompleteAsync = 路由 → 選 kb-query
        agent.Responses.Enqueue("本季毛利率是 32.8%。"); // 第二次 CompleteAsync = 摘要(只潤飾)
        var mem0 = new FakeMem0Client();
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"毛利率 32.8%" } }"""),
        };
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, _) = TestChatAgent.Build(mem0: mem0, convos: convos, identity: identity, llmAgent: agent, workflows: wf);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);

        var reply = await svc.ChatAsync("這季毛利率多少?", "u1", "c1", UserA);

        // 第一次呼叫是路由:system 為路由指令、user 為原訊息、不含護欄/歷史。
        Assert.StartsWith("你是一個路由器", agent.CompleteCalls[0][0].Content);
        Assert.DoesNotContain(agent.CompleteCalls[0], m => m.Content == GuardPrompt);

        // (a) 選中的 skill 以「使用者原訊息」為輸入被呼叫(input key 由 schema 挑出 = query)。
        var invoke = Assert.Single(wf.SkillInvokes);
        Assert.Equal("kb-query", invoke.Name);
        Assert.Equal("這季毛利率多少?", invoke.Input["query"].GetString());

        // (b) 摘要呼叫:system 為禁改數字指令、user 帶入工具的確定性結果(數字原封)。
        Assert.Equal(SummaryInstruction, agent.CompleteCalls[1][0].Content);
        var summaryUser = agent.CompleteCalls[1].Last();
        Assert.Equal("user", summaryUser.Role);
        Assert.Contains("毛利率 32.8%", summaryUser.Content);

        // (c) 對外回覆是摘要輸出,不是工具原始字串;remember 記最終摘要;已登入 → uid 為 JWT 身分。
        Assert.Equal("本季毛利率是 32.8%。", reply.Reply);
        Assert.Equal(("demo-a:user-a", "這季毛利率多少?", "本季毛利率是 32.8%。"), Assert.Single(mem0.Remembered));
    }

    // 路由回 NONE → 純聊天兜底:不執行任何 skill,兜底使用護欄 Instructions(P2:改走 Instructions,非訊息列)。
    [Fact]
    public async Task RoutedPath_NoneReply_FallsBackToGuardedPlainChat()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");   // 路由 = NONE
        var chatClient = new FakeChatClient { Response = "純聊天回覆" };
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf, chatClient);

        var reply = await svc.ChatAsync("你好呀", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
        // 兜底走共用 hosted agent:護欄以 Instructions 傳遞,不是訊息列裡的 system 訊息。
        Assert.Equal(GuardPrompt, chatClient.LastOptions!.Instructions);
    }

    // 路由指令必須把「數字/YoY/比較」意圖導向工具(與 ChatGuardPrompt 同一組語義):
    // 少了這一步,revenue-qa 這類 YoY 問題會被路由判成 NONE → 純聊天兜底吐「查無此數據」。
    [Fact]
    public async Task RoutingInstruction_SteersNumericIntent_TowardTool_NotNone()
    {
        var agent = new FakeLlmAgent { Response = "NONE" };
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf);

        await svc.ChatAsync("晴光科技 2025 相比 2024 的營收 YoY 年增率是多少?", "u1", "c1", UserA);

        // 第一次 CompleteAsync 是路由;其 system prompt 必須帶「年增率(YoY)」與「跨期間比較」的正向導引。
        var routingSystem = agent.CompleteCalls[0][0].Content;
        Assert.Contains("年增率", routingSystem);
        Assert.Contains("跨期間比較", routingSystem);
        Assert.Contains("不要因為題目像在算數學就輸出 NONE", routingSystem);
    }

    // 匿名:不路由、不讀目錄、不執行 skill,ILlmAgent(路由)完全不被呼叫,純聊天改由共用 hosted agent 回覆。
    [Fact]
    public async Task AnonymousPath_NoRouting_PlainChat_NoSkillInvoked()
    {
        var agent = new FakeLlmAgent();
        var chatClient = new FakeChatClient { Response = "匿名回覆" };
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf, chatClient);

        var reply = await svc.ChatAsync("嗨", "u1", "c1");  // userCtx = null

        Assert.Equal("匿名回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
        Assert.Empty(wf.CatalogContexts);       // 沒讀目錄 = 沒路由
        // P2:匿名不路由 → TryRouteAndExecuteAsync 立即回 null,ILlmAgent(路由專用)完全不被呼叫
        // (純聊天改由共用 hosted agent 處理,不再共用同一顆 ILlmAgent)。
        Assert.Empty(agent.CompleteCalls);
    }

    // 路由呼叫拋例外 → 退純聊天,整輪不炸;純聊天兜底改由共用 hosted agent 回覆。
    [Fact]
    public async Task RoutingCallThrows_FallsBackToPlainChat_ChatDoesNotThrow()
    {
        var agent = new FakeLlmAgent { ThrowOnFirstComplete = true };
        var chatClient = new FakeChatClient { Response = "兜底純聊天" };
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf, chatClient);

        var reply = await svc.ChatAsync("問題", "u1", "c1", UserA);

        Assert.Equal("兜底純聊天", reply.Reply);   // 路由拋例外被吞成 null,退純聊天(共用 hosted agent)成功
        Assert.Empty(wf.SkillInvokes);
    }

    // 串流版路由路徑:路由 + 執行在串流「前」完成,串流吐出的是摘要 chunks;摘要輸入帶工具結果(HIT 路徑,不受 P2 影響)。
    [Fact]
    public async Task RoutedPath_Streaming_SummaryStreamedFromToolResult()
    {
        var agent = new FakeLlmAgent { Chunks = new[] { "本季", "毛利率", "32.8%" } };
        agent.Responses.Enqueue("kb-query");   // 路由(阻塞)
        var convos = new FakeConversationStore();
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"毛利率 32.8%" } }"""),
        };
        var identity = new FakeChatIdentityAccessor();
        var (hostAgent, _, _) = TestChatAgent.Build(convos: convos, identity: identity, llmAgent: agent, workflows: wf);
        var svc = new ChatService(hostAgent, convos, identity, new LlmOptions(), NullLogger<ChatService>.Instance);

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

    // 寬鬆比對(路由回覆只含工具名 token)由 ChatBehaviorBaselineTests.A10 覆蓋,且嚴格更強:
    // 它的目錄同時有 kb 與 kb-query,能抓到「取最短前綴」的迴歸;此處 SampleCatalog 只有一個 kb-query,分辨不出。

    // ---- 路由重試(NONE/無命中一次後再試一次;最多兩次) ----

    // 第一次路由回 NONE、第二次選中 skill → 重試命中,回覆是摘要(HIT 路徑,不受 P2 影響)。
    [Fact]
    public async Task Routing_RetriesOnce_SecondAttemptHitsSkill_SummarizesResult()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");        // 第一次路由 = NONE
        agent.Responses.Enqueue("kb-query");    // 第二次路由(重試)= 選中
        agent.Responses.Enqueue("摘要輸出");    // 摘要
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb-query", Assert.Single(wf.SkillInvokes).Name);
        // 兩次路由 + 一次摘要 = 三次 CompleteAsync。
        Assert.Equal(3, agent.CompleteCalls.Count);
    }

    // 兩次路由皆 NONE → 退純聊天兜底,不執行任何 skill;純聊天兜底改由共用 hosted agent(不再計入 agent.CompleteCalls)。
    [Fact]
    public async Task Routing_RetryExhausted_BothNone_FallsBackToPlainChat()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("NONE");        // 第一次路由
        agent.Responses.Enqueue("NONE");        // 第二次路由(重試)
        var chatClient = new FakeChatClient { Response = "純聊天回覆" };
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };
        var svc = Build(agent, wf, chatClient);

        var reply = await svc.ChatAsync("你好呀", "u1", "c1", UserA);

        Assert.Equal("純聊天回覆", reply.Reply);
        Assert.Empty(wf.SkillInvokes);
        // 兩次路由 = 兩次 CompleteAsync(純聊天兜底改由共用 hosted agent,不再是第三次 CompleteAsync)。
        Assert.Equal(2, agent.CompleteCalls.Count);
        Assert.Equal(GuardPrompt, chatClient.LastOptions!.Instructions);
    }

    // 第一次路由就命中 → 不浪費重試(兩次呼叫:路由 + 摘要,HIT 路徑不受 P2 影響)。
    [Fact]
    public async Task Routing_FirstAttemptHits_NoWastedRetry()
    {
        var agent = new FakeLlmAgent();
        agent.Responses.Enqueue("kb-query");    // 第一次路由即命中
        agent.Responses.Enqueue("摘要輸出");    // 摘要
        var wf = new FakeWorkflowEngineClient
        {
            Catalog = Cat(SampleCatalog),
            SkillOutput = Cat("""{ "skill":"kb-query", "output": { "business_result":"命中" } }"""),
        };
        var svc = Build(agent, wf);

        var reply = await svc.ChatAsync("這季毛利率?", "u1", "c1", UserA);

        Assert.Equal("摘要輸出", reply.Reply);
        Assert.Equal("kb-query", Assert.Single(wf.SkillInvokes).Name);
        // 路由一次 + 摘要一次 = 兩次;沒有第二次路由。
        Assert.Equal(2, agent.CompleteCalls.Count);
    }

    // SSE 與阻塞路徑共用同一路由表規則;各輪 catalog 恰取一次。
    [Fact]
    public async Task BlockingAndStream_SameRoutingCatalog_CatalogFetchedOncePerRound()
    {
        var wf = new FakeWorkflowEngineClient { Catalog = Cat(SampleCatalog) };

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
