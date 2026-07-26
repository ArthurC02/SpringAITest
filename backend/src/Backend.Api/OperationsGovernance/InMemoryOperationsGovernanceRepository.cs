using Backend.Api.Skills;
using Backend.Api.RuntimeDiscovery;

namespace Backend.Api.OperationsGovernance;

/// <summary>Lite-mode parity for the release ledger. All keys are tenant scoped and hashed.</summary>
public sealed class InMemoryOperationsGovernanceRepository(
    IRuntimeBindingRepository? bindings = null) : IOperationsGovernanceRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TenantState> _states = new(StringComparer.Ordinal);
    private readonly List<(string Tenant, OperationsTelemetry Value)> _telemetry = [];
    public IReadOnlyList<(string Tenant, OperationsTelemetry Value)> Telemetry { get { lock (_gate) return _telemetry.ToArray(); } }

    public Task<RegressionGate> RecordRegressionAsync(string tenantId, string suite, bool passed, string evidenceRef, string actorId, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = State(tenantId); var item = new RegressionGate(Guid.NewGuid(), passed, suite, DateTime.UtcNow, false, state.Audit.Count + 1);
            state.Gate = item; state.Audit.Add("regression");
            return Task.FromResult(item with { AuditEntries = state.Audit.Count });
        }
    }

    public Task<RegressionGate?> GetCurrentGateAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(State(tenantId).Gate is { } gate ? gate with { OverrideActive = State(tenantId).Overrides.Values.Any(x => x.RegressionId == gate.Id), AuditEntries = State(tenantId).Audit.Count } : null);
    }

    public Task<OverrideWriteResult> CreateOverrideAsync(string tenantId, Guid regressionId, string idempotencyKeyHash, string reason, string actorId, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = State(tenantId);
            if (state.Overrides.TryGetValue(idempotencyKeyHash, out var prior))
                return Task.FromResult(prior.RegressionId == regressionId
                    && string.Equals(prior.Reason, reason, StringComparison.Ordinal)
                    ? new OverrideWriteResult(OverrideWriteStatus.Replay, Gate(state, prior.RegressionId))
                    : new OverrideWriteResult(OverrideWriteStatus.GateChanged, Gate(state, state.Gate?.Id)));
            if (state.Gate is not { } gate || gate.Id != regressionId) return Task.FromResult(new OverrideWriteResult(OverrideWriteStatus.GateChanged, Gate(state, regressionId)));
            if (gate.Passed) return Task.FromResult(new OverrideWriteResult(OverrideWriteStatus.NoLongerRequired, Gate(state, regressionId)));
            state.Overrides[idempotencyKeyHash] = (regressionId, reason); state.Audit.Add("regression_override");
            return Task.FromResult(new OverrideWriteResult(OverrideWriteStatus.Accepted, Gate(state, regressionId)));
        }
    }

    public Task<RolloutWriteStatus> ApplyRolloutAsync(string tenantId, TenantRuntimeBinding binding, string actorId, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = State(tenantId);
            if (binding.Enabled && state.Gate is { Passed: false } gate
                && !state.Overrides.Values.Any(x => x.RegressionId == gate.Id))
                return Task.FromResult(RolloutWriteStatus.RegressionBlocked);
            if (bindings is not null)
                bindings.PutAsync(tenantId, binding, ct).GetAwaiter().GetResult();
            state.Audit.Add(binding.Enabled ? "rollout" : "rollback");
            return Task.FromResult(RolloutWriteStatus.Applied);
        }
    }

    public Task RecordTelemetryAsync(string tenantId, OperationsTelemetry telemetry, CancellationToken ct)
    { lock (_gate) { if (!_telemetry.Any(x => x.Tenant == tenantId && x.Value.RunId == telemetry.RunId && x.Value.EventId == telemetry.EventId)) _telemetry.Add((tenantId, telemetry)); return Task.CompletedTask; } }

    public Task<OperationsMetrics> GetMetricsAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = State(tenantId); var gate = Gate(state, state.Gate?.Id);
            var values = _telemetry.Where(x => x.Tenant == tenantId).Select(x => x.Value).ToArray();
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
            return Task.FromResult(new OperationsMetrics(gate?.Passed ?? true, gate?.OverrideActive ?? false, state.Audit.Count, state.Audit.Count(x => x is "rollout" or "rollback"), 0, 0, 0, 0, 0, 0, agents, skills, tools, nodes, new RootAggregateMetric(0, 0, 0, 0)));
        }
    }

    // Lite 模式沒有 per-revision 的 run 指標來源:revisions 恆為空,因此 selected/previous/delta 也恆為 null。
    public Task<OperationsVersionComparison> GetVersionComparisonAsync(string tenantId, int? selectedRevision, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(new OperationsVersionComparison(
                selectedRevision, State(tenantId).Audit.Count(x => x is "rollout" or "rollback"),
                selectedRevision is not null, true, Array.Empty<RevisionMetric>(), null));
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
    private sealed class TenantState { public RegressionGate? Gate; public Dictionary<string, (Guid RegressionId, string Reason)> Overrides { get; } = new(StringComparer.Ordinal); public List<string> Audit { get; } = []; }
}
