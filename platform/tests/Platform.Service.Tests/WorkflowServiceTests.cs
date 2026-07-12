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
}
