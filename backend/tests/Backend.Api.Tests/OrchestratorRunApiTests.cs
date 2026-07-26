using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.Common;
using Backend.Api.OrchestratorRuns;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// D5 <c>OrchestratorRunController</c> 的 HTTP 層守門。倉儲層行為已由 OrchestratorRunRepositoryTests 覆蓋,
/// 這裡只驗「capability 精確比對 / 輸入邊界 / 擁有者範圍不洩漏存在性」這三件只有 controller 才決定的事。
/// </summary>
public sealed class OrchestratorRunApiTests : IClassFixture<OrchestratorRunApiTests.Factory>
{
    private static readonly Guid UnknownRun = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private readonly Factory _factory;

    public OrchestratorRunApiTests(Factory factory) => _factory = factory;

    // SYSTEM_ADMIN test-start 需要精確的 workflow.manage;ADMIN 角色本身不隱含它,近似字串也不行。
    [Theory]
    [InlineData(null)]
    [InlineData("workflow.manage.all")]
    [InlineData("WORKFLOW.MANAGE")]
    [InlineData("workflow.manag")]
    public async Task Start_WithoutExactWorkflowManage_IsForbidden(string? capability)
    {
        var client = Client(capability);

        using var request = StartRequest(new { conversation_id = "c-1", message = "plan" }, "start-key");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    // Idempotency-Key / conversation_id / message 的長度與空白邊界。上限剛好通過(會走到倉儲、
    // 因為 orchestrator 不存在而 404),上限 +1 必須在 controller 就被擋成 400 或 413。
    // message 過長對外是 413(與 AgentRunController.RequireMessage 的行為一致),空/空白仍是 400。
    public static TheoryData<string, object, HttpStatusCode> StartInputs => new()
    {
        { "start-key", new { conversation_id = "c-1", message = "" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "c-1", message = "   " }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "c-1", message = new string('m', 16_385) }, HttpStatusCode.RequestEntityTooLarge },
        { "start-key", new { conversation_id = "", message = "plan" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = new string('c', 129), message = "plan" }, HttpStatusCode.BadRequest },
        { "", new { conversation_id = "c-1", message = "plan" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "c-1", message = new string('m', 16_384) }, HttpStatusCode.NotFound },
        { "start-key", new { conversation_id = new string('c', 128), message = "plan" }, HttpStatusCode.NotFound },
    };

    [Theory]
    [MemberData(nameof(StartInputs))]
    public async Task Start_ValidatesIdempotencyKeyConversationAndMessage(
        string key, object body, HttpStatusCode expected)
    {
        var client = Client("workflow.manage");

        using var request = StartRequest(body, key);
        Assert.Equal(expected, (await client.SendAsync(request)).StatusCode);
    }

    // message 過長要單獨鎖住 413 的 ApiError 形狀,不能只驗狀態碼:400/413 對「訊息太長」講出兩種
    // 不同的話會讓呼叫端(Workflow 的 _request)分不出「請求本身壞掉」跟「內容超過大小上限」。
    [Fact]
    public async Task Start_OversizedMessage_ReturnsApiErrorShapeWith413()
    {
        var client = Client("workflow.manage");

        using var request = StartRequest(
            new { conversation_id = "c-1", message = new string('m', 16_385) }, "oversized-key");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(413, error!.Status);
        Assert.Equal("message is too large", error.Message);
        Assert.Empty(error.FieldErrors);
    }

