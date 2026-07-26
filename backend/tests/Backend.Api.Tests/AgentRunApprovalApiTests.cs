using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;

namespace Backend.Api.Tests;

/// <summary>HTTP-level D7 acceptance: a business approver needs no workflow.manage.</summary>
public sealed class AgentRunApprovalApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;
    public AgentRunApprovalApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task ApprovalApi_EnforcesTenantRoleSoDReplayAndRejectsGenericResume()
    {
        using var admin = Client("demo-a", "admin-a", "ADMIN");
        var agentId = await PublishedUserAgentAsync(admin);
        using var owner = Client("demo-a", "admin-a", "ADMIN");
        var running = await StartRunningAsync(owner, agentId);
        var run = running.Run;
        var fingerprint = new string('e', 64);
        var approval = await CreateApprovalAsync(owner, running, fingerprint, "USER");
        var approvalId = approval["id"]!.GetValue<string>();
        var runId = run["id"]!.GetValue<string>();

        // No capability header is supplied: normal USER role is the runtime
        // authority when it matches required_role, not workflow.manage.
        using var wrongRole = Client("demo-a", "admin-a", "ADMIN");
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(wrongRole, runId, approvalId, true, "wrong-role")).StatusCode);
        using var selfApprover = Client("demo-a", "admin-a", "USER");
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(selfApprover, runId, approvalId, true, "self")).StatusCode);
        using var crossTenant = Client("demo-b", "user-b", "USER");
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(crossTenant, runId, approvalId, true, "cross-tenant")).StatusCode);

        using var genericResume = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/resume")
        {
            Content = JsonContent.Create(new { message = "approve", expected_checkpoint_version = 1 }),
        };
        genericResume.Headers.TryAddWithoutValidation("Idempotency-Key", "generic-resume");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.SendAsync(genericResume)).StatusCode);

        using var approver = Client("demo-a", "user-b", "USER");
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(approver, runId, approvalId, true, "business-approval")).StatusCode);
        // An idempotency retry is an anti-replay response: it must not enqueue
        // another write execution.
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(approver, runId, approvalId, true, "business-approval")).StatusCode);
    }

    /// <summary>
    /// GET /api/runs/{runId}/approvals 是 root AGENTS.md 點名的 approver 入口。兩件事必須釘住:
    /// ① 瀏覽器投影不得洩漏 action_fingerprint / checkpoint_ref / requested_by / reason(公開遮蔽契約);
    /// ② 清單依角色過濾,且跨租戶不洩漏存在性(404,不是空陣列)。
    /// </summary>
    [Fact]
    public async Task ApprovalList_IsRoleFilteredTenantScoped_AndRedactsSensitiveFields()
    {
        using var admin = Client("demo-a", "admin-a", "ADMIN");
        var agent = await PublishedUserAgentAsync(admin);
        using var owner = Client("demo-a", "admin-a", "ADMIN");
        var running = await StartRunningAsync(owner, agent);
        var runId = running.Run["id"]!.GetValue<string>();
        var approval = await CreateApprovalAsync(owner, running, new string('a', 64), "USER");
        var approvalId = approval["id"]!.GetValue<string>();

        var ownerList = await owner.GetAsync($"/api/runs/{runId}/approvals");
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
        var items = (await ownerList.ReadJsonAsync()).AsArray();
        var item = Assert.Single(items)!.AsObject();
        Assert.Equal(approvalId, item["id"]!.GetValue<string>());
        Assert.Equal(
            new[]
            {
                "decided_at", "decision", "expires_at", "id",
                "required_role", "run_id", "self_approval_forbidden", "status",
            },
            item.Select(pair => pair.Key).Order(StringComparer.Ordinal));

        // 合格 approver(角色相符、非發起人)看得到;角色不符者看到空清單而非別人的待辦。
        using var approver = Client("demo-a", "user-b", "USER");
        Assert.Equal(
            approvalId,
            Assert.Single((await (await approver.GetAsync($"/api/runs/{runId}/approvals")).ReadJsonAsync()).AsArray())!
                ["id"]!.GetValue<string>());
        using var unrelated = Client("demo-a", "user-c", "ADMIN");
        Assert.Empty((await (await unrelated.GetAsync($"/api/runs/{runId}/approvals")).ReadJsonAsync()).AsArray());

        using var crossTenant = Client("demo-b", "user-b", "USER");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await crossTenant.GetAsync($"/api/runs/{runId}/approvals")).StatusCode);
    }

    private HttpClient Client(string tenant, string user, string role)
        => _factory.CreateInternalClient().WithTenant(tenant).WithUser(user).WithRole(role);

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, string runId, string approvalId, bool approve, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/approvals/{approvalId}/{(approve ? "approve" : "reject")}")
        {
            Content = JsonContent.Create(new { reason = "D7 test" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private async Task<JsonNode> PublishedUserAgentAsync(HttpClient admin)
    {
        var create = await admin.PostAsJsonAsync("/api/agents", new
        {
            slug = "d7-approval-" + Guid.NewGuid().ToString("N"), name = "D7 approval", description = "test", system_prompt = "test",
            execution_roles = new[] { "worker" }, audience = new[] { "ADMIN" }, allowed_tools = Array.Empty<string>(), knowledge_sources = Array.Empty<string>(),
            runtime_limits = new { timeout_seconds = 60, step_budget = 8 },
            runtime_workflow = new { id = AgentDefaults.RuntimeWorkflowId, revision = AgentDefaults.RuntimeWorkflowRevision },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var agent = await create.ReadJsonAsync();
        var id = agent["id"]!.GetValue<string>();
        using var validate = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/validate");
        validate.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(validate)).StatusCode);
        using var publish = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/publish") { Content = JsonContent.Create(new { expected_draft_version = 1 }) };
        publish.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(publish)).StatusCode);
        return agent;
    }

    private sealed record RunningRun(JsonNode Run, string LeaseToken, long LeaseGeneration);

    private static async Task<RunningRun> StartRunningAsync(HttpClient owner, JsonNode agent)
    {
        var agentId = agent["id"]!.GetValue<string>();
        using var start = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{agentId}/runs") { Content = JsonContent.Create(new { message = "write evidence" }) };
        start.Headers.TryAddWithoutValidation("Idempotency-Key", "start-" + Guid.NewGuid().ToString("N"));
        var started = await owner.SendAsync(start);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = await started.ReadJsonAsync();
        var runId = run["id"]!.GetValue<string>();
        var lease = await owner.PostAsJsonAsync($"/api/agent-runs/{runId}/lease", new { expected_version = run["state_version"]!.GetValue<long>(), owner = "workflow", duration_seconds = 120 });
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        var leaseBody = await lease.ReadJsonAsync();
        var transition = await owner.PostAsJsonAsync($"/api/agent-runs/{runId}/transitions", new
        {
            expected_version = leaseBody["run"]!["state_version"]!.GetValue<long>(), to_status = "running",
            lease_token = leaseBody["lease_token"]!.GetValue<string>(), lease_generation = leaseBody["lease_generation"]!.GetValue<long>(),
            expected_event_ack_cursor = leaseBody["event_ack_cursor"]!.GetValue<long>(),
        });
        Assert.Equal(HttpStatusCode.OK, transition.StatusCode);
        return new RunningRun(await transition.ReadJsonAsync(), leaseBody["lease_token"]!.GetValue<string>(), leaseBody["lease_generation"]!.GetValue<long>());
    }

    private static async Task<JsonNode> CreateApprovalAsync(HttpClient owner, RunningRun running, string fingerprint, string role)
    {
        var run = running.Run;
        var runId = run["id"]!.GetValue<string>();
        var create = await owner.PostAsJsonAsync($"/api/agent-runs/{runId}/approvals", new
        {
            expected_version = run["state_version"]!.GetValue<long>(), lease_token = running.LeaseToken, lease_generation = running.LeaseGeneration,
            checkpoint_ref = $"v2:{running.LeaseGeneration}:{new string('f', 64)}:{Guid.NewGuid():D}", checkpoint_version = 1,
            required_role = role, action_fingerprint = fingerprint, expires_at = DateTime.UtcNow.AddMinutes(10), self_approval_forbidden = false,
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        return await create.ReadJsonAsync();
    }
}
