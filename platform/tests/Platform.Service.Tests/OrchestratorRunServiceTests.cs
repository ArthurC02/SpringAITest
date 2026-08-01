using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service.Tests;

/// <summary>
/// D5 root dispatch 的 service 層。Backend 先落地、Platform 只 best-effort 送 {command_id, context:{}}
/// 給 Workflow;耐久命令身分是內部執行憑據,絕不進公開回應(root AGENTS.md D5:「Internal execution
/// claims and recovery are never public APIs」),與 D3 共用 RunCommandRedaction.StripCommandId。
/// </summary>
public sealed class OrchestratorRunServiceTests
{
    private const string OrchestratorIdText = "66666666-6666-6666-6666-666666666666";
    private const string RunIdText = "55555555-5555-5555-5555-555555555555";
    private const string CommandIdText = "77777777-7777-7777-7777-777777777777";
    private static readonly Guid OrchestratorId = Guid.Parse(OrchestratorIdText);
    private static readonly Guid RunId = Guid.Parse(RunIdText);

    /// <summary>cancel 只要求「已認證的 owner」——USER 也拿得到這個回應,故洩漏面比 start 更廣。</summary>
    private static readonly UserContext Owner = new("owner", "tenant-x", "USER");

    private static OrchestratorRunService Build(
        StubHttpMessageHandler backend, StubHttpMessageHandler workflow)
        => new(
            TestBackend.Client(backend),
            new HttpClient(workflow),
            new WorkflowOptions { BaseUrl = "http://workflow", InternalToken = "tok" },
            NullLogger<OrchestratorRunService>.Instance);

    /// <summary>backend 的 run 形狀:扁平 run + command_id。寫入(start/cancel)來自
    /// OrchestratorRunController.Accepted;讀取(get)來自 repository 的 Columns 子查詢(start 命令 id)。</summary>
    private static HttpResponseMessage Run(HttpStatusCode status) => TestHttp.Json(
        status,
        $$"""{"id":"{{RunIdText}}","status":"queued","state_version":1,"command_id":"{{CommandIdText}}"}""");

    /// <summary>
    /// D5 的核心順序:Backend 先耐久落地(唯一權威),Platform 才 best-effort 通知 Workflow,
    /// 且只送 {command_id, context:{}} —— 絕不送解碼後的 snapshot、也不自己 claim root command。
    /// </summary>
    [Fact]
    public async Task Start_AllocatesAtBackendFirst_ThenDispatchesOnlyCommandIdAndEmptyContext()
    {
        var backendCalls = new List<(string Path, string Method, string? Key, string? Body)>();
        var backend = new StubHttpMessageHandler(request =>
        {
            backendCalls.Add((
                request.RequestUri!.AbsolutePath,
                request.Method.Method,
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Run(HttpStatusCode.Accepted);
        });
        var workflowCallsBeforeBackend = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCallsBeforeBackend = backendCalls.Count;
            return TestHttp.Json(HttpStatusCode.Accepted, "{}");
        });

        var result = await Build(backend, workflow).StartAsync(
            OrchestratorId, "hello", "conversation-1", "root-key", Owner);

        Assert.Equal(202, result.Status);
        var allocation = Assert.Single(backendCalls);
        Assert.Equal($"/api/admin/orchestrators/{OrchestratorIdText}/runs", allocation.Path);
        Assert.Equal("POST", allocation.Method);
        Assert.Equal("root-key", allocation.Key);
        using (var sentToBackend = JsonDocument.Parse(allocation.Body!))
        {
            Assert.Equal(
                new[] { "message", "conversation_id" },
                sentToBackend.RootElement.EnumerateObject().Select(p => p.Name));
        }

        // Backend 的落地一定發生在 dispatch 之前(否則 Workflow 可能 claim 到還不存在的命令)。
        Assert.Equal(1, workflowCallsBeforeBackend);
        Assert.Equal(
            $"http://workflow/orchestrator-runs/{RunIdText}/dispatch",
            workflow.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tenant-x", workflow.Header("X-Tenant-Id"));
        using var kick = JsonDocument.Parse(workflow.LastBody!);
        Assert.Equal(
            new[] { "command_id", "context" },
            kick.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(CommandIdText, kick.RootElement.GetProperty("command_id").GetString());
        Assert.Empty(kick.RootElement.GetProperty("context").EnumerateObject());
    }

    /// <summary>
    /// dispatch 是 best-effort:Workflow 回錯誤碼或整個沒有回應,都不得回滾已受理的 root run
    /// (耐久命令留著讓 Workflow 之後回收)。共用的 InternalRequest.KickBestEffortAsync 刻意寬 catch,
    /// 任何下游失敗型別都不得逃逸給呼叫端。
    /// </summary>
    [Theory]
    [InlineData("http-503")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task Start_WorkflowKickFails_StillReturnsDurableAccepted(string failure)
    {
        var backend = new StubHttpMessageHandler(_ => Run(HttpStatusCode.Accepted));
        var workflow = new StubHttpMessageHandler(_ => failure switch
        {
            "http-503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "transport" => throw new HttpRequestException("workflow unavailable"),
            _ => throw new TaskCanceledException("workflow timed out"),
        });

        var result = await Build(backend, workflow).StartAsync(
            OrchestratorId, "hello", "conversation-1", "root-key", Owner);

        Assert.Equal(202, result.Status);
        using var body = JsonDocument.Parse(result.Body);
        Assert.Equal(RunIdText, body.RootElement.GetProperty("id").GetString());
    }

    /// <summary>
    /// A 2xx allocation without both durable identities is a broken Backend contract, not a
    /// recoverable Workflow-kick failure. Never return a false successful allocation or send an
    /// ambiguous dispatch that Workflow cannot safely claim.
    /// </summary>
    [Theory]
    [InlineData("""{"status":"queued","command_id":"77777777-7777-7777-7777-777777777777"}""")]
    [InlineData("""{"id":"not-a-guid","command_id":"77777777-7777-7777-7777-777777777777"}""")]
    [InlineData("""{"id":"55555555-5555-5555-5555-555555555555","status":"queued"}""")]
    [InlineData("""{"id":"55555555-5555-5555-5555-555555555555","command_id":"not-a-guid"}""")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Start_SuccessfulAllocationWithoutValidDispatchIds_FailsClosed(string body)
    {
        var backend = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Accepted, body));
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return TestHttp.Json(HttpStatusCode.Accepted, "{}");
        });

        var error = await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            Build(backend, workflow).StartAsync(
                OrchestratorId, "hello", "conversation-1", "root-key", Owner));

