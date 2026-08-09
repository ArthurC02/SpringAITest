using System.Net;
using System.Net.Http.Json;
using Backend.Api.Data.InMemory;
using Backend.Api.OrchestratorRuns;
using Backend.Api.RunDiscovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// HTTP-level O2 acceptance (04-operations-trigger-plan.md §3): GET /api/runs, flag/capability
/// gating, tenant+owner scoping, keyset pagination, DTO field boundary, and consistency with the
/// existing per-run detail endpoint. Filter/pagination/visibility correctness at the repository
/// layer is separately covered by <see cref="RunDiscoveryRepositoryTests"/> (InMemory) and
/// <see cref="RunDiscoveryRepositoryPostgresTests"/> (real Postgres); this file only proves the
/// controller/middleware wiring on top of it.
/// </summary>
public sealed class RunDiscoveryApiTests : IClassFixture<RunDiscoveryApiTests.Factory>
{
    private readonly Factory _factory;
    public RunDiscoveryApiTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task List_FlagOff_Returns404IndistinguishableFromUnknownRoute()
    {
        // The base TestWebAppFactory never sets RUN_DISCOVERY_ENABLED (defaults false) -- a
        // deliberately different factory instance from every other test in this class, matching
        // O2's fail-closed default.
        using var off = new TestWebAppFactory();
        using var client = off.CreateInternalClient().WithTenant("demo-a").WithUser("admin-a").WithRole("ADMIN")
            .WithCapabilities("workflow.manage");

        var gated = await client.GetAsync("/api/runs");
        var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());

