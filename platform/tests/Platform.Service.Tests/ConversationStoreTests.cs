using System.Net;
using System.Text.Json;
using Platform.Service.Abstractions;
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

    // backend 若回沒有時區標記的 createdAt(System.Text.Json 解成 Kind=Unspecified),ConversationStore 的
    // SpecifyKind 必須把它標成 UTC 且不位移時鐘 —— 帶 Z 的輸入本來就是 Utc,測不到這段。
    // 若哪天改成 ToUniversalTime,這筆會在非 UTC 機器上偏移而失敗。
    [Fact]
    public async Task Add_CreatedAtWithoutZoneSuffix_MarkedUtcWithoutShiftingClock()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.Created, "{\"id\":5,\"createdAt\":\"2026-07-12T10:00:00\"}"));

        var saved = await Build(stub).AddAsync("問句", "答句", Ctx);

        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Utc), saved.CreatedAt);
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

    // 空歷史是清單端點的邊界:真的 [](0 筆)與 body 反序列化成 null(SendForJsonListAsync 的 ?? new List<T>())
    // 兩種輸入都要回空清單,不得是 null、也不得拋例外(新使用者的聊天歷史就是這條路)。
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task ListDesc_EmptyOrNullBody_ReturnsEmptyList(string body)
    {
        var store = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, body)));

        var list = await store.ListDescAsync(Ctx);

        Assert.Empty(list);
    }

    [Fact]
    public async Task Add_WithRootMetadata_ForwardsServerDerivedLineage()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.Created, "{\"id\":5,\"createdAt\":\"2026-07-12T10:00:00Z\"}"));
        var metadata = new ChatTurnMetadata(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            2,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            3,
            Guid.Parse("33333333-3333-3333-3333-333333333333"));

        await Build(stub).AddAsync("prompt", "reply", Ctx, metadata);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        var root = doc.RootElement;
        Assert.Equal(metadata.OrchestratorId, root.GetProperty("orchestrator_id").GetGuid());
        Assert.Equal(2, root.GetProperty("orchestrator_revision").GetInt32());
        Assert.Equal(metadata.WorkflowId, root.GetProperty("workflow_id").GetGuid());
        Assert.Equal(3, root.GetProperty("workflow_revision").GetInt32());
        Assert.Equal(metadata.RootRunId, root.GetProperty("root_run_id").GetGuid());
    }

    // 沒有 D6 lineage 的一般聊天輪:五個 lineage 欄位一律以 null 送出(不得省略、更不得殘留上一輪的值)。
    // 與 Add_WithRootMetadata_* 成對:同一段 body 組裝在帶/不帶 metadata 兩種輸入下都要正確。
    [Fact]
    public async Task Add_WithoutMetadata_SendsNullLineageFields()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.Created, "{\"id\":5,\"createdAt\":\"2026-07-12T10:00:00Z\"}"));

        // 刻意經介面省略 metadata 呼叫(聊天走的就是這條:D6 未命中時 TurnMetadata 為 null)。
        IConversationStore store = Build(stub);
        await store.AddAsync("prompt", "reply", Ctx);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        var root = doc.RootElement;
        foreach (var field in new[]
                 {
                     "orchestrator_id", "orchestrator_revision", "workflow_id", "workflow_revision", "root_run_id",
                 })
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty(field).ValueKind);
        }
    }

    // 讀取端的錯誤映射與寫入端同語意:HTTP 錯誤碼與傳輸層例外都是 BackendCallException(對外 500),
    // 不得變成 WorkflowInvocationException(那會讓聊天歷史端點回 502)。
    [Fact]
    public async Task ListDesc_500AndTransportError_ThrowBackendCall_NotWorkflowInvocation()
    {
        var onError = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var httpError = await Assert.ThrowsAsync<BackendCallException>(() => onError.ListDescAsync(Ctx));
        Assert.IsNotType<WorkflowInvocationException>(httpError);

        var onTransport = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒")));
        var transportError = await Assert.ThrowsAsync<BackendCallException>(() => onTransport.ListDescAsync(Ctx));
        Assert.IsNotType<WorkflowInvocationException>(transportError);
    }

    [Fact]
    public async Task Add_500_ThrowsBackendCall_NotWorkflowInvocation()
    {
        var store = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() => store.AddAsync("問", "答", Ctx));
        // 刻意不是 WorkflowInvocationException(那會變 502);維持 chat 端點失敗即 500。
        Assert.IsNotType<WorkflowInvocationException>(ex);
    }

    // 第三種失敗等價類:狀態碼是 2xx、傳輸也成功,但 body 反序列化成 null(backend 回 JSON literal null)。
    // 寫入端不像清單端可以退成空集合 —— 拿不到 id 就得失敗,不得回一個半殘的 ChatResponse。
    [Fact]
    public async Task Add_SuccessWithNullJsonBody_ThrowsBackendCall_EmptyResponseMessage()
    {
        var store = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, "null")));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() => store.AddAsync("問", "答", Ctx));
        Assert.Contains("回應內容為空", ex.Message);
    }

    [Fact]
    public async Task Add_TransportError_ThrowsBackendCall_NotWorkflowInvocation()
    {
        // 傳輸層錯誤包成 BackendCallException(對外 500),與 WorkflowEngineClient 的 502 語意刻意不同。
        var store = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒")));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() => store.AddAsync("問", "答", Ctx));
        Assert.IsNotType<WorkflowInvocationException>(ex);
    }
}
