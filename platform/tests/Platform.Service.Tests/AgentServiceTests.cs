using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

/// <summary>
/// AgentService(透明代理 backend /api/agents,D1)。這一層驗:
/// (a) 每個請求都帶 X-Internal-Token + 三個身分 header(取自已驗證 JWT),capabilities 有才帶 X-User-Capabilities;
/// (b) PUT draft / publish / validate 的 If-Match 原樣往下轉發;
/// (c) backend 的狀態碼(含 412)、body 與 ETag 原樣穿透;5xx 與傳輸失敗才收斂成對外 502。
/// </summary>
public sealed class AgentServiceTests
{
    private const string AgentIdText = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid AgentId = Guid.Parse(AgentIdText);
    private static readonly UserContext AdminCtx = new("admin-a", "demo-a", "ADMIN");

    private static readonly UserContext CapCtx =
        new("admin-a", "demo-a", "ADMIN", new[] { "workflow.manage", "agent.author" });

    private static readonly UserContext GroupCtx =
        new(
            "admin-a",
            "demo-a",
            "ADMIN",
            Groups: new[] { "operations", "reviewers" });

    private static AgentService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    private static HttpResponseMessage Resp(HttpStatusCode status, string body, string? etag = null)
    {
        var resp = TestHttp.Json(status, body);
        if (etag is not null)
        {
            resp.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return resp;
    }

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ---- 身分 header 轉發 ----

    [Fact]
    public async Task List_ForwardsIdentityHeaders_NoCapabilityHeaderWhenAbsent()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            HttpStatusCode.OK,
            """[{"id":"11111111-1111-1111-1111-111111111111","slug":"researcher"}]"""));

        var result = await Build(stub).ListAsync(AdminCtx);

        Assert.Equal("http://backend/api/agents", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
        // 無 capabilities claim → 不帶 header(fail-closed:缺席即無授權)。
        Assert.False(stub.HasHeader("X-User-Capabilities"));
        Assert.False(stub.HasHeader("X-User-Groups"));

        Assert.Equal(200, result.Status);
        Assert.Equal("researcher", Json(result.Body).EnumerateArray().Single().GetProperty("slug").GetString());
    }

    [Fact]
    public async Task List_ForwardsCapabilitiesHeader_SpaceJoined_WhenPresent()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, "[]"));

        await Build(stub).ListAsync(CapCtx);

        Assert.Equal("workflow.manage agent.author", stub.Header("X-User-Capabilities"));
    }

    [Fact]
    public async Task List_RegeneratesGroupsHeaderFromUserContext()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, "[]"));

        await Build(stub).ListAsync(GroupCtx);

        Assert.Equal("operations reviewers", stub.Header("X-User-Groups"));
    }

    [Fact]
    public async Task List_RejectsOverAggregateGroupContextBeforeTransport()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("must not send"));
        var context = AdminCtx with
        {
            Groups = GroupSet(exceedByOneByte: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(stub).ListAsync(context));
        Assert.Null(stub.LastRequest);
    }

    // ---- 路徑/方法/body 正確性 ----

    [Fact]
    public async Task Create_PostsBodyToCollection()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.Created, """{"id":"a2"}"""));

        var result = await Build(stub).CreateAsync(AdminCtx, Json("""{"slug":"new-agent","name":"新代理"}"""));

        Assert.Equal("http://backend/api/agents", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal(201, result.Status);
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("new-agent", sent.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Get_ForwardsIdInPath_ReturnsEtagFromResponse()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            HttpStatusCode.OK,
            """{"id":"11111111-1111-1111-1111-111111111111","draft_version":3}""",
            "\"3\""));

        var result = await Build(stub).GetAsync(AgentId, AdminCtx);

        Assert.Equal($"http://backend/api/agents/{AgentIdText}", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(200, result.Status);
        Assert.Equal("\"3\"", result.ETag);
        Assert.Equal(3, Json(result.Body).GetProperty("draft_version").GetInt32());
    }

    private static string[] GroupSet(bool exceedByOneByte)
        => Enumerable.Range(0, 16)
            .Select(index =>
            {
                var length = index == 0 || exceedByOneByte && index == 1
                    ? 128
                    : 127;
                var prefix = $"g{index:D2}";
                return prefix + new string('a', length - prefix.Length);
            })
            .ToArray();

    [Fact]
    public async Task Deactivate_SendsDeleteToItemPath()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await Build(stub).DeactivateAsync(AgentId, AdminCtx);

        Assert.Equal($"http://backend/api/agents/{AgentIdText}", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, stub.LastRequest!.Method);
        Assert.Equal(204, result.Status);
    }

    [Fact]
    public async Task Revisions_SendsGetToRevisionsPath()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, """[{"revision":1}]"""));

        await Build(stub).RevisionsAsync(AgentId, AdminCtx);

        Assert.Equal($"http://backend/api/agents/{AgentIdText}/revisions", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
    }

    [Fact]
    public async Task RestoreRevision_SendsPostToRestorePath()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, """{"revision":2}"""));

        await Build(stub).RestoreRevisionAsync(AgentId, 1, AdminCtx);

        Assert.Equal(
            $"http://backend/api/agents/{AgentIdText}/revisions/1/restore",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
    }

    [Fact]
    public async Task Enable_SendsPostToEnablePath()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            HttpStatusCode.OK,
            """{"id":"11111111-1111-1111-1111-111111111111","enabled":true}"""));

        var result = await Build(stub).EnableAsync(AgentId, AdminCtx);

        Assert.Equal($"http://backend/api/agents/{AgentIdText}/enable", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal(200, result.Status);
    }

    // ---- If-Match 轉發(樂觀鎖)----

    [Fact]
    public async Task UpdateDraft_ForwardsIfMatchAndBody_ToPut()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, """{"draft_version":2}""", "\"2\""));

        await Build(stub).UpdateDraftAsync(AgentId, AdminCtx, "\"1\"", Json("""{"name":"改名"}"""));

        Assert.Equal($"http://backend/api/agents/{AgentIdText}/draft", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);
        Assert.Equal("\"1\"", stub.LastRequest!.Headers.IfMatch.Single().ToString());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("改名", sent.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Publish_ForwardsIfMatch()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            HttpStatusCode.OK,
            """{"published_revision":1}"""));

        await Build(stub).PublishAsync(
            AgentId,
            AdminCtx,
            "\"1\"",
            Json("""{"expected_draft_version":1}"""));

        Assert.Equal($"http://backend/api/agents/{AgentIdText}/publish", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("\"1\"", stub.LastRequest!.Headers.IfMatch.Single().ToString());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(1, sent.RootElement.GetProperty("expected_draft_version").GetInt64());
    }

    [Fact]
    public async Task Validate_ForwardsIfMatch()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, """{"valid":true}"""));

        await Build(stub).ValidateAsync(AgentId, AdminCtx, "\"1\"", null);

        Assert.Equal($"http://backend/api/agents/{AgentIdText}/validate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("\"1\"", stub.LastRequest!.Headers.IfMatch.Single().ToString());
    }

    [Fact]
    public async Task UpdateDraft_WithoutIfMatch_DoesNotSendHeader()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, "{}"));

        await Build(stub).UpdateDraftAsync(AgentId, AdminCtx, null, null);

        Assert.Empty(stub.LastRequest!.Headers.IfMatch);
    }

    // ---- 狀態碼穿透(決策表的兩半:downstream 狀態碼 → 對外狀態碼 + body/ETag)----

    // 409(版本過期):backend 對 draft 樂觀鎖用 409(不是 412)—— 透明代理原樣帶回狀態碼、ApiError body 與目前版本的 ETag。
    [Fact]
    public async Task UpdateDraft_Backend409Stale_PassesThroughStatusBodyAndEtag()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            (HttpStatusCode)409,
            """{"timestamp":"2026-07-24T00:00:00Z","status":409,"message":"草稿版本衝突","fieldErrors":{}}""",
            "\"5\""));

        var result = await Build(stub).UpdateDraftAsync(AgentId, AdminCtx, "\"1\"", Json("{}"));

        Assert.Equal(409, result.Status);
        Assert.Equal("\"5\"", result.ETag);
        Assert.Equal("草稿版本衝突", Json(result.Body).GetProperty("message").GetString());
    }

    // 428(缺 If-Match 前置條件):backend 專屬碼,既有例外對照表沒有 —— 透明代理原樣帶回。
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(428)]
    public async Task Backend4xx_PassesThroughStatusAndBodyVerbatim(int status)
    {
        var body = $"{{\"timestamp\":\"2026-07-24T00:00:00Z\",\"status\":{status},\"message\":\"下游訊息\",\"fieldErrors\":{{\"slug\":\"重複\"}}}}";
        var stub = new StubHttpMessageHandler(_ => Resp((HttpStatusCode)status, body));

        var result = await Build(stub).CreateAsync(AdminCtx, Json("{}"));

        Assert.Equal(status, result.Status);
        var parsed = Json(result.Body);
        Assert.Equal("下游訊息", parsed.GetProperty("message").GetString());
        // fieldErrors 原樣穿透(不被代理層吞掉)。
        Assert.Equal("重複", parsed.GetProperty("fieldErrors").GetProperty("slug").GetString());
    }

    // off-point:5xx 邊界 —— 500 不穿透 backend body,收斂成對外 502(隱藏內部細節)。
    [Fact]
    public async Task Backend500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => Build(stub).ListAsync(AdminCtx));
        Assert.Contains("500", ex.Message);
    }

    // 「沒有回應」的等價類:傳輸失敗 → 502(不誤判成使用者輸入問題)。
    [Fact]
    public async Task TransportFailure_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒"));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => Build(stub).ListAsync(AdminCtx));
        Assert.Contains("Agent 服務呼叫失敗", ex.Message);
    }
}
