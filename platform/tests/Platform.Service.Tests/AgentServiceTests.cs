using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

/// <summary>
/// AgentService(透明代理 backend /api/agents,D1)。收斂成單一 <c>SendAsync</c> 後,「哪個 action 走哪條路徑」
/// 是 AgentController 的決策,由 <c>AgentApiTests.Routes_ForwardExactBackendPathSuffix</c> 在 Web 層釘住;
/// 這一層只剩這顆代理自己的契約:
/// (a) 每個請求都帶 X-Internal-Token + 三個身分 header(取自已驗證 JWT),capabilities/groups 有才帶;
/// (b) suffix 接在 /api/agents 之後(空字串 = 集合本身),If-Match 有值才轉發;
/// (c) backend 的狀態碼、body 與 ETag 原樣穿透;5xx 與傳輸失敗才收斂成對外 502。
/// </summary>
public sealed class AgentServiceTests
{
    private const string AgentIdText = "11111111-1111-1111-1111-111111111111";
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

        var result = await Build(stub).SendAsync(HttpMethod.Get, string.Empty, AdminCtx);

        // 空 suffix = 集合本身,不多掛一條斜線。
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

        await Build(stub).SendAsync(HttpMethod.Get, string.Empty, CapCtx);

        Assert.Equal("workflow.manage agent.author", stub.Header("X-User-Capabilities"));
    }

    [Fact]
    public async Task List_RegeneratesGroupsHeaderFromUserContext()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, "[]"));

        await Build(stub).SendAsync(HttpMethod.Get, string.Empty, GroupCtx);

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
            () => Build(stub).SendAsync(HttpMethod.Get, string.Empty, context));
        Assert.Null(stub.LastRequest);
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

    // ---- suffix / body / ETag ----

    [Fact]
    public async Task Create_PostsBodyToCollection()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.Created, """{"id":"a2"}"""));

        var result = await Build(stub).SendAsync(
            HttpMethod.Post, string.Empty, AdminCtx, body: Json("""{"slug":"new-agent","name":"新代理"}"""));

        Assert.Equal("http://backend/api/agents", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal(201, result.Status);
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("new-agent", sent.RootElement.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task NonEmptySuffix_IsAppendedToCollectionPath_AndEtagIsReturned()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            HttpStatusCode.OK,
            """{"id":"11111111-1111-1111-1111-111111111111","draft_version":3}""",
            "\"3\""));

        var result = await Build(stub).SendAsync(HttpMethod.Get, AgentIdText, AdminCtx);

        Assert.Equal($"http://backend/api/agents/{AgentIdText}", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(200, result.Status);
        Assert.Equal("\"3\"", result.ETag);
        Assert.Equal(3, Json(result.Body).GetProperty("draft_version").GetInt32());
    }

    // ---- If-Match 轉發(樂觀鎖)----

    [Fact]
    public async Task IfMatch_AndBody_AreForwardedVerbatim()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, """{"draft_version":2}""", "\"2\""));

        await Build(stub).SendAsync(
            HttpMethod.Put, $"{AgentIdText}/draft", AdminCtx, "\"1\"", Json("""{"name":"改名"}"""));

        Assert.Equal($"http://backend/api/agents/{AgentIdText}/draft", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);
        Assert.Equal("\"1\"", stub.LastRequest!.Headers.IfMatch.Single().ToString());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("改名", sent.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithoutIfMatch_DoesNotSendHeader()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(HttpStatusCode.OK, "{}"));

        await Build(stub).SendAsync(HttpMethod.Put, $"{AgentIdText}/draft", AdminCtx);

        Assert.Empty(stub.LastRequest!.Headers.IfMatch);
    }

    // ---- 狀態碼穿透(決策表的兩半:downstream 狀態碼 → 對外狀態碼 + body/ETag)----

    // 409(版本過期):backend 對 draft 樂觀鎖用 409(不是 412)—— 透明代理原樣帶回狀態碼、ApiError body 與目前版本的 ETag。
    [Fact]
    public async Task Backend409Stale_PassesThroughStatusBodyAndEtag()
    {
        var stub = new StubHttpMessageHandler(_ => Resp(
            (HttpStatusCode)409,
            """{"timestamp":"2026-07-24T00:00:00Z","status":409,"code":"version_conflict","message":"草稿版本衝突","correlationId":"backend-trace-1","fieldErrors":{}}""",
            "\"5\""));

        var result = await Build(stub).SendAsync(
            HttpMethod.Put, $"{AgentIdText}/draft", AdminCtx, "\"1\"", Json("{}"));

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
        var body = $"{{\"timestamp\":\"2026-07-24T00:00:00Z\",\"status\":{status},\"code\":\"backend_code\",\"message\":\"下游訊息\",\"correlationId\":\"backend-trace-1\",\"fieldErrors\":{{\"slug\":\"重複\"}}}}";
        var stub = new StubHttpMessageHandler(_ => Resp((HttpStatusCode)status, body));

        var result = await Build(stub).SendAsync(HttpMethod.Post, string.Empty, AdminCtx, body: Json("{}"));

        Assert.Equal(status, result.Status);
        var parsed = Json(result.Body);
        Assert.Equal("下游訊息", parsed.GetProperty("message").GetString());
        // fieldErrors 原樣穿透(不被代理層吞掉)。
        Assert.Equal("重複", parsed.GetProperty("fieldErrors").GetProperty("slug").GetString());
        // code/correlationId 是 backend 自己的欄位,代理層不得改寫或補上本地推導值。
        Assert.Equal("backend_code", parsed.GetProperty("code").GetString());
        Assert.Equal("backend-trace-1", parsed.GetProperty("correlationId").GetString());
    }

    // off-point:5xx 邊界 —— 500 不穿透 backend body,收斂成對外 502(隱藏內部細節)。
    [Fact]
    public async Task Backend500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).SendAsync(HttpMethod.Get, string.Empty, AdminCtx));
        Assert.Contains("500", ex.Message);
    }

    // 「沒有回應」的等價類:傳輸失敗 → 502(不誤判成使用者輸入問題)。
    [Fact]
    public async Task TransportFailure_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒"));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).SendAsync(HttpMethod.Get, string.Empty, AdminCtx));
        Assert.Contains("Agent 服務呼叫失敗", ex.Message);
    }
}
