using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Service.Dtos;
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
        var requests = new List<(string Path, string? IdempotencyKey, string? Body)>();
        var backend = new StubHttpMessageHandler(request =>
        {
            requests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
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
        string? backendBody = null;
        var backend = new StubHttpMessageHandler(request =>
        {
            backendBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Command($$"""{"id":"{{RunIdText}}","status":"queued"}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, "{}"));
        var expected = new string('x', 16_384);
        var padded = new string(' ', 20_000) + expected + new string(' ', 20_000);

        await Build(backend, workflow).StartAsync(
            AgentId, padded, "trimmed-start", Admin);

        using var backendJson = JsonDocument.Parse(backendBody!);
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

    [Fact]
    public async Task Resume_AllocatesCheckpointCommand_AndKicksWithOnlyCommandId()
    {
        var backendRequests = new List<(string Path, string? IdempotencyKey, string? Body)>();
        var backend = new StubHttpMessageHandler(request =>
        {
            backendRequests.Add((
                request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"queued","state_version":7}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, $$"""{"run_id":"{{RunIdText}}","status":"queued"}"""));

        await Build(backend, workflow).ResumeAsync(
            RunId, "more context", 3, "resume-1", Admin);

        Assert.Equal($"/api/runs/{RunIdText}/resume", backendRequests[0].Path);
        Assert.Equal("resume-1", backendRequests[0].IdempotencyKey);
        using (var body = JsonDocument.Parse(backendRequests[0].Body!))
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

    [Fact]
    public async Task Resume_TrimsMessageOnlyInDurableBackendAllocation()
    {
        string? backendBody = null;
        var backend = new StubHttpMessageHandler(request =>
        {
            backendBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Command(
                $$"""{"id":"{{RunIdText}}","status":"queued","state_version":7}""");
        });
        var workflow = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Accepted, "{}"));

        await Build(backend, workflow).ResumeAsync(
            RunId,
            new string(' ', 20_000) + "x" + new string(' ', 20_000),
            3,
            "trimmed-resume",
            Admin);

        using var backendJson = JsonDocument.Parse(backendBody!);
        using var workflowJson = JsonDocument.Parse(workflow.LastBody!);
        Assert.Equal("x", backendJson.RootElement.GetProperty("message").GetString());
        Assert.Equal(new[] { "command_id" }, workflowJson.RootElement.EnumerateObject().Select(p => p.Name));
    }

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
        var backendCalls = 0;
        string? backendBody = null;
        var backend = new StubHttpMessageHandler(request =>
        {
            backendCalls++;
            backendBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
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
        using var sent = JsonDocument.Parse(backendBody!);
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
