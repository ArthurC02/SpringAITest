using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Backend.Api.OperationsGovernance;

namespace Backend.Api.Tests;

public sealed class OperationsGovernanceApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;
    public OperationsGovernanceApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task InternalTelemetry_IsTenantFencedAndIdempotentlyRecorded()
    {
        using var client = _factory.CreateInternalClient().WithTenant("ops-meter").WithUser("workflow").WithRole("SYSTEM");
        var run = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var first = await client.PostAsJsonAsync("/api/operations/telemetry", new { run_id = run, event_id = eventId, kind = "model", node_id = "model_step", usage_units = 42, latency_ms = 9 });
        var replay = await client.PostAsJsonAsync("/api/operations/telemetry", new { run_id = run, event_id = eventId, kind = "model", node_id = "model_step", usage_units = 42, latency_ms = 9 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_factory.Fake<IOperationsGovernanceRepository>());
        var item = Assert.Single(store.Telemetry);
        Assert.Equal("ops-meter", item.Tenant); Assert.Equal(42, item.Value.UsageUnits); Assert.Equal(9, item.Value.LatencyMs);
    }

    [Fact]
    public async Task InMemoryTelemetry_AggregatesAgentSkillToolNodeAndRevisionWithoutCrossTenantLeakage()
    {
        var store = new InMemoryOperationsGovernanceRepository();
        var run = Guid.NewGuid();
        await store.RecordTelemetryAsync("metric-a", new(run, Guid.NewGuid(), "model", "model_step", null, "skill-a", 3, "agent-a", 7, 12, 1.25m, 20), default);
        await store.RecordTelemetryAsync("metric-a", new(run, Guid.NewGuid(), "tool", "invoke_tool", "local.calculator", "skill-a", 3, "agent-a", 7, null, null, 10), default);
        await store.RecordTelemetryAsync("metric-b", new(Guid.NewGuid(), Guid.NewGuid(), "model", "other", null, "other", 1, "other", 1, 99, 9m, 99), default);

        var metrics = await store.GetMetricsAsync("metric-a", default);
        var agent = Assert.Single(metrics.Agents);
        Assert.Equal(("agent-a", 7, 1), (agent.AgentId, agent.Revision, agent.Runs));
        Assert.Equal(12, agent.ObservedUsageUnits); Assert.Equal(1.25m, agent.ObservedCostUnits);
        var skill = Assert.Single(metrics.Skills);
        Assert.Equal(("skill-a", 3, 12L), (skill.Name, skill.Revision, skill.ObservedUsageUnits));
        var tool = Assert.Single(metrics.Tools);
        Assert.Equal("local.calculator", tool.Kind); Assert.Equal(10, tool.ObservedLatencyMs);
        Assert.Equal(2, metrics.Nodes.Count);

        var comparison = await store.GetVersionComparisonAsync("metric-a", 7, default);
        // Telemetry carries Agent revision, never Orchestrator revision.
        // Lite mode has no durable Root ledger, so it must not fabricate an
        // Orchestrator version series from these events.
        Assert.Empty(comparison.Revisions);
        Assert.Null(comparison.SelectedVsPrevious);
    }

    [Fact]
    public async Task FeatureOff_HidesOperationsBeforeInternalAuthentication()
    {
        using var factory = new DisabledFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/api/admin/operations/metrics")).StatusCode);
    }

    [Fact]
    public async Task FailedRegression_BlocksRollout_UntilDurableAuditedOverride_AndIsTenantScoped()
    {
        using var admin = Client("ops-a", "operator-a", manage: true);
        var failed = await admin.PostAsJsonAsync("/api/admin/operations/regressions", new { suite = "d7-release", passed = false, evidence_ref = "evidence/d7-a" });
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

        var blocked = await admin.PutAsJsonAsync("/api/admin/operations/rollout", new { enabled = true, orchestrator_id = Guid.NewGuid(), revision = 2, canary_user_ids = new[] { "user-a" } });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        using var missingKey = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "documented break-glass rollout" }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(missingKey)).StatusCode);

        using var overrideRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "documented break-glass rollout" }) };
        overrideRequest.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(overrideRequest)).StatusCode);
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "documented break-glass rollout" }) };
        replay.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(replay)).StatusCode);
        using var payloadConflict = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = JsonContent.Create(new { reason = "different text must not reuse an accepted decision" }) };
        payloadConflict.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(payloadConflict)).StatusCode);

        var enabled = await admin.PutAsJsonAsync("/api/admin/operations/rollout", new { enabled = true, orchestrator_id = Guid.NewGuid(), revision = 2, canary_user_ids = new[] { "user-a" } });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var body = await enabled.ReadJsonAsync();
        Assert.True(body["new_roots_only"]!.GetValue<bool>());

        var metrics = await admin.GetAsync("/api/admin/operations/metrics");
        var metricText = await metrics.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.DoesNotContain("evidence/d7-a", metricText, StringComparison.Ordinal);
        Assert.DoesNotContain("break-glass", metricText, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            "/api/admin/operations/regressions",
            new { suite = "d7-release-2", passed = false, evidence_ref = "evidence/d7-b" })).StatusCode);
        using var staleKey = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides")
        {
            Content = JsonContent.Create(new { reason = "documented break-glass rollout" }),
        };
        staleKey.Headers.Add("Idempotency-Key", "ops-override-a");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.SendAsync(staleKey)).StatusCode);

        using var otherTenant = Client("ops-b", "operator-b", manage: true);
        var otherMetrics = await (await otherTenant.GetAsync("/api/admin/operations/metrics")).ReadJsonAsync();
        Assert.True(otherMetrics["release_gate"]!["regression_passed"]!.GetValue<bool>());
        Assert.False(otherMetrics["release_gate"]!["override_active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task VersionAndInventory_AreCapabilityProtected_AndRedacted()
    {
        using var denied = Client("ops-c", "ordinary", manage: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/admin/operations/legacy-inventory")).StatusCode);

        using var admin = Client("ops-c", "operator", manage: true);
        var compare = await (await admin.GetAsync("/api/admin/operations/version-comparison")).ReadJsonAsync();
        Assert.False(compare["new_roots_only"]!.GetValue<bool>());
        Assert.True(compare["active_runs_keep_immutable_snapshot"]!.GetValue<bool>());
        var inventory = await admin.GetAsync("/api/admin/operations/legacy-inventory");
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
        Assert.DoesNotContain("token", await inventory.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Client(string tenant, string user, bool manage)
    {
        var client = _factory.CreateInternalClient().WithTenant(tenant).WithUser(user).WithRole("SYSTEM_ADMIN");
        if (manage) client.DefaultRequestHeaders.Add("X-User-Capabilities", "workflow.manage");
        return client;
    }

    private sealed class DisabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AGENT_WRITE_TOOLS_ENABLED", "false");
        }
    }
}