        // The existing per-run detail/approval routes on the same /api/runs prefix are untouched.
        var detail = await client.GetAsync($"/api/runs/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("找不到 Agent run", (await detail.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_MissingWorkflowManageCapability_IsForbidden()
    {
        // Deliberately not using the Client() helper, which always attaches workflow.manage --
        // this caller carries no capability header at all, ADMIN role included.
        using var client = _factory.CreateInternalClient().WithTenant("demo-a").WithUser("admin-a").WithRole("ADMIN");

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_MissingTenantHeader_IsBadRequest()
    {
        using var client = _factory.CreateInternalClient().WithUser("admin-a").WithRole("ADMIN")
            .WithCapabilities("workflow.manage");

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsOwnDirectAgentRun_ConsistentWithDetailEndpoint_AndScopedByTenantAndOwner()
    {
        var tenant = UniqueTenant();
        using var owner = Client(tenant, "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);
        var started = await Start(owner, agent);
        var runId = started["id"]!.GetValue<string>();

        var list = await owner.GetAsync("/api/runs");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var items = (await list.ReadJsonAsync())["items"]!.AsArray();
        var item = Assert.Single(items)!.AsObject();
        Assert.Equal(runId, item["id"]!.GetValue<string>());
        Assert.Equal("direct-agent", item["kind"]!.GetValue<string>());

        // Every listed run must be retrievable one at a time through the existing detail endpoint
        // for this exact caller.
        var detail = await owner.GetAsync($"/api/runs/{runId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        // Different owner, same tenant, same ADMIN role, same capability: still empty --
        // GET /api/runs/{id} would 404 for them too (owner-scoped), so the list must agree.
        using var otherOwner = Client(tenant, "admin-b", "ADMIN");
        Assert.Empty((await (await otherOwner.GetAsync("/api/runs")).ReadJsonAsync())["items"]!.AsArray());
        Assert.Equal(
            HttpStatusCode.NotFound, (await otherOwner.GetAsync($"/api/runs/{runId}")).StatusCode);

        // Cross-tenant: same owner id, different tenant.
        using var crossTenant = Client(UniqueTenant(), "admin-a", "ADMIN");
        Assert.Empty((await (await crossTenant.GetAsync("/api/runs")).ReadJsonAsync())["items"]!.AsArray());
    }

    [Fact]
    public async Task List_NonAdminCapabilityHolder_NeverSeesAgentRunSourcedItems()
    {
        var tenant = UniqueTenant();
        using var admin = Client(tenant, "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(admin);
        await Start(admin, agent);

        using var nonAdmin = Client(tenant, "admin-a", "USER");
        var items = (await (await nonAdmin.GetAsync("/api/runs")).ReadJsonAsync())["items"]!.AsArray();

        Assert.Empty(items);
    }

    [Fact]
    public async Task List_OwnerFilter_ForeignOwnerReturnsEmptyPageNotError()
    {
        var tenant = UniqueTenant();
        using var owner = Client(tenant, "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);
        await Start(owner, agent);

        var foreign = await owner.GetAsync("/api/runs?owner=someone-else");
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Empty((await foreign.ReadJsonAsync())["items"]!.AsArray());

        var own = await owner.GetAsync("/api/runs?owner=admin-a");
        Assert.Single((await own.ReadJsonAsync())["items"]!.AsArray());
    }

    /// <summary>O2 §3 DTO boundary: exactly the allowed field set, never prompt/tool argument
    /// values, checkpoint identity, effect identity, lease, or idempotency keys.</summary>
    [Fact]
    public async Task List_ItemShape_ExposesOnlyTheAllowedO2Fields()
    {
        var tenant = UniqueTenant();
        using var owner = Client(tenant, "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);
        await Start(owner, agent);

        var item = Assert.Single((await (await owner.GetAsync("/api/runs")).ReadJsonAsync())["items"]!.AsArray())!.AsObject();

        Assert.Equal(
            new[]
            {
                "agent_id", "agent_revision", "budget_summary", "cancel_requested", "child_progress",
                "completed_at", "created_at", "elapsed_seconds", "error_class", "id", "kind",
                "last_event_at", "last_event_type", "needs_recovery", "orchestrator_id",
                "orchestrator_revision", "orchestrator_root_run_id", "pending_approval", "started_at",
                "status", "task_id", "updated_at", "workflow_id", "workflow_revision",
            },
            item.Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task List_RejectsLimitOutOfRange(int limit)
    {
        using var client = Client("demo-a", "admin-a", "ADMIN");

        var response = await client.GetAsync($"/api/runs?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_RejectsUnknownKindAndStatus()
    {
        using var client = Client("demo-a", "admin-a", "ADMIN");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/runs?kind=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/runs?status=bogus")).StatusCode);
    }

    [Fact]
    public async Task List_RejectsMalformedCursor()
    {
        using var client = Client("demo-a", "admin-a", "ADMIN");

        var response = await client.GetAsync("/api/runs?cursor=not-base64!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The decision-table half: on-point vs off-point for the page-size boundary, plus proof the
    // keyset cursor actually advances (two pages never overlap).
    [Fact]
    public async Task List_Pagination_ReturnsHasMoreAndAdvancingCursor_WhenExceedingLimit()
    {
        var tenant = UniqueTenant();
        using var owner = Client(tenant, "admin-a", "ADMIN");
        var agent = await AgentRunApprovalApiTests.PublishedUserAgentAsync(owner);
        for (var i = 0; i < 3; i++)
        {
            await Start(owner, agent);
        }

        var firstPage = await owner.GetAsync("/api/runs?limit=2");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        var firstBody = await firstPage.ReadJsonAsync();
        Assert.Equal(2, firstBody["items"]!.AsArray().Count);
        Assert.True(firstBody["has_more"]!.GetValue<bool>());
        var cursor = firstBody["next_cursor"]!.GetValue<string>();

        var secondPage = await owner.GetAsync($"/api/runs?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        var secondBody = await secondPage.ReadJsonAsync();
        Assert.Single(secondBody["items"]!.AsArray());
        Assert.False(secondBody["has_more"]!.GetValue<bool>());
        Assert.Null(secondBody["next_cursor"]);

        var firstIds = firstBody["items"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToHashSet();
        var secondIds = secondBody["items"]!.AsArray().Select(x => x!["id"]!.GetValue<string>()).ToHashSet();
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    private HttpClient Client(string tenant, string user, string role)
        => _factory.CreateInternalClient().WithTenant(tenant).WithUser(user).WithRole(role)
            .WithCapabilities("workflow.manage");

    private static async Task<System.Text.Json.Nodes.JsonNode> Start(HttpClient owner, System.Text.Json.Nodes.JsonNode agent)
    {
        var agentId = agent["id"]!.GetValue<string>();
        using var start = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{agentId}/runs")
        {
            Content = JsonContent.Create(new { message = "hello" }),
        };
        start.Headers.TryAddWithoutValidation("Idempotency-Key", "start-" + Guid.NewGuid().ToString("N"));
        var response = await owner.SendAsync(start);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static string UniqueTenant() => "demo-a-" + Guid.NewGuid().ToString("N");

    public sealed class Factory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RUN_DISCOVERY_ENABLED", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrchestratorRunRepository>();
                services.AddSingleton<IOrchestratorRunRepository, InMemoryOrchestratorRunRepository>();
                services.RemoveAll<IRunDiscoveryRepository>();
                services.AddSingleton<IRunDiscoveryRepository, InMemoryRunDiscoveryRepository>();
            });
        }
    }
}
