using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.Common;
using Backend.Api.OrchestratorRuns;
using Backend.Api.Contexts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    /// <summary>控制字元等價類的代表值(U+0001);Conversation() 與 Key() 都用 Any(char.IsControl) 擋。</summary>
    private const char ControlChar = (char)1;
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

    // X-User-Role 是 orchestrator_run.caller_role(NOT NULL)的唯一來源。帶著 workflow.manage 卻沒有
    // 角色標頭時,必須在 controller 就 400 —— 否則 null 會一路穿到倉儲,對外變成 500。
    [Fact]
    public async Task Start_WithoutUserRoleHeader_IsBadRequest()
    {
        var client = _factory.CreateInternalClient().WithTenant("d5-api").WithUser("root-operator");
        client.DefaultRequestHeaders.TryAddWithoutValidation(IdentityHeaders.CapabilitiesHeader, "workflow.manage");

        using var request = StartRequest(new { conversation_id = "c-1", message = "plan" }, "no-role-key");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("X-User-Role is required", error!.Message);
    }

    // Idempotency-Key / conversation_id / message 的長度、空白與控制字元邊界。上限剛好通過(會走到倉儲、
    // 因為 orchestrator 不存在而 404),上限 +1 必須在 controller 就被擋成 400 或 413。
    // message 過長對外是 413(與 AgentRunController.RequireMessage 的行為一致),空/空白仍是 400。
    // Conversation() 與 Key() 除了長度還各自擋 Trim 後為空與 Any(char.IsControl) —— 兩個等價類都要有代表值。
    public static TheoryData<string, object, HttpStatusCode> StartInputs => new()
    {
        { "start-key", new { conversation_id = "c-1", message = "" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "c-1", message = "   " }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "c-1", message = new string('m', 16_385) }, HttpStatusCode.RequestEntityTooLarge },
        { "start-key", new { conversation_id = "", message = "plan" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "   ", message = "plan" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = "c" + ControlChar, message = "plan" }, HttpStatusCode.BadRequest },
        { "start-key", new { conversation_id = new string('c', 129), message = "plan" }, HttpStatusCode.BadRequest },
        { "", new { conversation_id = "c-1", message = "plan" }, HttpStatusCode.BadRequest },
        { "   ", new { conversation_id = "c-1", message = "plan" }, HttpStatusCode.BadRequest },
        { "k" + ControlChar, new { conversation_id = "c-1", message = "plan" }, HttpStatusCode.BadRequest },
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

    // 重複的 Idempotency-Key 標頭:舊的 .ToString() 會把多值拼成 "a, b",變成一把從未用過的**新** key
    // 而順利放行 —— 重試因此被記成新的 root run,冪等去重靜默失效。共用 helper 要求恰好一個標頭。
    [Fact]
    public async Task Start_RejectsMultipleIdempotencyKeyHeaders()
    {
        var client = Client("workflow.manage");

        using var request = StartRequest(new { conversation_id = "c-1", message = "plan" }, "start-a");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "start-b");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("Idempotency-Key is required", error!.Message);
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
    public async Task ContextRequests_AreOwnerScoped_AndDeltaRequiresIfMatch()
    {
        var client = Client(capability: null);
        var child = Guid.NewGuid(); var requestId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(
            $"/api/orchestrator-runs/{UnknownRun:D}/children/{child:D}/context-requests", null)).StatusCode);

        var path = $"/api/orchestrator-runs/{UnknownRun:D}/children/{child:D}/context-requests/{requestId:D}/deltas";
        var missing = await client.PostAsJsonAsync(path, new { definition = new { }, views = new[] { new { view_type = "worker", definition = new { } } } });
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { definition = new { }, views = new[] { new { view_type = "worker", definition = new { } } } })
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(stale)).StatusCode);
    }

    // If-Match 的三種等價類必須分流:缺 → 428(上面那格)、格式非法/低於下界 → 400、
    // 合法但過期 → 409。"1" 是 on-point 合法值,"0" 是它的 off-point。
    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"0\"")]
    [InlineData("abc")]
    public async Task ContextDelta_InvalidIfMatch_IsBadRequest(string ifMatch)
    {
        var client = Client(capability: null);
        var path = $"/api/orchestrator-runs/{UnknownRun:D}/children/{Guid.NewGuid():D}/context-requests/{Guid.NewGuid():D}/deltas";

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { definition = new { }, views = new[] { new { view_type = "worker", definition = new { } } } })
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    // 決策表的另一半:倉儲丟出的 Context 例外必須映到規格上的對外狀態碼,而不是 500。
    // policy 不可用是暫時性的 503,policy 本身壞掉是 422,候選不合法是 400。
    [Theory]
    [InlineData("unavailable", 503)]
    [InlineData("invalid", 422)]
    [InlineData("argument", 400)]
    public async Task ContextDelta_RepositoryFailures_MapToTheContractStatusCodes(string failure, int expected)
    {
        var controller = E3Controller(new E3OnlyRepository { Failure = failure }, out var http);
        http.Request.Headers.IfMatch = "\"1\"";
        var delta = new OrchestratorContextDeltaRequest(JsonSerializer.SerializeToElement(new { }), [], [new("worker", JsonSerializer.SerializeToElement(new { }))], new ContextObjectiveMeasurements(1, 1));

        var thrown = await Assert.ThrowsAsync<ApiException>(
            () => controller.AppendContextDelta(E3OnlyRepository.Root, E3OnlyRepository.Child, E3OnlyRepository.RequestId, delta, default));

        Assert.Equal(expected, thrown.Status);
    }

    // M7:server-owned Context 投影是在倉儲裡跑的,它產出的 envelope 若超過白名單契約上限,
    // 對外必須是永久的 400(與 controller 前置檢查同一語意),不是 Workflow 會重試的 500。
    [Fact]
    public async Task CreateChild_WhenServerProjectionRejectsTheEnvelope_IsBadRequest()
    {
        var controller = E3Controller(new E3OnlyRepository { Failure = "argument" }, out _);
        var request = new OrchestratorChildCreateRequest("worker-task", 1, "worker", Guid.NewGuid(), 1,
            TaskEnvelope: EnvelopeOfLength(null), TokenCap: 1000);

        var thrown = await Assert.ThrowsAsync<ApiException>(() => controller.CreateChild(E3OnlyRepository.Root, request, default));

        Assert.Equal(400, thrown.Status);
    }

    private static OrchestratorRunController E3Controller(E3OnlyRepository repository, out DefaultHttpContext http)
    {
        http = new DefaultHttpContext();
        http.Request.Headers[IdentityHeaders.TenantHeader] = "d5-api";
        http.Request.Headers[IdentityHeaders.UserHeader] = "root-operator";
        return new OrchestratorRunController(repository) { ControllerContext = new() { HttpContext = http } };
    }

    [Fact]
    public async Task ContextRequests_ApiSuccess_UsesRequestVersionEtag_AndMarksStaleIfMatch()
    {
        var controller = E3Controller(new E3OnlyRepository(), out var http);

        var created = Assert.IsType<OkObjectResult>(await controller.CreateContextRequest(E3OnlyRepository.Root, E3OnlyRepository.Child, default));
        var request = Assert.IsType<OrchestratorContextRequestResponse>(created.Value);
        Assert.Equal("\"1\"", http.Response.Headers.ETag.ToString());
        Assert.Equal("worker", request.Role);

        http.Response.Headers.Clear(); http.Request.Headers.IfMatch = "\"1\"";
        var delta = new OrchestratorContextDeltaRequest(JsonSerializer.SerializeToElement(new { }), [], [new("worker", JsonSerializer.SerializeToElement(new { }))], new ContextObjectiveMeasurements(1, 1));
        var appended = Assert.IsType<OkObjectResult>(await controller.AppendContextDelta(E3OnlyRepository.Root, E3OnlyRepository.Child, request.Id, delta, default));
        Assert.IsType<ContextRevisionResponse>(appended.Value);
        Assert.Equal("\"2\"", http.Response.Headers.ETag.ToString());

        var stale = await Assert.ThrowsAsync<ApiException>(() => controller.AppendContextDelta(E3OnlyRepository.Root, E3OnlyRepository.Child, request.Id, delta, default));
        Assert.Equal(409, stale.Status);
        Assert.Equal("Context request version is stale", stale.FieldErrors!["If-Match"]);
    }

    // 上面所有 404 用的都是「從沒被建立過」的 id,只證明了「不存在 → 404」這一半。
    // 這裡拿同一份真的存在、擁有者讀得到的 context-request,只換掉 tenant 或 user 再讀一次:
    // 「存在但不屬於你」必須回一模一樣的 404 訊息,否則狀態碼本身就洩漏了資源存在。
    [Theory]
    [InlineData("other-tenant", "root-operator")]
    [InlineData("d5-api", "other-operator")]
    public async Task RunReads_ExistingResourceUnderAnotherOwner_AreIndistinguishableFromMissing(string tenant, string user)
    {
        var repository = new E3OnlyRepository();
        var owner = E3Controller(repository, out _);
        var created = Assert.IsType<OkObjectResult>(
            await owner.CreateContextRequest(E3OnlyRepository.Root, E3OnlyRepository.Child, default));
        var existing = Assert.IsType<OrchestratorContextRequestResponse>(created.Value);
        Assert.IsType<OkObjectResult>(
            await owner.GetContextRequest(E3OnlyRepository.Root, E3OnlyRepository.Child, existing.Id, default));

        var intruder = E3Controller(repository, out var http);
        http.Request.Headers[IdentityHeaders.TenantHeader] = tenant;
        http.Request.Headers[IdentityHeaders.UserHeader] = user;

        var thrown = await Assert.ThrowsAsync<ApiException>(
            () => intruder.GetContextRequest(E3OnlyRepository.Root, E3OnlyRepository.Child, existing.Id, default));

        Assert.Equal(404, thrown.Status);
        Assert.Equal("Orchestrator run not found", thrown.Message);
        Assert.Equal("", http.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task ContextRequests_FeatureOff_IsUndiscoverable()
    {
        using var factory = new ContextDisabledFactory();
        var client = factory.CreateInternalClient().WithTenant("d5-api").WithUser("root-operator");
        var response = await client.PostAsync($"/api/orchestrator-runs/{Guid.NewGuid():D}/children/{Guid.NewGuid():D}/context-requests", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private sealed class E3OnlyRepository : IOrchestratorRunRepository
    {
        public static readonly Guid Root = Guid.Parse("11111111-1111-4111-8111-111111111111");
        public static readonly Guid Child = Guid.Parse("22222222-2222-4222-8222-222222222222");
        public static readonly Guid RequestId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        private readonly Guid _request = RequestId;
        private long _version = 1;
        private readonly Guid _context = Guid.Parse("44444444-4444-4444-8444-444444444444");
        /// <summary>Which Context failure the repository raises, so the controller's exception → status
        /// mapping is exercised instead of only its happy path.</summary>
        public string? Failure { get; init; }
        private void Fail() => throw (Exception?)(Failure switch
        {
            "unavailable" => new ContextPolicyUnavailableException(),
            "invalid" => new ContextPolicyInvalidException("Active context policy is malformed"),
            "argument" => new ArgumentException("task_envelope is required and too large"),
            _ => null,
        }) ?? new InvalidOperationException("no failure configured");
        public Task<OrchestratorContextRequestResponse?> GetOrCreateContextRequestAsync(string tenant, string user, Guid root, Guid child, CancellationToken ct)
            => Task.FromResult(tenant == "d5-api" && user == "root-operator" && root == Root && child == Child ? Request() : null);
        public Task<OrchestratorContextRequestResponse?> GetContextRequestAsync(string tenant, string user, Guid root, Guid child, Guid request, CancellationToken ct)
            => Task.FromResult(tenant == "d5-api" && user == "root-operator" && root == Root && child == Child && request == _request ? Request() : null);
        public Task<OrchestratorContextDeltaResult> AppendContextDeltaAsync(string tenant, string user, Guid root, Guid child, Guid request, long expected, OrchestratorContextDeltaRequest delta, CancellationToken ct)
        {
            if (Failure is not null) Fail();
            if (tenant != "d5-api" || user != "root-operator" || root != Root || child != Child || request != _request) return Task.FromResult(new OrchestratorContextDeltaResult(OrchestratorContextDeltaStatus.NotFound));
            if (expected != _version) return Task.FromResult(new OrchestratorContextDeltaResult(OrchestratorContextDeltaStatus.Conflict));
            _version++;
            var response = new ContextRevisionResponse(_context, 1, Root, ContextStatuses.Ready, 1m, [], Guid.NewGuid(), JsonSerializer.SerializeToElement(new { }), DateTime.UtcNow, DateTime.UtcNow, null, new ContextRef(_context, 1, Guid.NewGuid()));
            return Task.FromResult(new OrchestratorContextDeltaResult(OrchestratorContextDeltaStatus.Success, response, _version));
        }
        private OrchestratorContextRequestResponse Request() => new(_request, Root, Child, "task", "worker", _context, null, null, _version, DateTime.UtcNow, DateTime.UtcNow);
        public Task<OrchestratorRunWriteResult> CreateAsync(string a,string b,string c,IReadOnlyCollection<string>d,IReadOnlyCollection<string>e,Guid f,string g,string h,string i,CancellationToken j)=>throw new NotSupportedException();
        public Task<OrchestratorRunResponse?> GetAsync(string a,string b,Guid c,CancellationToken d)=>throw new NotSupportedException(); public Task<OrchestratorRunActiveLookup> FindActiveAsync(string a,string b,string c,CancellationToken d)=>throw new NotSupportedException(); public Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string a,string b,string c,OrchestratorRunReplayRequest d,CancellationToken e)=>throw new NotSupportedException(); public Task<OrchestratorRunEventsResponse?> EventsAsync(string a,string b,Guid c,long d,int e,CancellationToken f)=>throw new NotSupportedException(); public Task<OrchestratorRunWriteResult> CancelAsync(string a,string b,Guid c,string? d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<OrchestratorRunWriteResult> ResumeAsync(string a,string b,Guid c,string d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<string?> ExecutionArtifactAsync(string a,string b,Guid c,CancellationToken d)=>throw new NotSupportedException(); public Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string a,string b,Guid c,Guid d,string e,int f,CancellationToken g)=>throw new NotSupportedException(); public Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string a,string b,Guid c,Guid d,string e,long f,int g,CancellationToken h)=>throw new NotSupportedException(); public Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string a,string b,Guid c,Guid d,string e,CancellationToken f)=>throw new NotSupportedException(); public Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string a,int b,int c,CancellationToken d)=>throw new NotSupportedException(); public Task<OrchestratorChildResponse?> CreateChildAsync(string a,string b,Guid c,OrchestratorChildCreateRequest d,CancellationToken e){Fail();throw new NotSupportedException();} public Task<OrchestratorChildStatusResponse?> GetChildAsync(string a,string b,Guid c,Guid d,CancellationToken e)=>throw new NotSupportedException(); public Task<OrchestratorRunWriteResult> TransitionAsync(string a,string b,Guid c,OrchestratorRootTransitionRequest d,CancellationToken e)=>throw new NotSupportedException(); public Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string a,string b,Guid c,OrchestratorContextAcquireRequest d,CancellationToken e)=>throw new NotSupportedException();
    }

    private sealed class ContextDisabledFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("CONTEXT_ENRICHMENT_ENABLED", "false");
        }
    }
}
