using Backend.Api.Skills;
using Backend.Api.RuntimeDiscovery;

namespace Backend.Api.OperationsGovernance;

/// <summary>Lite-mode parity for the release ledger. All keys are tenant scoped and hashed.</summary>
public sealed class InMemoryOperationsGovernanceRepository(
    IRuntimeBindingRepository? bindings = null) : IOperationsGovernanceRepository
{
    // ApplyRolloutAsync awaits binding persistence while holding this gate, so a plain `lock` cannot be used.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TenantState> _states = new(StringComparer.Ordinal);
    // W2-02(e):時間戳只為儀表板時間窗而存在(Dapper 側用 observed_at/occurred_at 欄位裁切),
    // 不進任何回應 DTO。
    private readonly List<(string Tenant, OperationsTelemetry Value, DateTime At)> _telemetry = [];
    private readonly List<(string Tenant, RunEvidenceEnvelope Value)> _evidence = [];
    public IReadOnlyList<(string Tenant, OperationsTelemetry Value)> Telemetry
    {
        get { _gate.Wait(); try { return _telemetry.Select(x => (x.Tenant, x.Value)).ToArray(); } finally { _gate.Release(); } }
    }
    // Lite mode records evidence under the caller's tenant; Dapper additionally fences the run.
    public IReadOnlyList<(string Tenant, RunEvidenceEnvelope Value)> Evidence
    {
        get { _gate.Wait(); try { return _evidence.ToArray(); } finally { _gate.Release(); } }
    }

    public async Task<RegressionGate> RecordRegressionAsync(string tenantId, string suite, bool passed, string evidenceRef, string actorId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = State(tenantId); var item = new RegressionGate(Guid.NewGuid(), passed, suite, DateTime.UtcNow, false, state.Audit.Count + 1);
            state.Gate = item; state.Record("regression");
            return item with { AuditEntries = state.Audit.Count };
        }
        finally { _gate.Release(); }
    }

    public async Task<RegressionGate?> GetCurrentGateAsync(string tenantId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return State(tenantId).Gate is { } gate ? gate with { OverrideActive = State(tenantId).Overrides.Values.Any(x => x.RegressionId == gate.Id), AuditEntries = State(tenantId).Audit.Count } : null; }
        finally { _gate.Release(); }
    }

    public async Task<OverrideWriteResult> CreateOverrideAsync(string tenantId, Guid regressionId, string idempotencyKeyHash, string reason, string actorId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = State(tenantId);
            if (state.Overrides.TryGetValue(idempotencyKeyHash, out var prior))
                return prior.RegressionId == regressionId
                    && string.Equals(prior.Reason, reason, StringComparison.Ordinal)
                    ? new OverrideWriteResult(OverrideWriteStatus.Replay, Gate(state, prior.RegressionId))
                    : new OverrideWriteResult(OverrideWriteStatus.GateChanged, Gate(state, state.Gate?.Id));
            if (state.Gate is not { } gate || gate.Id != regressionId) return new OverrideWriteResult(OverrideWriteStatus.GateChanged, Gate(state, regressionId));
            if (gate.Passed) return new OverrideWriteResult(OverrideWriteStatus.NoLongerRequired, Gate(state, regressionId));
            state.Overrides[idempotencyKeyHash] = (regressionId, reason); state.Record("regression_override");
            return new OverrideWriteResult(OverrideWriteStatus.Accepted, Gate(state, regressionId));
        }
        finally { _gate.Release(); }
    }

    public async Task<RolloutWriteStatus> ApplyRolloutAsync(string tenantId, TenantRuntimeBinding binding, string actorId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = State(tenantId);
            if (binding.Enabled && state.Gate is { Passed: false } gate
                && !state.Overrides.Values.Any(x => x.RegressionId == gate.Id))
                return RolloutWriteStatus.RegressionBlocked;
            if (bindings is not null)
                await bindings.PutAsync(tenantId, binding, ct);
            state.Record(binding.Enabled ? "rollout" : "rollback");
            return RolloutWriteStatus.Applied;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordTelemetryAsync(string tenantId, OperationsTelemetry telemetry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (!_telemetry.Any(x => x.Tenant == tenantId && x.Value.RunId == telemetry.RunId && x.Value.EventId == telemetry.EventId)) _telemetry.Add((tenantId, telemetry, DateTime.UtcNow)); }
        finally { _gate.Release(); }
    }

    public async Task RecordEvidenceAsync(string tenantId, RunEvidenceEnvelope envelope, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { if (!_evidence.Any(x => x.Tenant == tenantId && x.Value.RunId == envelope.RunId && x.Value.EventId == envelope.EventId)) _evidence.Add((tenantId, envelope)); }
        finally { _gate.Release(); }
    }

    public async Task<EvidenceReconcileSummary> GetEvidenceReconcileAsync(string tenantId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var metricCount = _telemetry.Count(x => x.Tenant == tenantId);
            var envelope = _evidence.Where(x => x.Tenant == tenantId).Select(x => x.Value).ToArray();
            var mismatched = envelope.Count(e =>
            {
                var metric = _telemetry.FirstOrDefault(x => x.Tenant == tenantId && x.Value.RunId == e.RunId && x.Value.EventId == e.EventId).Value;
                return metric is not null && (metric.UsageUnits != e.UsageUnits || metric.CostUnits != e.CostUnits || metric.LatencyMs != e.LatencyMs);
            });
            var unknown = envelope.Count(e => e.ObservationQuality == "unknown");
            var measured = envelope.Where(e => e.ObservationQuality != "unknown" && e.UsageUnits is not null).Select(e => e.UsageUnits!.Value).ToArray();
            return new EvidenceReconcileSummary(metricCount, envelope.Length, mismatched, unknown, measured.Length == 0 ? null : measured.Sum());
        }
        finally { _gate.Release(); }
    }

    // W2-02(e):與 Dapper 同樣只彙總最近 windowDays 天的資料。release gate 本身刻意不加窗
    // (同 Dapper 註解:加窗會讓沒有近期迴歸結果的租戶 fail-open 成 RegressionPassed=true)。
    public async Task<OperationsMetrics> GetMetricsAsync(string tenantId, int windowDays, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = State(tenantId); var gate = Gate(state, state.Gate?.Id);
            var since = DateTime.UtcNow.AddDays(-windowDays);
            var values = _telemetry.Where(x => x.Tenant == tenantId && x.At >= since).Select(x => x.Value).ToArray();
            var agents = values.GroupBy(x => (x.AgentId ?? "unattributed", x.AgentRevision ?? 0), StringTupleComparer.Instance)
                .OrderBy(x => x.Key.Item1, StringComparer.Ordinal).ThenBy(x => x.Key.Item2)
                .Select(x => new AgentRevisionMetric(x.Key.Item1, x.Key.Item2, x.Select(v => v.RunId).Distinct().Count(), 0, 0, Average(x.Select(v => v.LatencyMs)), 0, SumNullable(x.Select(v => v.UsageUnits)), SumNullable(x.Select(v => v.CostUnits)), AverageNullable(x.Select(v => v.LatencyMs)))).ToArray();
            var skills = values.Where(x => !string.IsNullOrEmpty(x.SkillName) && x.SkillRevision is not null).GroupBy(x => (x.SkillName!, x.SkillRevision!.Value), StringTupleComparer.Instance)
                .OrderBy(x => x.Key.Item1, StringComparer.Ordinal).ThenBy(x => x.Key.Item2)
                .Select(x => new SkillRevisionMetric(x.Key.Item1, x.Key.Item2, x.Select(v => v.RunId).Distinct().Count(), AverageNullable(x.Select(v => v.LatencyMs)), SumNullable(x.Select(v => v.UsageUnits)), SumNullable(x.Select(v => v.CostUnits)), 0)).ToArray();
            var tools = values.Where(x => x.Kind == "tool" && !string.IsNullOrEmpty(x.ToolName)).GroupBy(x => x.ToolName!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ToolMetric(x.Key, x.Count(), AverageNullable(x.Select(v => v.LatencyMs)), SumNullable(x.Select(v => v.UsageUnits)), SumNullable(x.Select(v => v.CostUnits)), 0)).ToArray();
            var nodes = values.Where(x => !string.IsNullOrEmpty(x.NodeId)).GroupBy(x => x.NodeId!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new NodeMetric(x.Key, x.Count(), Average(x.Select(v => v.LatencyMs)), Max(x.Select(v => v.LatencyMs)))).ToArray();
            var audit = state.AuditWithin(windowDays).ToArray();
            return new OperationsMetrics(gate?.Passed ?? true, gate?.OverrideActive ?? false, audit.Length, audit.Count(x => x.Kind is "rollout" or "rollback"), 0, 0, 0, 0, 0, 0, agents, skills, tools, nodes, new RootAggregateMetric(0, 0, 0, 0));
        }
        finally { _gate.Release(); }
    }

    // Lite 模式沒有 per-revision 的 run 指標來源:revisions 恆為空,因此 selected/previous/delta 也恆為 null。
    public async Task<OperationsVersionComparison> GetVersionComparisonAsync(string tenantId, int? selectedRevision, int windowDays, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return new OperationsVersionComparison(
                selectedRevision, State(tenantId).AuditWithin(windowDays).Count(x => x.Kind is "rollout" or "rollback"),
                selectedRevision is not null, true, Array.Empty<RevisionMetric>(), null, windowDays);
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<LegacyInventoryItem>> GetLegacyInventoryAsync(string tenantId, CancellationToken ct)
        => Task.FromResult(LegacyInventory.Items);

    private TenantState State(string tenantId) => _states.TryGetValue(tenantId, out var state) ? state : _states[tenantId] = new TenantState();
    private static RegressionGate? Gate(TenantState state, Guid? id) => state.Gate is { } gate && gate.Id == id
        ? gate with { OverrideActive = state.Overrides.Values.Any(x => x.RegressionId == gate.Id), AuditEntries = state.Audit.Count } : null;
    private static long Average(IEnumerable<long?> values) { var items = values.Where(x => x is not null).Select(x => x!.Value).ToArray(); return items.Length == 0 ? 0 : (long)items.Average(); }
    private static long Max(IEnumerable<long?> values) { var items = values.Where(x => x is not null).Select(x => x!.Value).ToArray(); return items.Length == 0 ? 0 : items.Max(); }
    private static long? AverageNullable(IEnumerable<long?> values) { var items = values.Where(x => x is not null).Select(x => x!.Value).ToArray(); return items.Length == 0 ? null : (long)items.Average(); }
    private static long? SumNullable(IEnumerable<long?> values) { var items = values.Where(x => x is not null).Select(x => x!.Value).ToArray(); return items.Length == 0 ? null : items.Sum(); }
    private static decimal? SumNullable(IEnumerable<decimal?> values) { var items = values.Where(x => x is not null).Select(x => x!.Value).ToArray(); return items.Length == 0 ? null : items.Sum(); }
    private sealed class StringTupleComparer : IEqualityComparer<(string, int)>
    { public static StringTupleComparer Instance { get; } = new(); public bool Equals((string, int) x, (string, int) y) => StringComparer.Ordinal.Equals(x.Item1, y.Item1) && x.Item2 == y.Item2; public int GetHashCode((string, int) value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Item1), value.Item2); }
    private sealed class TenantState
    {
        public RegressionGate? Gate;
        public Dictionary<string, (Guid RegressionId, string Reason)> Overrides { get; } = new(StringComparer.Ordinal);
        // W2-02(e):稽核項帶時間戳,對齊 Dapper 的 operations_release_audit.occurred_at 裁切。
        public List<(string Kind, DateTime At)> Audit { get; } = [];
        public void Record(string kind) => Audit.Add((kind, DateTime.UtcNow));
        public IEnumerable<(string Kind, DateTime At)> AuditWithin(int windowDays)
            => Audit.Where(x => x.At >= DateTime.UtcNow.AddDays(-windowDays));
    }
}
