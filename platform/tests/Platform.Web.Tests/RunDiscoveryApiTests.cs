using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Web.Tests;

/// <summary>
/// O2 "Unified Runs and Tasks center" (04-operations-trigger-plan.md §3): GET /api/runs.
/// Backend owns every filter/keyset cursor/tenant-owner-ADMIN visibility rule (see
/// RunDiscoveryApiTests in Backend.Api.Tests); this file only proves Platform's own
/// gate/capability/proxy wiring on top of it.
/// </summary>
[Collection("EngineCalls")]
public sealed class RunDiscoveryApiTests : IClassFixture<RunDiscoveryApiTests.EnabledFixture>
{
    private readonly EnabledFixture _factory;
    public RunDiscoveryApiTests(EnabledFixture factory) => _factory = factory;

    [Fact]
    public async Task List_FlagOff_Returns404BeforeAuthentication()
    {
        using var factory = new TestWebAppFactory(new() { ["RUN_DISCOVERY_ENABLED"] = "false" });
        var before = FakeRunDiscoveryService.Calls.Count;

        var response = await factory.CreateClient().GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, FakeRunDiscoveryService.Calls.Count);
    }

    // 決策表的另一半:flag off 的 404 body 必須與一般找不到路由的 404 完全一致(不可分辨)。
    [Fact]
    public async Task List_FlagOff_UsesSameNotFoundContractAsUnknownRoute()
    {
        using var factory = new TestWebAppFactory(new() { ["RUN_DISCOVERY_ENABLED"] = "false" });
        using var client = factory.CreateClient();

        var gated = await client.GetAsync("/api/runs");
        var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        unknownBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());
    }

    // The existing per-run detail/approval routes on the same /api/runs prefix are untouched by
    // this flag -- both stay governed solely by their own existing gates.
    [Fact]
    public async Task List_FlagOff_DoesNotHideExistingRunRoutes()
    {
        using var factory = new TestWebAppFactory(new()
        {
            ["RUN_DISCOVERY_ENABLED"] = "false",
            ["AGENT_BUILDER_ENABLED"] = "true",
            ["AGENT_TEST_RUN_ENABLED"] = "true",
        });
        var client = factory.CreateClient().WithToken(factory.IssueToken("admin-a", "ADMIN"));

        var detail = await client.GetAsync($"/api/runs/{FakeAgentRunService.RunIdText}");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task List_MissingWorkflowManageCapability_IsForbidden()
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken("user-a", "ADMIN", capabilities: null));

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_EmptyCapabilitySet_IsForbidden()
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken("user-a", "ADMIN", capabilities: Array.Empty<string>()));

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // workflow.manage 是精確比對:ADMIN 角色本身不隱含它,近似字串也不行 —— 與 D4/D5 既有 policy 同一慣例。
    [Theory]
    [InlineData("workflow.manage.all")]
    [InlineData("WORKFLOW.MANAGE")]
    public async Task List_ApproximateCapability_IsForbidden(string capability)
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken("user-a", "ADMIN", capabilities: new[] { capability }));

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_Unauthenticated_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ADMIN doesn't imply workflow.manage: a non-ADMIN USER holding the exact capability must
    // still succeed -- this endpoint's gate is the capability, never the role.
    [Fact]
    public async Task List_NonAdminWithExactCapability_Succeeds()
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken("user-a", "USER", capabilities: new[] { "workflow.manage" }));

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // O2 has ten independent filter dimensions; Platform's whole job is forwarding the query
    // string and identity verbatim -- Backend owns every filter and the cursor codec.
    [Fact]
    public async Task List_ForwardsQueryStringVerbatim()
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken("op-a", "ADMIN", capabilities: new[] { "workflow.manage" }));

        var response = await client.GetAsync(
            "/api/runs?kind=direct-agent&status=running&agent_id=11111111-1111-1111-1111-111111111111&limit=7&cursor=abc123");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "list:?kind=direct-agent&status=running&agent_id=11111111-1111-1111-1111-111111111111&limit=7&cursor=abc123:op-a",
            FakeRunDiscoveryService.Calls);
    }

    // Off-point: no query string at all -- Platform must not invent or default any parameter.
    [Fact]
    public async Task List_WithoutQueryString_ForwardsEmptyString()
    {
        var client = _factory.CreateClient()
            .WithToken(_factory.IssueToken("op-a", "ADMIN", capabilities: new[] { "workflow.manage" }));

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("list::op-a", FakeRunDiscoveryService.Calls);
    }

    // Backend owns the visibility/filter decision; Platform must pass its status/body through
    // verbatim, never rewrite it into a locally-derived error.
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    public async Task List_DownstreamRejection_PassesThroughVerbatim(int status)
    {
        using var factory = new RejectingFactory(status);
        var client = factory.CreateClient()
            .WithToken(factory.IssueToken("op-a", "ADMIN", capabilities: new[] { "workflow.manage" }));

        var response = await client.GetAsync("/api/runs");

        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(status, body["status"]!.GetValue<int>());
        Assert.Equal("下游訊息", body["message"]!.GetValue<string>());
        Assert.Equal("backend_code", body["code"]!.GetValue<string>());
        Assert.Equal("backend-trace-9", body["correlationId"]!.GetValue<string>());
    }

    private sealed class RejectingRunDiscoveryService(int status) : IRunDiscoveryService
    {
        public Task<AgentProxyResponse> ListAsync(string queryString, UserContext ctx, CancellationToken ct = default)
            => Task.FromResult(new AgentProxyResponse(
                status,
                $"{{\"timestamp\":\"2026-07-25T00:00:00Z\",\"status\":{status},\"code\":\"backend_code\",\"message\":\"下游訊息\",\"correlationId\":\"backend-trace-9\",\"fieldErrors\":{{}}}}",
                null));
    }

    private sealed class RejectingFactory(int status)
        : TestWebAppFactory(new() { ["RUN_DISCOVERY_ENABLED"] = "true" })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRunDiscoveryService>();
                services.AddScoped<IRunDiscoveryService>(_ => new RejectingRunDiscoveryService(status));
            });
        }
    }

    public sealed class EnabledFixture : TestWebAppFactory
    {
        public EnabledFixture() : base(new() { ["RUN_DISCOVERY_ENABLED"] = "true" })
        {
        }
    }
}
