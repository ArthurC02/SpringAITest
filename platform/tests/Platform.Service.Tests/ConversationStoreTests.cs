using System.Net;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class ConversationStoreTests
{
    private static readonly UserContext Ctx = new("user-a", "demo-a", "USER");

    private static ConversationStore Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    [Fact]
    public async Task Add_PostsPromptAndReply_SendsIdentityHeaders_MapsResult()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.Created, "{\"id\":5,\"createdAt\":\"2026-07-12T10:00:00Z\"}"));
        var store = Build(stub);

        var saved = await store.AddAsync("問句", "答句", Ctx);

        Assert.Equal(5, saved.Id);
        Assert.Equal("答句", saved.Reply);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);

        Assert.Equal("http://backend/api/conversations", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        // 對話以 (tenant_id, user_id) 隔離,身分 header 必須送達。
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("user-a", stub.Header("X-User-Id"));

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("問句", doc.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("答句", doc.RootElement.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task ListDesc_MapsItems_PreservesBackendOrder_SendsIdentityHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "[{\"id\":2,\"reply\":\"r2\",\"createdAt\":\"2026-07-12T10:01:00Z\"}," +
            "{\"id\":1,\"reply\":\"r1\",\"createdAt\":\"2026-07-12T10:00:00Z\"}]"));
        var store = Build(stub);

        var list = await store.ListDescAsync(Ctx);

        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[0].Id);
        Assert.Equal("r2", list[0].Reply);
        Assert.Equal("r1", list[1].Reply);
        Assert.Equal(DateTimeKind.Utc, list[0].CreatedAt.Kind);
        Assert.Equal("http://backend/api/conversations", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("user-a", stub.Header("X-User-Id"));
    }

    [Fact]
    public async Task Add_500_ThrowsBackendCall_NotWorkflowInvocation()
    {
        var store = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() => store.AddAsync("問", "答", Ctx));
        // 刻意不是 WorkflowInvocationException(那會變 502);維持 chat 端點失敗即 500。
        Assert.IsNotType<WorkflowInvocationException>(ex);
    }

    [Fact]
    public async Task Add_TransportError_ThrowsBackendCall_NotWorkflowInvocation()
    {
        // 傳輸層錯誤包成 BackendCallException(對外 500),與 WorkflowService 的 502 語意刻意不同。
        var store = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒")));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() => store.AddAsync("問", "答", Ctx));
        Assert.IsNotType<WorkflowInvocationException>(ex);
    }
}