    [Fact]
    public async Task Start_OversizedIdempotencyKey_IsRejected()
    {
        var client = Client("workflow.manage");
        var body = new { conversation_id = "c-1", message = "plan" };

        using var tooLong = StartRequest(body, new string('k', 129));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(tooLong)).StatusCode);
        using var atLimit = StartRequest(body, new string('k', 128));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(atLimit)).StatusCode);
    }

    // 讀取端的游標守門與 Agent run 端一致:off-point 400,on-point 通過驗證後才因為 run 不存在而 404。
    [Theory]
    [InlineData("after_sequence=-1&limit=10", HttpStatusCode.BadRequest)]
    [InlineData("after_sequence=0&limit=0", HttpStatusCode.BadRequest)]
    [InlineData("after_sequence=0&limit=201", HttpStatusCode.BadRequest)]
    [InlineData("after_sequence=0&limit=1", HttpStatusCode.NotFound)]
    [InlineData("after_sequence=0&limit=200", HttpStatusCode.NotFound)]
    public async Task Events_CursorQueryBoundaries(string query, HttpStatusCode expected)
    {
        var client = Client("workflow.manage");

        var response = await client.GetAsync($"/api/orchestrator-runs/{UnknownRun:D}/events?{query}");

        Assert.Equal(expected, response.StatusCode);
    }

    // GET / events / cancel / execution-artifact 都以 tenant+owner 過濾,查不到一律 404,
    // 不得因為「存在但不屬於你」而回不同狀態碼。這些路由不需要 workflow.manage。
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/events")]
    [InlineData("GET", "/execution-artifact")]
    [InlineData("POST", "/cancel")]
    public async Task RunReads_AreOwnerScoped_AndDoNotLeakExistence(string method, string suffix)
    {
        var client = Client(capability: null);

        using var request = new HttpRequestMessage(
            new HttpMethod(method), $"/api/orchestrator-runs/{UnknownRun:D}{suffix}");
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { reason = "stop" });
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "cancel-unknown");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Cancel_RequiresIdempotencyKey()
    {
        var client = Client(capability: null);

        var response = await client.PostAsJsonAsync(
            $"/api/orchestrator-runs/{UnknownRun:D}/cancel", new { reason = "stop" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // recovery/claim 的輸入邊界:倉儲對非法 worker_id/limit/lease_seconds 直接丟
    // ArgumentOutOfRangeException(OrchestratorRunRepository.cs:231、InMemory 同一組界線),
    // controller 必須先擋成 400,否則對外是 500。on-point 值必須放行(200)。
    public static TheoryData<object, HttpStatusCode> RecoveryInputs => new()
    {
        { new { worker_id = "", limit = 20, lease_seconds = 30 }, HttpStatusCode.BadRequest },
        { new { worker_id = "   ", limit = 20, lease_seconds = 30 }, HttpStatusCode.BadRequest },
        { new { worker_id = new string('w', 257), limit = 20, lease_seconds = 30 }, HttpStatusCode.BadRequest },
        { new { worker_id = "recovery", limit = 0, lease_seconds = 30 }, HttpStatusCode.BadRequest },
        { new { worker_id = "recovery", limit = 101, lease_seconds = 30 }, HttpStatusCode.BadRequest },
        { new { worker_id = "recovery", limit = 20, lease_seconds = 0 }, HttpStatusCode.BadRequest },
        { new { worker_id = "recovery", limit = 20, lease_seconds = 301 }, HttpStatusCode.BadRequest },
        { new { worker_id = new string('w', 256), limit = 1, lease_seconds = 1 }, HttpStatusCode.OK },
        { new { worker_id = "recovery", limit = 100, lease_seconds = 300 }, HttpStatusCode.OK },
    };

    [Theory]
    [MemberData(nameof(RecoveryInputs))]
    public async Task Recovery_ValidatesWorkerLimitAndLeaseSeconds(object body, HttpStatusCode expected)
    {
        var client = Client(capability: null);

        var response = await client.PostAsJsonAsync("/api/orchestrator-runs/recovery/claim", body);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Recovery_InvalidInput_ReturnsApiErrorShape()
    {
        var client = Client(capability: null);

        var response = await client.PostAsJsonAsync(
            "/api/orchestrator-runs/recovery/claim", new { worker_id = "recovery", limit = 0, lease_seconds = 30 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(400, error!.Status);
        Assert.Equal("worker_id、limit 或 lease_seconds 無效", error.Message);
        Assert.Empty(error.FieldErrors);
    }

    // children 的輸入界線:生產倉儲(OrchestratorRunRepository.cs:244 + OrchestratorTaskEnvelope.Validate)
    // 對這組畸形請求丟 ArgumentException,GlobalExceptionHandler 只認 ApiException → 對外是 500。
    // 而 Workflow 的 _request 只把 {400,404,413,422,428} 當永久性錯誤(會 fail-closed 取消 root),
    // 500 會被當成可重試 —— 一個永遠不會變好的畸形 envelope 因此無限重試。必須在 controller 就是 400。
    // on-point 值要通過驗證,走到倉儲後因為 run 不存在而 409。
    public static TheoryData<object, HttpStatusCode> ChildInputs => new()
    {
        { ChildBody(taskId: ""), HttpStatusCode.BadRequest },
        { ChildBody(taskId: "   "), HttpStatusCode.BadRequest },
        { ChildBody(taskId: new string('t', 129)), HttpStatusCode.BadRequest },
        { ChildBody(taskId: "task\u0001id"), HttpStatusCode.BadRequest },
        { ChildBody(attempt: 0), HttpStatusCode.BadRequest },
        { ChildBody(runKind: "planner"), HttpStatusCode.BadRequest },
        { ChildBody(runKind: "Worker"), HttpStatusCode.BadRequest },
        { ChildBody(writeIntent: true), HttpStatusCode.BadRequest },
        { ChildBody(envelope: JsonDocument.Parse("null").RootElement.Clone()), HttpStatusCode.BadRequest },
        { ChildBody(envelope: JsonDocument.Parse("[]").RootElement.Clone()), HttpStatusCode.BadRequest },
        { ChildBody(envelope: EnvelopeOfLength(65_537)), HttpStatusCode.BadRequest },
        { ChildBody(taskId: new string('t', 128)), HttpStatusCode.Conflict },
        { ChildBody(attempt: 1), HttpStatusCode.Conflict },
        { ChildBody(runKind: "verifier"), HttpStatusCode.Conflict },
        { ChildBody(envelope: EnvelopeOfLength(65_536)), HttpStatusCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(ChildInputs))]
    public async Task CreateChild_ValidatesTaskAttemptKindWriteIntentAndEnvelope(object body, HttpStatusCode expected)
    {
        var client = Client(capability: null);

        var response = await client.PostAsJsonAsync($"/api/orchestrator-runs/{UnknownRun:D}/children", body);

        Assert.Equal(expected, response.StatusCode);
    }

    // 決策表的另一半:例外 → 對外狀態碼 + ApiError 形狀。envelope 內部語意錯誤的固定字串必須原樣帶出。
    [Fact]
    public async Task CreateChild_InvalidEnvelope_ReturnsApiErrorShape()
    {
        var client = Client(capability: null);

        var response = await client.PostAsJsonAsync(
            $"/api/orchestrator-runs/{UnknownRun:D}/children",
            ChildBody(envelope: JsonDocument.Parse("""{"objective":"research"}""").RootElement.Clone()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(400, error!.Status);
        Assert.Equal("task_envelope has unsupported fields", error.Message);
        Assert.Empty(error.FieldErrors);
    }

    private static object ChildBody(
        string taskId = "task-1", int attempt = 1, string runKind = "worker",
        bool writeIntent = false, object? envelope = null)
        => new
        {
            task_id = taskId,
            attempt,
            run_kind = runKind,
            agent_id = Guid.NewGuid(),
            agent_revision = 1,
            write_intent = writeIntent,
            task_envelope = envelope ?? EnvelopeOfLength(null),
            token_cap = 1000,
        };

    /// <summary>合法 envelope;<paramref name="length"/> 非 null 時把 context 值補到剛好那個總長度。</summary>
    private static JsonElement EnvelopeOfLength(int? length)
    {
        var text = Envelope("q");
        return JsonDocument.Parse(length is null ? text : Envelope(new string('q', length.Value - text.Length + 1)))
            .RootElement.Clone();

        static string Envelope(string query) => $$"""
            {"objective":"research","required_capabilities":[],"context":{"query":"{{query}}"},"context_provenance":[{"context_key":"query","source_type":"caller","source_id":"user","observed_at":"2026-01-01T00:00:00Z","content_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],"write_intent":false,"delegation_depth":0}
            """;
    }

    private static HttpRequestMessage StartRequest(object body, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/orchestrators/{Guid.NewGuid():D}/runs")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    private HttpClient Client(string? capability)
    {
        var client = _factory.CreateInternalClient().WithTenant("d5-api").WithUser("root-operator").WithRole("SYSTEM_ADMIN");
        if (capability is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(IdentityHeaders.CapabilitiesHeader, capability);
        }
        return client;
    }

    /// <summary>沿用共用工廠,只把 D5 倉儲換成 lite 模式的同一份生產碼(不連 PostgreSQL)。</summary>
    public sealed class Factory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrchestratorRunRepository>();
                services.AddSingleton<IOrchestratorRunRepository, InMemoryOrchestratorRunRepository>();
            });
        }
    }
}
