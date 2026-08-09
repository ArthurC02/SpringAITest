using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service.Tests;

public sealed class AgentRunServiceTests
{
    private const string AgentIdText = "11111111-1111-1111-1111-111111111111";
    private const string RunIdText = "44444444-4444-4444-4444-444444444444";
    private const string CommandIdText = "55555555-5555-5555-5555-555555555555";
    private const string DispatchClaim = "dispatch-claim";
    private static readonly Guid AgentId = Guid.Parse(AgentIdText);
    private static readonly Guid RunId = Guid.Parse(RunIdText);
    private static readonly Guid CommandId = Guid.Parse(CommandIdText);
    private static readonly UserContext Admin = new("admin-a", "demo-a", "ADMIN");

    private static AgentRunService Build(
        StubHttpMessageHandler backend,
        StubHttpMessageHandler workflow)
        => new(
            TestBackend.Client(backend),
            new HttpClient(workflow),
            new WorkflowOptions
            {
                BaseUrl = "http://workflow",
                InternalToken = "tok",
            },
            NullLogger<AgentRunService>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => TestHttp.Json(status, body);

    private static HttpResponseMessage Command(
        string body,
        bool dispatchRequired = true,
        bool replayed = false)
    {
        if (dispatchRequired)
        {
            var internalBody = JsonNode.Parse(body)!.AsObject();
            internalBody["command_id"] = CommandId;
            body = internalBody.ToJsonString();
        }
        var response = Json(HttpStatusCode.Accepted, body);
        response.Headers.TryAddWithoutValidation(
            "X-Agent-Run-Dispatch-Required",
            dispatchRequired.ToString());
        response.Headers.TryAddWithoutValidation(
            "X-Agent-Run-Replayed",
            replayed.ToString());
        if (dispatchRequired)
        {
            response.Headers.TryAddWithoutValidation(
                "X-Agent-Run-Command-Id",
                CommandIdText);
            response.Headers.TryAddWithoutValidation(
                "X-Agent-Run-Dispatch-Claim",
                DispatchClaim);
        }
        return response;
    }

    [Fact]
    public async Task Start_AllocatesCommand_AndKicksWorkflowWithOnlyCommandId()
    {
        // Body is deliberately not captured here (never asserted below) -- StubHttpMessageHandler
        // already reads it into LastBody before invoking this delegate, so re-reading
        // request.Content synchronously would just be a redundant blocking call.
        var requests = new List<(string Path, string? IdempotencyKey)>();
        var backend = new StubHttpMessageHandler(request =>
        {
            requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null));
            return Command($$"""{"id":"{{RunIdText}}","status":"queued"}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, $$"""{"run_id":"{{RunIdText}}","status":"queued"}"""));

        var result = await Build(backend, workflow).StartAsync(
            AgentId, "hello", "idem-1", Admin);

        Assert.Equal(202, result.Status);
        using (var publicBody = JsonDocument.Parse(result.Body))
        {
            Assert.False(publicBody.RootElement.TryGetProperty(
                "command_id",
                out _));
        }
        Assert.Equal(
            new[]
            {
                $"/api/agents/{AgentIdText}/runs",
            },
            requests.Select(r => r.Path));
        Assert.Equal("idem-1", requests[0].IdempotencyKey);
        Assert.Equal($"http://workflow/agent-runs/{RunIdText}/start", workflow.LastRequest!.RequestUri!.ToString());
        Assert.Equal("demo-a", workflow.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", workflow.Header("X-User-Id"));
        using var sent = JsonDocument.Parse(workflow.LastBody!);
        Assert.Equal(new[] { "command_id" }, sent.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(CommandId, sent.RootElement.GetProperty("command_id").GetGuid());
    }

    [Fact]
    public async Task Start_TrimsMessageOnlyInDurableBackendAllocation()
    {
        // Single-call handler: reading the body back via backend.LastBody after the await
        // (StubHttpMessageHandler already reads it there) avoids a redundant blocking read here.
        var backend = new StubHttpMessageHandler(_ =>
            Command($$"""{"id":"{{RunIdText}}","status":"queued"}"""));
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, "{}"));
        var expected = new string('x', 16_384);
        var padded = new string(' ', 20_000) + expected + new string(' ', 20_000);

        await Build(backend, workflow).StartAsync(
            AgentId, padded, "trimmed-start", Admin);

        using var backendJson = JsonDocument.Parse(backend.LastBody!);
        using var workflowJson = JsonDocument.Parse(workflow.LastBody!);
        Assert.Equal(
            expected,
            backendJson.RootElement.GetProperty("message").GetString());
        Assert.Equal(new[] { "command_id" }, workflowJson.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Start_Backend4xx_PassesThroughWithoutCallingWorkflow()
    {
        var backend = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Conflict, """{"status":409,"message":"not published"}"""));
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return Json(HttpStatusCode.Accepted, "{}");
        });

        var result = await Build(backend, workflow).StartAsync(
            AgentId, "hello", "idem-2", Admin);

        Assert.Equal(409, result.Status);
        Assert.Equal(0, workflowCalls);
    }

    /// <summary>讀取路徑:GET /api/runs/{id} 原樣穿透 backend 的 status/body,並帶簽發身分;讀取不得觸發 Workflow。</summary>
    [Fact]
    public async Task Get_ForwardsRunDetailPathAndSignedIdentity()
    {
        var backend = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK,
            $$"""{"id":"{{RunIdText}}","status":"running","state_version":4}"""));
        var workflow = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("讀取執行狀態不得觸發 Workflow"));

        var result = await Build(backend, workflow).GetAsync(RunId, Admin);

        Assert.Equal($"http://backend/api/runs/{RunIdText}", backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, backend.LastRequest.Method);
        Assert.Equal("demo-a", backend.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", backend.Header("X-User-Id"));
        Assert.Equal(200, result.Status);
        Assert.Equal($$"""{"id":"{{RunIdText}}","status":"running","state_version":4}""", result.Body);
    }

    /// <summary>
    /// after_sequence / limit 在 platform 這層不做驗證也不夾擠,原樣以不變文化格式串進查詢字串
    /// (0、負數、型別上界都照送);兩個參數名對調或順序寫錯會在這裡爆。
    /// </summary>
    [Theory]
    [InlineData(0L, 1, "after_sequence=0&limit=1")]
    [InlineData(-1L, 0, "after_sequence=-1&limit=0")]
    [InlineData(long.MaxValue, int.MaxValue, "after_sequence=9223372036854775807&limit=2147483647")]
    public async Task Events_ForwardsSequenceAndLimitBoundariesVerbatim(
        long afterSequence, int limit, string expectedQuery)
    {
        var backend = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "[]"));
        var workflow = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("讀取事件不得觸發 Workflow"));

        var result = await Build(backend, workflow).EventsAsync(RunId, afterSequence, limit, Admin);

        Assert.Equal(
            $"http://backend/api/runs/{RunIdText}/events?{expectedQuery}",
            backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, backend.LastRequest.Method);
        Assert.Equal("demo-a", backend.Header("X-Tenant-Id"));
        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task Resume_AllocatesCheckpointCommand_AndKicksWithOnlyCommandId()
    {
        // Body is read back via backend.LastBody after the await (single-call handler; see
        // Start_TrimsMessageOnlyInDurableBackendAllocation) rather than a redundant blocking read.
        var backendRequests = new List<(string Path, string? IdempotencyKey)>();
        var backend = new StubHttpMessageHandler(request =>
        {
            backendRequests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null));
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"queued","state_version":7}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, $$"""{"run_id":"{{RunIdText}}","status":"queued"}"""));

        await Build(backend, workflow).ResumeAsync(
            RunId, "more context", 3, "resume-1", Admin);

        Assert.Equal($"/api/runs/{RunIdText}/resume", backendRequests[0].Path);
        Assert.Equal("resume-1", backendRequests[0].IdempotencyKey);
        using (var body = JsonDocument.Parse(backend.LastBody!))
        {
            Assert.Equal("more context", body.RootElement.GetProperty("message").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("expected_checkpoint_version").GetInt64());
        }
        Assert.Single(backendRequests);
        Assert.Equal($"http://workflow/agent-runs/{RunIdText}/resume", workflow.LastRequest!.RequestUri!.ToString());
        using var workflowBody = JsonDocument.Parse(workflow.LastBody!);
        Assert.Equal(new[] { "command_id" }, workflowBody.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(CommandId, workflowBody.RootElement.GetProperty("command_id").GetGuid());
    }

    // Resume 的 message?.Trim() 是 Start 的同一行語意(AgentRunService.cs 兩個 caller);
    // resume 側的 trim 另由 ReplayedCommand_ReturnsOriginalRun_WithoutWorkflowOrAck 驗過。

    [Fact]
    public async Task Cancel_PersistsFirst_ThenKicksWorkflowWithOnlyCommandId()
    {
        var backendRequests = new List<(string Path, string? IdempotencyKey)>();
        var backend = new StubHttpMessageHandler(request =>
        {
            backendRequests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null));
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"cancelled","state_version":9}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, $$"""{"run_id":"{{RunIdText}}","status":"cancelled"}"""));

        await Build(backend, workflow).CancelAsync(
            RunId, "operator request", "cancel-1", Admin);

        Assert.Equal("cancel-1", backendRequests[0].IdempotencyKey);
        Assert.Single(backendRequests);
        using var sent = JsonDocument.Parse(workflow.LastBody!);
        Assert.Equal(new[] { "command_id" }, sent.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(CommandId, sent.RootElement.GetProperty("command_id").GetGuid());
    }

    [Fact]
    public async Task ReplayedCommand_ReturnsOriginalRun_WithoutWorkflowOrAck()
    {
        // Body is read back via backend.LastBody after the await (single-call handler; see
        // Start_TrimsMessageOnlyInDurableBackendAllocation) rather than a redundant blocking read.
        var backendCalls = 0;
        var backend = new StubHttpMessageHandler(request =>
        {
            backendCalls++;
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"queued","state_version":7}""",
                dispatchRequired: false,
                replayed: true);
        });
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return Json(HttpStatusCode.Accepted, "{}");
        });

        var result = await Build(backend, workflow).ResumeAsync(
            RunId,
            "  same input  ",
            3,
            "resume-replay",
            Admin);

        Assert.Equal(202, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(RunId, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(1, backendCalls);
        Assert.Equal(0, workflowCalls);
        using var sent = JsonDocument.Parse(backend.LastBody!);
        Assert.Equal(
            "same input",
            sent.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Start_ForwardsExactAuthenticatedCapabilityClaims_WhenPresent()
    {
        string[]? forwarded = null;
        string[]? forwardedGroups = null;
        var backend = new StubHttpMessageHandler(request =>
        {
            forwarded = request.Headers.TryGetValues(
                "X-User-Capabilities",
                out var values)
                ? values.Single().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : null;
            forwardedGroups = request.Headers.TryGetValues(
                "X-User-Groups",
                out var groupValues)
                ? groupValues.Single().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : null;
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"queued","state_version":1}""",
                dispatchRequired: false,
                replayed: true);
        });
        var workflow = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("replay must not dispatch"));
        var context = Admin with
        {
            Capabilities = new[]
            {
                "tool.use:local.calculator",
                "knowledge.read:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            },
            Groups = new[] { "operations", "reviewers" },
        };

        await Build(backend, workflow).StartAsync(
            AgentId,
            "hello",
            "capability-start",
            context);

        Assert.Equal(context.Capabilities, forwarded);
        Assert.Equal(context.Groups, forwardedGroups);
    }

    /// <summary>
    /// capabilities/groups 齊備**且**真的需要 dispatch 時,Workflow kick 也必須帶同一組簽發身分——
    /// 上一個測試走 replay(根本不 kick),這半邊組合之前沒有任何斷言守著。
    /// </summary>
    [Fact]
    public async Task Start_ForwardsCapabilityClaimsToWorkflowKick_WhenDispatchRequired()
    {
        var backend = new StubHttpMessageHandler(_ =>
            Command($$"""{"id":"{{RunIdText}}","status":"queued"}"""));
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, $$"""{"run_id":"{{RunIdText}}","status":"queued"}"""));
        var context = Admin with
        {
            Capabilities = new[] { "tool.use:local.calculator" },
            Groups = new[] { "operations", "reviewers" },
        };

        await Build(backend, workflow).StartAsync(
            AgentId,
            "hello",
            "capability-dispatch",
            context);

        Assert.Equal($"http://workflow/agent-runs/{RunIdText}/start", workflow.LastRequest!.RequestUri!.ToString());
        Assert.Equal("demo-a", workflow.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", workflow.Header("X-User-Id"));
        Assert.Equal("tool.use:local.calculator", workflow.Header("X-User-Capabilities"));
        Assert.Equal("operations reviewers", workflow.Header("X-User-Groups"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task WorkflowHttpFailure_StillReturnsDurableAcceptedRun(HttpStatusCode status)
    {
        var backendPaths = new List<string>();
        var backend = new StubHttpMessageHandler(request =>
        {
            backendPaths.Add(request.RequestUri!.AbsolutePath);
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"queued","state_version":7}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(status));

        var result = await Build(backend, workflow).ResumeAsync(
                RunId,
                "retry later",
                3,
                "resume-failure",
                Admin);

        Assert.Equal(202, result.Status);
        Assert.Equal(new[] { $"/api/runs/{RunIdText}/resume" }, backendPaths);
    }

    // ---- D7 approvals(service 層之前 0 覆蓋;reject 分支在 platform 從未被執行過)----

    [Fact]
    public async Task Approvals_ForwardsListPathAndSignedIdentity()
    {
        var backend = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK, """[{"id":"66666666-6666-4666-8666-666666666666","status":"pending"}]"""));
        var workflow = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("讀取待審清單不得觸發 Workflow"));

        var result = await Build(backend, workflow).ApprovalsAsync(RunId, Admin);

        Assert.Equal($"http://backend/api/runs/{RunIdText}/approvals", backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, backend.LastRequest.Method);
        Assert.Equal("demo-a", backend.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", backend.Header("X-User-Id"));
        Assert.Equal(200, result.Status);
    }

    /// <summary>
    /// O3 discoverable approval queue: Backend owns both predicates and the keyset cursor codec,
    /// so this service layer's only job is to build the query string exactly (scope/limit always
    /// present; cursor only when non-blank — never invented when the caller omitted it).
    /// </summary>
    [Theory]
    [InlineData("visible", null, 20, "scope=visible&limit=20")]
    [InlineData("actionable", "abc123", 5, "scope=actionable&limit=5&cursor=abc123")]
    [InlineData("visible", "", 20, "scope=visible&limit=20")]
    public async Task Queue_ForwardsScopeLimitAndOptionalCursorPathVerbatim(
        string scope, string? cursor, int limit, string expectedQuery)
    {
        var backend = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.OK, """{"items":[],"next_cursor":null,"has_more":false}"""));
        var workflow = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("讀取核准佇列不得觸發 Workflow"));

        var result = await Build(backend, workflow).QueueAsync(scope, cursor, limit, Admin);

        Assert.Equal(
            $"http://backend/api/runs/approvals?{expectedQuery}",
            backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, backend.LastRequest.Method);
        Assert.Equal("demo-a", backend.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", backend.Header("X-User-Id"));
        Assert.Equal(200, result.Status);
    }

    /// <summary>
    /// approve 走 /approve 後綴、reason 去頭尾空白、Idempotency-Key 原樣轉發,成功後 best-effort kick
    /// Workflow 的一次性寫入執行;reject 走 /reject 後綴且**絕不**觸發該 kick(拒絕不得產生任何效果)。
    /// </summary>
    [Theory]
    [InlineData(true, "approve", 1)]
    [InlineData(false, "reject", 0)]
    public async Task DecideApproval_UsesDecisionSuffix_AndOnlyApproveKicksWorkflow(
        bool approve, string expectedSuffix, int expectedWorkflowCalls)
    {
        // Body is read back via backend.LastBody after the await (single-call handler; see
        // Start_TrimsMessageOnlyInDurableBackendAllocation) rather than a redundant blocking read.
        var approvalId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        string? idempotencyKey = null;
        var backend = new StubHttpMessageHandler(request =>
        {
            idempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? values.Single()
                : null;
            return Json(HttpStatusCode.Accepted, """{"id":"66666666-6666-4666-8666-666666666666","status":"decided"}""");
        });
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return Json(HttpStatusCode.Accepted, "{}");
        });

        var result = await Build(backend, workflow).DecideApprovalAsync(
            RunId, approvalId, approve, "  已複核  ", "approval-attempt-1", Admin);

        Assert.Equal(202, result.Status);
        Assert.Equal(
            $"http://backend/api/runs/{RunIdText}/approvals/{approvalId:D}/{expectedSuffix}",
            backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal("approval-attempt-1", idempotencyKey);
        using (var sent = JsonDocument.Parse(backend.LastBody!))
        {
            Assert.Equal("已複核", sent.RootElement.GetProperty("reason").GetString());
        }

        Assert.Equal(expectedWorkflowCalls, workflowCalls);
        if (expectedWorkflowCalls > 0)
        {
            Assert.Equal(
                $"http://workflow/agent-runs/{RunIdText}/approvals/{approvalId:D}/execute",
                workflow.LastRequest!.RequestUri!.ToString());
        }
    }

    /// <summary>Backend 拒絕(SoD/過期/非 waiting)原樣穿透,且失敗的決策不得觸發一次性寫入。</summary>
    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public async Task DecideApproval_BackendRejection_PassesThroughWithoutKick(int status)
    {
        var backend = new StubHttpMessageHandler(_ => Json(
            (HttpStatusCode)status, $$"""{"status":{{status}},"message":"下游決策訊息"}"""));
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return Json(HttpStatusCode.Accepted, "{}");
        });

        var result = await Build(backend, workflow).DecideApprovalAsync(
            RunId, Guid.Parse("66666666-6666-4666-8666-666666666666"), true, "r", "k", Admin);

        Assert.Equal(status, result.Status);
        Assert.Contains("下游決策訊息", result.Body, StringComparison.Ordinal);
        Assert.Equal(0, workflowCalls);
    }

    /// <summary>核准已耐久落地後,Workflow kick 是 best-effort:掛掉也不得改寫已回給審批者的狀態碼。</summary>
    [Fact]
    public async Task DecideApproval_ApprovedWriteKickFails_KeepsDurableDecisionStatus()
    {
        var backend = new StubHttpMessageHandler(_ => Json(
            HttpStatusCode.Accepted, """{"id":"66666666-6666-4666-8666-666666666666","status":"approved"}"""));
        var workflow = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("workflow unavailable"));

        var result = await Build(backend, workflow).DecideApprovalAsync(
            RunId, Guid.Parse("66666666-6666-4666-8666-666666666666"), true, null, null, Admin);

        Assert.Equal(202, result.Status);
        Assert.Contains("approved", result.Body, StringComparison.Ordinal);
    }

    // ---- Backend 命令 metadata 不可信時的受控失敗(全部收斂成 502,不得靜默跳過 dispatch)----

    [Theory]
    [InlineData("missing-dispatch-header")]
    [InlineData("non-boolean-dispatch-header")]
    [InlineData("missing-command-id-header")]
    [InlineData("non-guid-command-id")]
    [InlineData("body-missing-id")]
    [InlineData("body-non-guid-id")]
    [InlineData("body-numeric-id")]
    [InlineData("backend-5xx")]
    public async Task BackendCommandMetadata_Invalid_MapsToControlledFailure(string scenario)
    {
        var backend = new StubHttpMessageHandler(_ =>
        {
            if (scenario == "backend-5xx")
            {
                return Json(HttpStatusCode.InternalServerError, """{"detail":"secret"}""");
            }

            // id 有三種不可信等價類:缺鍵、字串但非 GUID、非字串 JSON 型別(對應 RequiredGuid 的三個合取條件)。
            var body = scenario switch
            {
                "body-missing-id" =>
                    """{"status":"queued","command_id":"55555555-5555-5555-5555-555555555555"}""",
                "body-non-guid-id" =>
                    """{"id":"not-a-guid","status":"queued","command_id":"55555555-5555-5555-5555-555555555555"}""",
                "body-numeric-id" =>
                    """{"id":123,"status":"queued","command_id":"55555555-5555-5555-5555-555555555555"}""",
                _ => $$"""{"id":"{{RunIdText}}","status":"queued","command_id":"{{CommandIdText}}"}""",
            };
            var response = Json(HttpStatusCode.Accepted, body);
            if (scenario != "missing-dispatch-header")
            {
                response.Headers.TryAddWithoutValidation(
                    "X-Agent-Run-Dispatch-Required",
                    scenario == "non-boolean-dispatch-header" ? "yes" : "True");
            }

            if (scenario != "missing-command-id-header")
            {
                response.Headers.TryAddWithoutValidation(
                    "X-Agent-Run-Command-Id",
                    scenario == "non-guid-command-id" ? "not-a-guid" : CommandIdText);
            }

            return response;
        });
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return Json(HttpStatusCode.Accepted, "{}");
        });

        var error = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(backend, workflow).StartAsync(AgentId, "hello", "metadata-" + scenario, Admin));

        Assert.StartsWith("Agent 執行服務失敗：", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, workflowCalls);
    }

    [Fact]
    public async Task WorkflowTransportException_StillReturnsDurableAcceptedRun()
    {
        var backendPaths = new List<string>();
        var backend = new StubHttpMessageHandler(request =>
        {
            backendPaths.Add(request.RequestUri!.AbsolutePath);
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"cancelled","state_version":8}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            throw new HttpRequestException("workflow unavailable"));

        var result = await Build(backend, workflow).CancelAsync(
            RunId,
            "operator",
            "cancel-failure",
            Admin);

        Assert.Equal(202, result.Status);
        Assert.Equal(new[] { $"/api/runs/{RunIdText}/cancel" }, backendPaths);
    }
}
