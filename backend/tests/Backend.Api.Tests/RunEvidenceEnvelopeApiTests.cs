using System.Net;
using System.Net.Http.Json;
using Backend.Api.OperationsGovernance;
using Backend.Api.RuntimeDiscovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// E1 run evidence envelope: flag-off no-op, dual-write idempotency, ingest-failure isolation and
/// unknown-usage aggregation. All against the InMemory/lite repository (no live DB required);
/// tenant/run/snapshot fail-closed and the reconcile endpoint need a real <c>agent_run</c> row and
/// are covered by <see cref="RunEvidenceEnvelopePostgresApiTests"/>.
/// </summary>
public sealed class RunEvidenceEnvelopeApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;
    public RunEvidenceEnvelopeApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task FlagOff_WritesNoEnvelope_AndLeavesLegacyMetricPathUnchanged()
    {
        // RUN_EVIDENCE_ENABLED is unset on the shared factory (defaults false). A deliberately
        // invalid nested "evidence" object proves it is never even looked at while off.
        using var client = _factory.CreateInternalClient().WithTenant("e1-off").WithUser("workflow").WithRole("SYSTEM");
        var run = Guid.NewGuid();
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = run, event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 7, evidence = new { outcome = "not-a-real-outcome" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_factory.Fake<IOperationsGovernanceRepository>());
        Assert.Single(store.Telemetry, x => x.Tenant == "e1-off");
        Assert.DoesNotContain(store.Evidence, x => x.Tenant == "e1-off");
    }

    [Fact]
    public async Task FlagOn_MalformedEvidence_Returns400_AndNeverWritesTheLegacyMetric()
    {
        // Evidence shape is validated *before* the legacy metric write: a 400 here must mean
        // nothing at all was written -- not "metric landed, envelope rejected".
        using var factory = new RunEvidenceEnabledFactory();
        using var client = factory.CreateInternalClient().WithTenant("e1-malformed").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 3, evidence = new { outcome = "not-a-real-outcome" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(factory.Fake<IOperationsGovernanceRepository>());
        Assert.DoesNotContain(store.Telemetry, x => x.Tenant == "e1-malformed");
    }

    [Theory]
    [InlineData("System.Net.Http.HttpRequestException")] // representative valid token
    [InlineData("bad class\n")] // control character
    [InlineData("System.Net.Http.HttpRequestException\n")] // trailing newline: proves \A...\z (not ^...$) is enforced
    public async Task FlagOn_ErrorClassShape_RejectsControlCharacters_AcceptsTokenForm(string errorClass)
    {
        using var factory = new RunEvidenceEnabledFactory();
        using var client = factory.CreateInternalClient().WithTenant("e1-error-class").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 1, evidence = new { outcome = "failure", error_class = errorClass },
        });

        var expected = errorClass.Contains('\n') ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task FlagOn_DuplicateEventIdReplay_DoesNotDoubleWriteTheEnvelope()
    {
        using var factory = new RunEvidenceEnabledFactory();
        using var client = factory.CreateInternalClient().WithTenant("e1-dup").WithUser("workflow").WithRole("SYSTEM");
        var body = new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 5, cost_units = 0.1m, latency_ms = 20,
            evidence = new { outcome = "success", observation_quality = "measured" },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operations/telemetry", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operations/telemetry", body)).StatusCode);

        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(factory.Fake<IOperationsGovernanceRepository>());
        Assert.Single(store.Evidence, x => x.Tenant == "e1-dup");
        var reconcile = await store.GetEvidenceReconcileAsync("e1-dup", default);
        Assert.Equal(1, reconcile.EnvelopeEventCount);
        Assert.Equal(1, reconcile.MetricEventCount);
        Assert.Equal(0, reconcile.MismatchedEventCount);
        Assert.Equal(5, reconcile.MeasuredUsageUnitsSum);
    }

    [Fact]
    public async Task IngestFailure_IsSwallowed_AndDoesNotAffectTheAcceptedResponseOrLegacyMetric()
    {
        using var factory = new ThrowingEvidenceFactory();
        using var client = factory.CreateInternalClient().WithTenant("e1-ingest-fail").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 3, evidence = new { outcome = "success" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.ReadJsonAsync())["accepted"]!.GetValue<bool>());

        var store = Assert.IsType<ThrowingEvidenceRepository>(factory.Fake<IOperationsGovernanceRepository>());
        Assert.Single(store.Telemetry, x => x.Tenant == "e1-ingest-fail");
    }

    [Fact]
    public async Task MissingUsage_IsRecordedUnknown_AndExcludedFromTheMeasuredAggregate()
    {
        var store = new InMemoryOperationsGovernanceRepository();
        var run = Guid.NewGuid();
        await store.RecordEvidenceAsync(
            "e1-unknown",
            new(RunId: run, EventId: Guid.NewGuid(), Kind: "model", Outcome: "success", ErrorClass: null, SnapshotSha256: null,
                AgentId: "agent-a", AgentRevision: 1, OrchestratorRevision: null, SkillName: null, SkillRevision: null,
                PromptManifestSha256: null, ContextRevision: null, RoleView: null, PolicyRevision: null,
                ModelProvider: null, ModelDeployment: null, ModelId: null, ModelFingerprint: null, ModelSettingsHash: null,
                ToolName: null, ToolRevision: null, NodeId: "model_step", TraceId: null, SpanId: null,
                UsageUnits: null, CostUnits: null, LatencyMs: null, ObservationQuality: "unknown",
                VerifierVerdict: null, CaseVerdict: null, RedactionNote: null),
            default);
        await store.RecordEvidenceAsync(
            "e1-unknown",
            new(RunId: run, EventId: Guid.NewGuid(), Kind: "model", Outcome: "success", ErrorClass: null, SnapshotSha256: null,
                AgentId: "agent-a", AgentRevision: 1, OrchestratorRevision: null, SkillName: null, SkillRevision: null,
                PromptManifestSha256: null, ContextRevision: null, RoleView: null, PolicyRevision: null,
                ModelProvider: null, ModelDeployment: null, ModelId: null, ModelFingerprint: null, ModelSettingsHash: null,
                ToolName: null, ToolRevision: null, NodeId: "model_step", TraceId: null, SpanId: null,
                UsageUnits: 10, CostUnits: null, LatencyMs: null, ObservationQuality: "measured",
                VerifierVerdict: null, CaseVerdict: null, RedactionNote: null),
            default);

        var reconcile = await store.GetEvidenceReconcileAsync("e1-unknown", default);
        Assert.Equal(2, reconcile.EnvelopeEventCount);
        Assert.Equal(1, reconcile.UnknownObservationCount);
        // The unknown-usage row must not be zero-filled into the sum -- only the measured 10 counts.
        Assert.Equal(10, reconcile.MeasuredUsageUnitsSum);
    }

    private sealed class RunEvidenceEnabledFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RUN_EVIDENCE_ENABLED", "true");
        }
    }

    private sealed class ThrowingEvidenceFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RUN_EVIDENCE_ENABLED", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOperationsGovernanceRepository>();
                services.AddSingleton<IOperationsGovernanceRepository, ThrowingEvidenceRepository>();
            });
        }
    }

    /// <summary>Hand-written fake: legacy telemetry recording behaves exactly like InMemory, but
    /// the extended envelope sink always throws, simulating a transient ingest outage.</summary>
    private sealed class ThrowingEvidenceRepository : IOperationsGovernanceRepository
    {
        private readonly InMemoryOperationsGovernanceRepository _inner = new();
        public IReadOnlyList<(string Tenant, OperationsTelemetry Value)> Telemetry => _inner.Telemetry;
        public Task<RegressionGate> RecordRegressionAsync(string tenantId, string suite, bool passed, string evidenceRef, string actorId, CancellationToken ct)
            => _inner.RecordRegressionAsync(tenantId, suite, passed, evidenceRef, actorId, ct);
        public Task<RegressionGate?> GetCurrentGateAsync(string tenantId, CancellationToken ct) => _inner.GetCurrentGateAsync(tenantId, ct);
        public Task<OverrideWriteResult> CreateOverrideAsync(string tenantId, Guid regressionId, string idempotencyKeyHash, string reason, string actorId, CancellationToken ct)
            => _inner.CreateOverrideAsync(tenantId, regressionId, idempotencyKeyHash, reason, actorId, ct);
        public Task<RolloutWriteStatus> ApplyRolloutAsync(string tenantId, TenantRuntimeBinding binding, string actorId, CancellationToken ct)
            => _inner.ApplyRolloutAsync(tenantId, binding, actorId, ct);
        public Task RecordTelemetryAsync(string tenantId, OperationsTelemetry telemetry, CancellationToken ct) => _inner.RecordTelemetryAsync(tenantId, telemetry, ct);
        public Task<OperationsMetrics> GetMetricsAsync(string tenantId, CancellationToken ct) => _inner.GetMetricsAsync(tenantId, ct);
        public Task<OperationsVersionComparison> GetVersionComparisonAsync(string tenantId, int? selectedRevision, CancellationToken ct)
            => _inner.GetVersionComparisonAsync(tenantId, selectedRevision, ct);
        public Task<IReadOnlyList<LegacyInventoryItem>> GetLegacyInventoryAsync(string tenantId, CancellationToken ct) => _inner.GetLegacyInventoryAsync(tenantId, ct);
        public Task RecordEvidenceAsync(string tenantId, RunEvidenceEnvelope envelope, CancellationToken ct) => throw new InvalidOperationException("simulated evidence sink outage");
        public Task<EvidenceReconcileSummary> GetEvidenceReconcileAsync(string tenantId, CancellationToken ct) => _inner.GetEvidenceReconcileAsync(tenantId, ct);
    }
}
