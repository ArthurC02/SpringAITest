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
public sealed class RunEvidenceEnvelopeApiTests :
    IClassFixture<TestWebAppFactory>, IClassFixture<RunEvidenceEnvelopeApiTests.RunEvidenceEnabledFactory>
{
    private readonly TestWebAppFactory _factory;
    // Shared: every test below asserts against store.Telemetry/store.Evidence with an
    // `x.Tenant == "..."` predicate, and every test uses its own unique "e1-*" tenant, so a shared
    // InMemoryOperationsGovernanceRepository never lets one test's rows answer another's assertion.
    private readonly RunEvidenceEnabledFactory _enabledFactory;

    public RunEvidenceEnvelopeApiTests(TestWebAppFactory factory, RunEvidenceEnabledFactory enabledFactory)
    {
        _factory = factory;
        _enabledFactory = enabledFactory;
    }

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
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-malformed").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 3, evidence = new { outcome = "not-a-real-outcome" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_enabledFactory.Fake<IOperationsGovernanceRepository>());
        Assert.DoesNotContain(store.Telemetry, x => x.Tenant == "e1-malformed");
    }

    [Theory]
    [InlineData("System.Net.Http.HttpRequestException", HttpStatusCode.OK)] // representative valid token
    [InlineData("bad class", HttpStatusCode.BadRequest)] // charset alone: a space is outside [A-Za-z0-9_.:+-], no control character involved
    [InlineData("System.Net.Http.HttpRequestException\n", HttpStatusCode.BadRequest)] // trailing newline: proves \A...\z (not ^...$) is enforced
    public async Task FlagOn_ErrorClassShape_AcceptsTokenForm_RejectsNonTokenAndControlCharacters(string errorClass, HttpStatusCode expected)
    {
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-error-class").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 1, evidence = new { outcome = "failure", error_class = errorClass },
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory] // ErrorClassPattern is {1,200}: both ends of the length class, not just the charset
    [InlineData(0, HttpStatusCode.BadRequest)] // empty string is below the 1-char minimum
    [InlineData(200, HttpStatusCode.OK)] // on-point
    [InlineData(201, HttpStatusCode.BadRequest)] // off-point
    public async Task FlagOn_ErrorClassLength_OnOffPointOfTheTwoHundredCharCap(int length, HttpStatusCode expected)
    {
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-error-class-length").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 1, evidence = new { outcome = "failure", error_class = new string('a', length) },
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task FlagOn_InvalidObservationQuality_Returns400_WithItsOwnMessage()
    {
        // The message proves *which* branch rejected: observation_quality has its own enum check,
        // separate from outcome's -- both are only ever 400 to the caller.
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-quality-invalid").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 2, evidence = new { outcome = "success", observation_quality = "bogus" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid evidence observation_quality", (await response.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Theory] // every other evidence string goes through Safe(value, max) -- the caps are per field, not global
    [InlineData("role_view", 128, "", HttpStatusCode.OK)] // on-point of its 128 cap
    [InlineData("role_view", 129, "", HttpStatusCode.BadRequest)] // off-point
    [InlineData("verifier_verdict", 64, "", HttpStatusCode.OK)] // its own cap is 64...
    [InlineData("verifier_verdict", 65, "", HttpStatusCode.BadRequest)] // ...not the 128 used by its neighbours
    [InlineData("model_fingerprint", 8, "\n", HttpStatusCode.BadRequest)] // control character, far inside the 256 cap
    public async Task FlagOn_EvidenceStringFields_EnforceTheirOwnLengthAndControlCharacterLimits(
        string field, int length, string suffix, HttpStatusCode expected)
    {
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-evidence-strings").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 1,
            evidence = new Dictionary<string, object?> { ["outcome"] = "success", [field] = new string('x', length) + suffix },
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory] // the four evidence revisions share one bound: [1, 1_000_000]
    [InlineData("orchestrator_revision", 0, HttpStatusCode.BadRequest)]
    [InlineData("orchestrator_revision", 1, HttpStatusCode.OK)]
    [InlineData("context_revision", 1_000_000, HttpStatusCode.OK)]
    [InlineData("policy_revision", 1_000_001, HttpStatusCode.BadRequest)]
    [InlineData("tool_revision", 0, HttpStatusCode.BadRequest)]
    public async Task FlagOn_EvidenceRevisionBounds_OnOffPointOfOneToOneMillion(string field, int revision, HttpStatusCode expected)
    {
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-revision-bounds").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 1,
            evidence = new Dictionary<string, object?> { ["outcome"] = "success", [field] = revision },
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task FlagOn_EvidenceRevisionFields_AreCarriedIntoTheEnvelope()
    {
        // Distinct values on purpose: a crossed mapping in BuildEnvelope cannot pass this.
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-revisions").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 1,
            evidence = new
            {
                outcome = "success", orchestrator_revision = 5, context_revision = 6,
                policy_revision = 7, tool_revision = 8,
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_enabledFactory.Fake<IOperationsGovernanceRepository>());
        var envelope = Assert.Single(store.Evidence, x => x.Tenant == "e1-revisions").Value;
        Assert.Equal(5, envelope.OrchestratorRevision);
        Assert.Equal(6, envelope.ContextRevision);
        Assert.Equal(7, envelope.PolicyRevision);
        Assert.Equal(8, envelope.ToolRevision);
    }

    [Fact]
    public async Task FlagOn_EvidenceObjectOmitted_StillWritesAnEnvelope_WithUnknownOutcome()
    {
        // The whole nested object is optional: SafeEvidence(null) passes and BuildEnvelope fills in
        // the defaults -- outcome "unknown", quality "measured" because usage *was* observed.
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-no-evidence").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 4,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_enabledFactory.Fake<IOperationsGovernanceRepository>());
        var envelope = Assert.Single(store.Evidence, x => x.Tenant == "e1-no-evidence").Value;
        Assert.Equal("unknown", envelope.Outcome);
        Assert.Equal("measured", envelope.ObservationQuality);
    }

    [Fact]
    public async Task FlagOn_NothingObserved_OverridesTheCallerClaimedQualityWithUnknown()
    {
        // usage/cost/latency all absent: the caller may claim "measured", the envelope still says
        // unknown -- and the aggregate must not zero-fill it into the measured sum.
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-quality").WithUser("workflow").WithRole("SYSTEM");
        var response = await client.PostAsJsonAsync("/api/operations/telemetry", new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            evidence = new { outcome = "success", observation_quality = "measured" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_enabledFactory.Fake<IOperationsGovernanceRepository>());
        Assert.Equal("unknown", Assert.Single(store.Evidence, x => x.Tenant == "e1-quality").Value.ObservationQuality);

        var reconcile = await store.GetEvidenceReconcileAsync("e1-quality", default);
        Assert.Equal(1, reconcile.UnknownObservationCount);
        Assert.Null(reconcile.MeasuredUsageUnitsSum);
    }

    [Fact]
    public async Task FlagOn_DuplicateEventIdReplay_DoesNotDoubleWriteTheEnvelope()
    {
        using var client = _enabledFactory.CreateInternalClient().WithTenant("e1-dup").WithUser("workflow").WithRole("SYSTEM");
        var body = new
        {
            run_id = Guid.NewGuid(), event_id = Guid.NewGuid(), kind = "model", node_id = "model_step",
            usage_units = 5, cost_units = 0.1m, latency_ms = 20,
            evidence = new { outcome = "success", observation_quality = "measured" },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operations/telemetry", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/operations/telemetry", body)).StatusCode);

        var store = Assert.IsType<InMemoryOperationsGovernanceRepository>(_enabledFactory.Fake<IOperationsGovernanceRepository>());
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

    public sealed class RunEvidenceEnabledFactory : TestWebAppFactory
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
