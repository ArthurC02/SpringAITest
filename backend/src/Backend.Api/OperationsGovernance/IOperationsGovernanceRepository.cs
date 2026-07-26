using System.Text.Json.Serialization;
using Backend.Api.RuntimeDiscovery;

namespace Backend.Api.OperationsGovernance;

/// <summary>
/// Durable, tenant-scoped release-control state.  This is deliberately separate from the
/// runtime binding: a binding selects future roots; this ledger explains why it was allowed.
/// </summary>
public interface IOperationsGovernanceRepository
{
    Task<RegressionGate> RecordRegressionAsync(string tenantId, string suite, bool passed, string evidenceRef, string actorId, CancellationToken ct);
    Task<RegressionGate?> GetCurrentGateAsync(string tenantId, CancellationToken ct);
    Task<OverrideWriteResult> CreateOverrideAsync(string tenantId, Guid regressionId, string idempotencyKeyHash, string reason, string actorId, CancellationToken ct);
    Task<RolloutWriteStatus> ApplyRolloutAsync(string tenantId, TenantRuntimeBinding binding, string actorId, CancellationToken ct);
    Task RecordTelemetryAsync(string tenantId, OperationsTelemetry telemetry, CancellationToken ct);
    Task<OperationsMetrics> GetMetricsAsync(string tenantId, CancellationToken ct);
    Task<OperationsVersionComparison> GetVersionComparisonAsync(string tenantId, int? selectedRevision, CancellationToken ct);
    Task<IReadOnlyList<LegacyInventoryItem>> GetLegacyInventoryAsync(string tenantId, CancellationToken ct);
}

public sealed record RegressionGate(Guid Id, bool Passed, string Suite, DateTime RecordedAt, bool OverrideActive, int AuditEntries);
public sealed record OperationsTelemetry(Guid RunId, Guid EventId, string Kind, string? NodeId, string? ToolName, string? SkillName, int? SkillRevision, string? AgentId, int? AgentRevision, long? UsageUnits, decimal? CostUnits, long? LatencyMs);
public enum OverrideWriteStatus { Accepted, Replay, NoLongerRequired, GateChanged }
public enum RolloutWriteStatus { Applied, RegressionBlocked }
public sealed record OverrideWriteResult(OverrideWriteStatus Status, RegressionGate? Gate);
public sealed record OperationsMetrics(
    bool RegressionPassed, bool OverrideActive, int ReleaseAuditEntries, int RolloutEvents,
    int RootRuns, int ChildRuns, int ChildSucceeded, int VerifierRejected, int RepairEvents,
    int WriteEffects, IReadOnlyList<AgentRevisionMetric> Agents, IReadOnlyList<SkillRevisionMetric> Skills,
    IReadOnlyList<ToolMetric> Tools, IReadOnlyList<NodeMetric> Nodes, RootAggregateMetric Aggregation);
public sealed record AgentRevisionMetric(string AgentId, int Revision, int Runs, int Completed, int Failed, long AverageLatencyMs, long ReservedBudgetUnits, long? ObservedUsageUnits, decimal? ObservedCostUnits, long? ObservedLatencyMs);
public sealed record SkillRevisionMetric(string Name, int Revision, int Runs, long? ObservedLatencyMs, long? ObservedUsageUnits, decimal? ObservedCostUnits, long ReservedBudgetUnits);
public sealed record ToolMetric(string Kind, int Count, long? ObservedLatencyMs, long? ObservedUsageUnits, decimal? ObservedCostUnits, long ReservedBudgetUnits);
public sealed record NodeMetric(string NodeId, int Executions, long AverageLatencyMs, long MaxLatencyMs);
public sealed record RootAggregateMetric(int Completed, int PartialOrFailed, int AverageFanOut, long AverageLatencyMs);
public sealed record OperationsVersionComparison(
    [property: JsonPropertyName("selected_revision")] int? SelectedRevision,
    [property: JsonPropertyName("rollout_events")] int RolloutEvents,
    [property: JsonPropertyName("new_roots_only")] bool NewRootsOnly,
    [property: JsonPropertyName("active_runs_keep_immutable_snapshot")] bool ActiveRunsKeepImmutableSnapshot,
    [property: JsonPropertyName("revisions")] IReadOnlyList<RevisionMetric> Revisions,
    [property: JsonPropertyName("selected_vs_previous")] RevisionDelta? SelectedVsPrevious);
public sealed record RevisionMetric(int Revision, int Runs, int Completed, int Failed, long AverageLatencyMs, long ReservedBudgetUnits, int ActiveRuns);
public sealed record RevisionDelta(int FromRevision, int ToRevision, int RunDelta, int CompletedDelta, long AverageLatencyDeltaMs, long ReservedBudgetDeltaUnits);
public sealed record LegacyInventoryItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("trigger")] string Trigger);

/// <summary>
/// R5 收尾盤點:哪些 legacy 執行路徑仍在線、為何保留、何時複審。內容是編譯期常數(不是租戶資料),
/// Dapper 與 in-memory 兩個 repository 回同一份 —— 兩邊各抄一次就會悄悄漂移。
/// </summary>
public static class LegacyInventory
{
    public static readonly IReadOnlyList<LegacyInventoryItem> Items =
    [
        new("flow-yaml-authors", "read_only_pending_r6", "legacy fallback below threshold"),
        new("agent-skill-runner", "explicit_legacy_executor", "r6 review"),
        new("current-skill-package", "retain_until_revision_artifact", "r6"),
    ];
}
