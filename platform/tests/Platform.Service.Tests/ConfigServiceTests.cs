using System.Net;
using System.Text.Json;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class ConfigServiceTests
{
    private static readonly UserContext AdminCtx = new("admin-a", "demo-a", "ADMIN");
    private static readonly UserContext UserCtx = new("user-a", "demo-a", "USER");

    private static ConfigService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    [Fact]
    public async Task List_MapsResponse_ForwardsHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "[{\"key\":\"a\",\"value\":\"1\",\"updatedAt\":\"2026-07-12T00:00:00Z\"}]"));
        var svc = Build(stub);

        var list = await svc.ListAsync(UserCtx);

        var item = Assert.Single(list);
        Assert.Equal("a", item.Key);
        Assert.Equal("1", item.Value);
        Assert.Equal("http://backend/api/config", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("USER", stub.Header("X-User-Role"));
    }

    [Fact]
    public async Task Update_Succeeds_SendsValue_ForwardsAdminRole_MapsResult()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "{\"key\":\"a\",\"value\":\"2\",\"updatedAt\":\"2026-07-12T00:00:00Z\"}"));
        var svc = Build(stub);

        var updated = await svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx);

        Assert.Equal("a", updated.Key);
        Assert.Equal("2", updated.Value);

        Assert.Equal("http://backend/api/config/a", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("2", doc.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Update_403_ThrowsWorkflowForbidden_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足，無法修改系統組態")));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), UserCtx));
        Assert.Equal("權限不足，無法修改系統組態", ex.Message);
    }

    [Fact]
    public async Task Update_500_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx));
    }

    // B2:List 改走 BackendErrorMapper —— backend 4xx 不再被一律壓成 502,403 對外仍是 403。
    [Fact]
    public async Task List_Backend403_ThrowsWorkflowForbidden()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足")));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() => svc.ListAsync(UserCtx));
        Assert.Equal("權限不足", ex.Message);
    }
}