        Assert.Contains("缺少有效 id 或 command_id", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, workflowCalls);
    }

    /// <summary>Backend 未受理就沒有耐久命令可 claim:4xx 原樣穿透、5xx 收斂成受控 502,兩者都不得 dispatch。</summary>
    [Fact]
    public async Task Start_Backend4xxOr5xx_DoesNotDispatch()
    {
        var workflowCalls = 0;
        var workflow = new StubHttpMessageHandler(_ =>
        {
            workflowCalls++;
            return TestHttp.Json(HttpStatusCode.Accepted, "{}");
        });

        var conflict = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.Conflict,
            """{"status":409,"message":"Orchestrator 未發布"}"""));
        var rejected = await Build(conflict, workflow).StartAsync(
            OrchestratorId, "hello", "conversation-1", "root-key", Owner);
        Assert.Equal(409, rejected.Status);
        Assert.Contains("Orchestrator 未發布", rejected.Body, StringComparison.Ordinal);

        var broken = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.InternalServerError, """{"detail":"secret"}"""));
        var error = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(broken, workflow).StartAsync(
                OrchestratorId, "hello", "conversation-1", "root-key", Owner));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);

        Assert.Equal(0, workflowCalls);
    }

    /// <summary>
    /// 公開的 camelCase cursor → Backend 的 snake_case query;預設 0/100 與 InvariantCulture 格式化
    /// (千分位分隔會讓 Backend 解析失敗)。events 走的是另一個 DTO,不經 Redact。
    /// </summary>
    [Theory]
    [InlineData(0L, 100, "after_sequence=0&limit=100")]
    [InlineData(1234567L, 5000, "after_sequence=1234567&limit=5000")]
    public async Task Events_MapsCursorToSnakeCaseQuery(long after, int limit, string expectedQuery)
    {
        var backend = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK, $$"""{"run_id":"{{RunIdText}}","events":[],"next_sequence":{{after}}}"""));
        var workflow = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("events 不得觸發 dispatch"));

        var result = await Build(backend, workflow).EventsAsync(RunId, after, limit, Owner);

        Assert.Equal(
            $"http://backend/api/orchestrator-runs/{RunIdText}/events?{expectedQuery}",
            backend.LastRequest!.RequestUri!.ToString());
        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task PublicRunBody_DoesNotExposeInternalCommandId_ButStillDispatchesIt()
    {
        var backend = new StubHttpMessageHandler(request => Run(
            request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Accepted));
        var workflow = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.Accepted, "{}"));
        var service = Build(backend, workflow);

        var started = await service.StartAsync(
            OrchestratorId, "hello", "conversation-1", "root-key", Owner);
        var dispatched = workflow.LastBody!;
        var cancelled = await service.CancelAsync(RunId, "stop", "cancel-key", Owner);
        var read = await service.GetAsync(RunId, Owner);

        // 命令身分仍要送到 Workflow(修正不得靠「不再讀 command_id」而讓 dispatch 失效)。
        using var kick = JsonDocument.Parse(dispatched);
        Assert.Equal(CommandIdText, kick.RootElement.GetProperty("command_id").GetString());

        foreach (var (label, response) in new[] { ("start", started), ("cancel", cancelled), ("get", read) })
        {
            using var body = JsonDocument.Parse(response.Body);
            Assert.False(
                body.RootElement.TryGetProperty("command_id", out _),
                $"{label} 的公開回應洩漏了內部耐久命令身分 command_id");
            // 其餘公開欄位必須原樣保留(只剝除內部欄位,不是整包改寫)。
            Assert.Equal(RunIdText, body.RootElement.GetProperty("id").GetString());
            Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("state_version").GetInt32());
        }
    }
}
