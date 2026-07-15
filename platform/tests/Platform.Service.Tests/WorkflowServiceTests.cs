using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Service;
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Invoke_Succeeds_SendsFourHeaders_AndMapsResponse()
    {
        var stub = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, "{\"workflow\":\"rag_qa\",\"output\":{\"answer\":\"42\"}}"));
        var svc = Build(stub);

        var response = await svc.InvokeAsync("rag_qa", Input(), Ctx);

        Assert.Equal("rag_qa", response.Workflow);
        Assert.Equal("42", response.Output["answer"].GetString());

        Assert.Equal("http://downstream/workflows/rag_qa/invoke", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("alice", stub.Header("X-User-Id"));
        Assert.Equal("USER", stub.Header("X-User-Role"));
    }

    [Fact]
    public async Task Invoke_404_ThrowsWorkflowNotFound()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(() => svc.InvokeAsync("ghost", Input(), Ctx));
        Assert.Equal("找不到工作流：ghost", ex.Message);
    }

    [Fact]
    public async Task Invoke_403_ThrowsWorkflowForbidden()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() => svc.InvokeAsync("secret", Input(), Ctx));
        Assert.Equal("權限不足，無法執行工作流：secret", ex.Message);
    }

    [Fact]
    public async Task Invoke_422_ThrowsWorkflowBadInput_WithDownstreamBody()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("欄位錯誤", Encoding.UTF8) });
        var svc = Build(stub);

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(() => svc.InvokeAsync("rag_qa", Input(), Ctx));
        Assert.Equal("工作流輸入不符合規範：欄位錯誤", ex.Message);
    }

    [Fact]
    public async Task Invoke_500_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.InvokeAsync("rag_qa", Input(), Ctx));
    }

    [Fact]
    public async Task Invoke_TransportError_ThrowsWorkflowInvocation()
    {
        // 傳輸層錯誤(連線失敗/逾時)也包成 WorkflowInvocationException(對外 502)。
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.InvokeAsync("rag_qa", Input(), Ctx));
    }

    [Fact]
    public async Task List_MapsResponse_AndSendsHeaders()
    {
        var stub = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, "[{\"name\":\"rag_qa\",\"description\":\"檢索問答\",\"required_role\":\"USER\"}]"));
        var svc = Build(stub);

        var list = await svc.ListAsync(Ctx);

        var item = Assert.Single(list);
        Assert.Equal("rag_qa", item.Name);
        Assert.Equal("檢索問答", item.Description);
        Assert.Equal("USER", item.RequiredRole);
        Assert.Equal("http://downstream/workflows", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("USER", stub.Header("X-User-Role"));
    }

    // ---- Skill 引擎端點(:8001):invoke / validate / catalog / nodes ----
    // 四者都要帶 X-Internal-Token + 三個身分 header(服務間信任邊界),回應原樣穿透。

    [Fact]
    public async Task InvokeSkill_PostsToSkillsPath_SendsFourHeaders_PassesJsonThrough()
    {
        var stub = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, "{\"skill\":\"quarterly_qa\",\"output\":{\"answer\":\"42\",\"trace\":[]}}"));

        var result = await Build(stub).InvokeSkillAsync("quarterly_qa", Input(), Ctx);

        Assert.Equal("http://downstream/skills/quarterly_qa/invoke", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("alice", stub.Header("X-User-Id"));
        Assert.Equal("USER", stub.Header("X-User-Role"));

        // 引擎輸出原樣穿透(含 trace 這類代理層不認識的鍵)。
        Assert.Equal("quarterly_qa", result.GetProperty("skill").GetString());
        Assert.Equal("42", result.GetProperty("output").GetProperty("answer").GetString());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("output").GetProperty("trace").ValueKind);

        // 請求 body 是 {"input": {...}}(與 /workflows invoke 同形)。
        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("hi", doc.RootElement.GetProperty("input").GetProperty("q").GetString());
    }

    // skill invoke 的錯誤碼映射與 /workflows/{name}/invoke 逐一相同。
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

    [Fact]
    public async Task InvokeSkill_TransportFailure_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.InvokeSkillAsync("s", Input(), Ctx));
    }

    [Fact] // 引擎契約:validate 一律回 200,valid/errors 在 body — 代理層不得把 valid:false 轉成錯誤。
    public async Task ValidateSkill_PostsDefinition_ReturnsBodyVerbatim_EvenWhenInvalid()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            "{\"valid\":false,\"errors\":[{\"code\":\"unbounded_loop\",\"line\":7}]}"));

        var result = await Build(stub).ValidateSkillAsync("name: x\nflow: []\n", Ctx);

        Assert.Equal("http://downstream/skills/validate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Equal("unbounded_loop", result.GetProperty("errors")[0].GetProperty("code").GetString());

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
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            "[{\"name\":\"kb_query\",\"source\":\"builtin\",\"revision\":null},"
            + "{\"name\":\"quarterly_qa\",\"source\":\"custom\",\"revision\":3}]"));

        var result = await Build(stub).GetSkillCatalogAsync(Ctx);

        Assert.Equal("http://downstream/skills", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal(2, result.GetArrayLength());
        Assert.Equal("builtin", result[0].GetProperty("source").GetString());
        Assert.Equal("custom", result[1].GetProperty("source").GetString());
    }

    [Fact]
    public async Task GetNodeCatalog_GetsNodesPath_PassesArrayThrough()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            "[{\"name\":\"query_intake\",\"version\":\"1.0\",\"reads\":[],\"writes\":[\"original_query\"],\"requires_tools\":[]}]"));

        var result = await Build(stub).GetNodeCatalogAsync(Ctx);

        Assert.Equal("http://downstream/nodes", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("USER", stub.Header("X-User-Role"));
        Assert.Equal("query_intake", result[0].GetProperty("name").GetString());
        Assert.Equal("original_query", result[0].GetProperty("writes")[0].GetString());
    }

    // 目錄類 GET:任何失敗(含 4xx/5xx、傳輸失敗)都當成呼叫失敗 → 502(同 ListAsync 既有語義)。
    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task GetCatalogs_DownstreamError_ThrowsWorkflowInvocation(int status)
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)status)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetSkillCatalogAsync(Ctx));
        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetNodeCatalogAsync(Ctx));
    }

    [Fact] // 下游回了 200 但 body 不是 JSON → 受控的 502,不是未捕捉的解析例外。
    public async Task GetSkillCatalog_GarbageBody_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "not json")));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.GetSkillCatalogAsync(Ctx));
        Assert.Contains("不是有效 JSON", ex.Message);
    }
}
