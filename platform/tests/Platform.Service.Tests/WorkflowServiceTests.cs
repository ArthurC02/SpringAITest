using System.Net;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service.Tests;

public sealed class WorkflowServiceTests
{
    private static readonly UserContext Ctx = new("alice", "demo-a", "USER");

    private static WorkflowService Build(StubHttpMessageHandler stub) =>
        new(new HttpClient(stub), new WorkflowOptions { BaseUrl = "http://downstream", InternalToken = "tok" });

    private static Dictionary<string, JsonElement> Input() =>
        new() { ["q"] = JsonSerializer.SerializeToElement("hi") };

    // ---- Skill 引擎端點(:8001):invoke / validate / catalog / nodes ----
    // 四者都要帶 X-Internal-Token + 三個身分 header(服務間信任邊界),回應原樣穿透。

    [Fact]
    public async Task InvokeSkill_PostsToSkillsPath_SendsFourHeaders_PassesJsonThrough()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.OK, "{\"skill\":\"quarterly-qa\",\"output\":{\"answer\":\"42\",\"trace\":[]}}"));

        var result = await Build(stub).InvokeSkillAsync("quarterly-qa", Input(), Ctx);

        Assert.Equal("http://downstream/skills/quarterly-qa/invoke", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("alice", stub.Header("X-User-Id"));
        Assert.Equal("USER", stub.Header("X-User-Role"));

        // 引擎輸出原樣穿透(含 trace 這類代理層不認識的鍵)。
        Assert.Equal("quarterly-qa", result.GetProperty("skill").GetString());
        Assert.Equal("42", result.GetProperty("output").GetProperty("answer").GetString());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("output").GetProperty("trace").ValueKind);

        // 請求 body 是 {"input": {...}}。
        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("hi", doc.RootElement.GetProperty("input").GetProperty("q").GetString());
    }

    // skill invoke 的下游狀態碼映射:見 MapInvokeErrorAsync。
    [Theory]
    [InlineData(404, typeof(WorkflowNotFoundException))]
    [InlineData(403, typeof(WorkflowForbiddenException))]
    [InlineData(422, typeof(WorkflowBadInputException))]
    [InlineData(500, typeof(WorkflowInvocationException))]
    [InlineData(504, typeof(WorkflowInvocationException))]
    public async Task InvokeSkill_DownstreamError_MapsSameAsWorkflowInvoke(int status, Type expected)
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)status)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => svc.InvokeSkillAsync("s", Input(), Ctx));

        Assert.IsType(expected, ex);
    }

    // 422 契約:解析 detail.message + detail.field_errors,填進 WorkflowBadInputException(對外 ApiError.fieldErrors)。
    [Fact]
    public async Task InvokeSkill_422WithFieldErrors_ParsesCleanMessageAndFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"detail":{"error":"workflow_input_invalid","message":"輸入資料有 2 個欄位需要修正","field_errors":{"query":"「query」為必填。","top_k":"「top_k」必須是整數。"}}}"""));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(() => Build(stub).InvokeSkillAsync("s", Input(), Ctx));

        // message = detail.message,乾淨且不含原始 body。
        Assert.Equal("輸入資料有 2 個欄位需要修正", ex.Message);
        Assert.DoesNotContain("detail", ex.Message);
        Assert.DoesNotContain("{", ex.Message);
        // field_errors(snake_case)逐鍵映入 FieldErrors,鍵沿用引擎欄位名。
        Assert.NotNull(ex.FieldErrors);
        Assert.Equal(2, ex.FieldErrors!.Count);
        Assert.Equal("「query」為必填。", ex.FieldErrors["query"]);
        Assert.Equal("「top_k」必須是整數。", ex.FieldErrors["top_k"]);
    }

    // 回落:舊版 workflow(裸字串 detail)/ 非 JSON / 缺 field_errors → 固定文案,無例外、不外洩原始 body。
    [Theory]
    [InlineData("{\"detail\":\"1 validation error for InputModel\\nquery field required\"}")] // 舊 pydantic 字串 detail
    [InlineData("plain text, not json")]                                                        // 非 JSON
    [InlineData("{\"detail\":{\"error\":\"x\"}}")]                                               // detail 物件但缺 message/field_errors
    public async Task InvokeSkill_422UnexpectedBody_FallsBackToFixedMessage_NoRawBodyLeak(string body)
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422, body));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(() => Build(stub).InvokeSkillAsync("s", Input(), Ctx));

        Assert.Equal("Skill 輸入不符合規範", ex.Message);
        Assert.Null(ex.FieldErrors);
    }

    // 缺 message 但有 field_errors:訊息回落固定文案,fieldErrors 仍帶上(不因缺 message 整組丟棄)。
    [Fact]
    public async Task InvokeSkill_422MissingMessageButHasFieldErrors_FallsBackMessage_KeepsFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"detail":{"field_errors":{"query":"「query」為必填。"}}}"""));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(() => Build(stub).InvokeSkillAsync("s", Input(), Ctx));

        Assert.Equal("Skill 輸入不符合規範", ex.Message);
        Assert.NotNull(ex.FieldErrors);
        Assert.Equal("「query」為必填。", ex.FieldErrors!["query"]);
    }

    [Fact]
    public async Task InvokeSkill_TransportFailure_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.InvokeSkillAsync("s", Input(), Ctx));
    }

    [Fact] // 引擎契約:validate 一律回 200,valid/errors 在 body — 代理層不得把 valid:false 轉成錯誤。
    public async Task ValidateSkill_PostsDefinition_ReturnsBodyVerbatim_EvenWhenInvalid()
    {
        // skill metadata 內的 additive kind 一併原樣穿透(AST-P1-013)。
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"valid":false,"errors":[{"code":"unbounded_loop","line":7}],"skill":{"name":"sales-helper","required_role":"USER","kind":"agentic"}}"""));

        var result = await Build(stub).ValidateSkillAsync("name: x\nflow: []\n", Ctx);

        Assert.Equal("http://downstream/skills/validate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Equal("unbounded_loop", result.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("agentic", result.GetProperty("skill").GetProperty("kind").GetString());

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("name: x\nflow: []\n", doc.RootElement.GetProperty("definition").GetString());
    }

    [Fact]
    public async Task ValidateSkill_DownstreamHttpError_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.ValidateSkillAsync("x", Ctx));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task GetSkillCatalog_GetsSkillsPath_PassesArrayThrough()
    {
        // 含 additive 的 kind:代理層不套 DTO,未知欄位原樣穿透,既有欄位不受影響(AST-P1-013)。
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "[{\"name\":\"kb-query\",\"source\":\"builtin\",\"kind\":\"flow\",\"revision\":null,\"bindable\":false},"
            + "{\"name\":\"quarterly-qa\",\"source\":\"custom\",\"kind\":\"agentic\",\"revision\":3,\"bindable\":true}]"));

        var result = await Build(stub).GetSkillCatalogAsync(Ctx);

        Assert.Equal("http://downstream/skills", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal("builtin", result[0].GetProperty("source").GetString());
        Assert.Equal("flow", result[0].GetProperty("kind").GetString());
        Assert.False(result[0].GetProperty("bindable").GetBoolean());
        Assert.Equal("custom", result[1].GetProperty("source").GetString());
        Assert.Equal("agentic", result[1].GetProperty("kind").GetString());
        Assert.True(result[1].GetProperty("bindable").GetBoolean());
    }

    [Fact]
    public async Task GetToolCatalog_GetsToolsPath_PassesSafeMetadataThrough()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """[{"name":"document_search","kind":"read","description":"搜尋租戶文件","risk":"low","returns":"SearchResult[]"}]"""));

        var result = await Build(stub).GetToolCatalogAsync(Ctx);

        Assert.Equal("http://downstream/tools", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("document_search", result[0].GetProperty("name").GetString());
        Assert.Equal("low", result[0].GetProperty("risk").GetString());
        Assert.False(result[0].TryGetProperty("endpoint", out _));
        Assert.False(result[0].TryGetProperty("token", out _));
    }

    [Fact]
    public async Task BusinessRuleCatalog_SplitsFactsAndActions_FromRegistryOwnedResponse()
    {
        const string catalog =
            """{"version":1,"gates":["pre-action"],"limits":{"maxDepth":8},"facts":[{"name":"action.amount","type":"decimal"}],"operators":[{"name":"gt"}],"actions":[{"name":"deny","precedence":100}]}""";

        var factsStub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, catalog));
        var facts = await Build(factsStub).GetBusinessRuleFactsAsync(Ctx);
        Assert.Equal("http://downstream/business-rules/catalog", factsStub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("action.amount", facts.GetProperty("facts")[0].GetProperty("name").GetString());
        Assert.Equal("gt", facts.GetProperty("operators")[0].GetProperty("name").GetString());
        Assert.Equal(8, facts.GetProperty("limits").GetProperty("maxDepth").GetInt32());
        Assert.False(facts.TryGetProperty("actions", out _));

        var actionsStub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, catalog));
        var actions = await Build(actionsStub).GetBusinessRuleActionsAsync(Ctx);
        Assert.Equal("deny", actions.GetProperty("actions")[0].GetProperty("name").GetString());
        Assert.Equal(100, actions.GetProperty("actions")[0].GetProperty("precedence").GetInt32());
        Assert.False(actions.TryGetProperty("facts", out _));
    }

    [Fact]
    public async Task ValidateBusinessRules_PostsExactContract_AndPassesInvalidResultThrough()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"valid":false,"errors":[{"path":"$.rules[0].when","code":"operator_type_mismatch","message":"型別不符"}]}"""));
        var ruleSet = JsonDocument.Parse("""{"version":1,"rules":[]}""").RootElement.Clone();

        var result = await Build(stub).ValidateBusinessRulesAsync(
            new BusinessRuleValidateRequest("pre-action", ruleSet), Ctx);

        Assert.Equal("http://downstream/business-rules/validate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Equal("operator_type_mismatch", result.GetProperty("errors")[0].GetProperty("code").GetString());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("pre-action", sent.RootElement.GetProperty("gate").GetString());
        Assert.Equal(1, sent.RootElement.GetProperty("ruleSet").GetProperty("version").GetInt32());
        Assert.Equal(2, sent.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task SimulateBusinessRules_PassesDecisionTraceThrough()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"valid":true,"canonicalRuleSet":{"version":1,"rules":[]},"errors":[],"simulation":{"decision":"deny","matchedRules":["r1"],"trace":[{"ruleId":"r1","outcome":"true"}]},"futureField":{"kept":true}}"""));
        var ruleSet = JsonDocument.Parse("""{"version":1,"rules":[]}""").RootElement.Clone();
        var facts = JsonDocument.Parse("""{"action.amount":9000}""").RootElement.Clone();

        var result = await Build(stub).SimulateBusinessRulesAsync(
            new BusinessRuleSimulateRequest("pre-action", ruleSet, facts), Ctx);

        Assert.Equal("http://downstream/business-rules/simulate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("deny", result.GetProperty("simulation").GetProperty("decision").GetString());
        Assert.Equal("r1", result.GetProperty("simulation").GetProperty("matchedRules")[0].GetString());
        Assert.True(result.GetProperty("futureField").GetProperty("kept").GetBoolean());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(9000, sent.RootElement.GetProperty("facts").GetProperty("action.amount").GetInt32());
        Assert.Equal(3, sent.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task BusinessRuleMalformedCatalogOrDownstreamFailure_MapsSafely()
    {
        var malformed = Build(new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.OK, """{"facts":"not-an-array"}""")));
        await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => malformed.GetBusinessRuleFactsAsync(Ctx));

        var downstream = Build(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var ruleSet = JsonDocument.Parse("""{"version":1,"rules":[]}""").RootElement.Clone();
        await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            downstream.SimulateBusinessRulesAsync(
                new BusinessRuleSimulateRequest("pre-action", ruleSet, null), Ctx));
    }

    [Fact]
    public async Task BusinessRuleRequest422_MapsToSanitizedBadInput()
    {
        var service = Build(new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.UnprocessableEntity,
            """{"detail":{"message":"gate 欄位不合法","field_errors":{"gate":"不支援的 gate"}}}""")));
        var ruleSet = JsonDocument.Parse("""{"version":1,"rules":[]}""").RootElement.Clone();

        var error = await Assert.ThrowsAsync<WorkflowBadInputException>(() =>
            service.ValidateBusinessRulesAsync(
                new BusinessRuleValidateRequest("bad-gate", ruleSet), Ctx));

        Assert.Equal("gate 欄位不合法", error.Message);
        Assert.Equal("不支援的 gate", error.FieldErrors!["gate"]);
        Assert.DoesNotContain("detail", error.Message);
    }

    [Fact]
    public async Task BusinessRuleRequest413_RemainsPayloadTooLarge()
    {
        var service = Build(new StubHttpMessageHandler(_ => TestHttp.Json(
            (HttpStatusCode)413,
            """{"detail":{"error":"request_too_large","message":"internal detail"}}""")));
        var ruleSet = JsonDocument.Parse("""{"version":1,"rules":[]}""").RootElement.Clone();

        var error = await Assert.ThrowsAsync<WorkflowPayloadTooLargeException>(() =>
            service.ValidateBusinessRulesAsync(
                new BusinessRuleValidateRequest("pre-action", ruleSet), Ctx));

        Assert.Equal(
            "Business Rule request exceeds the allowed size or nesting depth",
            error.Message);
        Assert.DoesNotContain("internal detail", error.Message);
    }

    [Fact]
    public async Task GetNodeCatalog_GetsNodesPath_PassesArrayThrough()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "[{\"name\":\"query_intake\",\"version\":\"1.0\",\"reads\":[],\"writes\":[\"original_query\"],\"requires_tools\":[]}]"));

        var result = await Build(stub).GetNodeCatalogAsync(Ctx);

        Assert.Equal("http://downstream/nodes", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("USER", stub.Header("X-User-Role"));
        Assert.Equal("query_intake", result[0].GetProperty("name").GetString());
        Assert.Equal("original_query", result[0].GetProperty("writes")[0].GetString());
    }

    // 目錄類 GET:任何失敗(含 4xx/5xx、傳輸失敗)都當成呼叫失敗 → 502。
    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task GetCatalogs_DownstreamError_ThrowsWorkflowInvocation(int status)
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)status)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetSkillCatalogAsync(Ctx));
        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetNodeCatalogAsync(Ctx));
        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetToolCatalogAsync(Ctx));
    }

    [Fact] // 下游回了 200 但 body 不是 JSON → 受控的 502,不是未捕捉的解析例外。
    public async Task GetSkillCatalog_GarbageBody_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, "not json")));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetSkillCatalogAsync(Ctx));
        Assert.Contains("不是有效 JSON", ex.Message);
    }
}
