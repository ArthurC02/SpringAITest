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
    /// <summary>
    /// W2-02(e):彙總只涵蓋最近 <paramref name="windowDays"/> 天的資料(預設
    /// <see cref="OperationsMetricsWindow.DefaultDays"/>,呼叫端可覆寫,值域由呼叫端夾在
    /// <see cref="OperationsMetricsWindow.MinDays"/>–<see cref="OperationsMetricsWindow.MaxDays"/>)。
    /// 全量 COUNT/AVG/SUM/GROUP BY 隨資料量單調變慢,加窗是為了把單次成本封頂;窗外資料不計入。
    /// 這是刻意的語意變更,因此實際區間必須隨回應帶出(<c>window_days</c>),讓畫面能明示「統計區間:
    /// 最近 N 天」。決策記錄:plans/wave2-decisions-2026-08-10.md(W2-02)。
    /// </summary>
    Task<OperationsMetrics> GetMetricsAsync(string tenantId, int windowDays, CancellationToken ct);

    /// <inheritdoc cref="GetMetricsAsync"/>
    Task<OperationsVersionComparison> GetVersionComparisonAsync(string tenantId, int? selectedRevision, int windowDays, CancellationToken ct);
    Task<IReadOnlyList<LegacyInventoryItem>> GetLegacyInventoryAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// E1 extended envelope, dual-written alongside <see cref="RecordTelemetryAsync"/> when
    /// <see cref="RunEvidenceState.Enabled"/> is true. Fails closed (no row written) on
    /// tenant/run mismatch or a caller-asserted snapshot SHA that disagrees with the run's own
    /// pinned snapshot; never throws for those cases, it just writes nothing.
    /// </summary>
    Task RecordEvidenceAsync(string tenantId, RunEvidenceEnvelope envelope, CancellationToken ct);

    /// <summary>Per-tenant, per-event comparison between the legacy metric and the extended
    /// envelope so dual-write never lets usage/cost carry two authorities.</summary>
    Task<EvidenceReconcileSummary> GetEvidenceReconcileAsync(string tenantId, CancellationToken ct);
}

/// <summary>
/// W2-02(e) 儀表板統計時間窗。有界(1–365)且超界一律拒絕(400)而非夾到邊界:一個打錯的
/// <c>window_days=3650</c> 若被默默夾成 365,呼叫端會拿到一份標示為 365 天、自己以為是 3650 天的
/// 數字——這正是本決策要避免的「數字含義改變卻不告訴使用者」。
/// </summary>
public static class OperationsMetricsWindow
{
    public const int DefaultDays = 90;
    public const int MinDays = 1;
    public const int MaxDays = 365;
}

public sealed record RegressionGate(Guid Id, bool Passed, string Suite, DateTime RecordedAt, bool OverrideActive, int AuditEntries);
public sealed record OperationsTelemetry(Guid RunId, Guid EventId, string Kind, string? NodeId, string? ToolName, string? SkillName, int? SkillRevision, string? AgentId, int? AgentRevision, long? UsageUnits, decimal? CostUnits, long? LatencyMs);

/// <summary>
/// E1 run evidence envelope. Identity/revision/hash/trace fields only -- deliberately excludes
/// raw prompt, context body, memory fact, tool argument/result, JWT, credential or
/// chain-of-thought (see plan §4). Root/child lineage and the immutable snapshot SHA are not
/// carried here: the repository resolves and validates them server-side from the run's own
/// <c>agent_run</c>/<c>orchestrator_run_child</c> rows, never from the caller.
/// </summary>
public sealed record RunEvidenceEnvelope(
    Guid RunId, Guid EventId, string Kind, string Outcome, string? ErrorClass, string? SnapshotSha256,
    string? AgentId, int? AgentRevision, int? OrchestratorRevision, string? SkillName, int? SkillRevision,
    string? PromptManifestSha256, int? ContextRevision, string? RoleView, int? PolicyRevision,
    string? ModelProvider, string? ModelDeployment, string? ModelId, string? ModelFingerprint, string? ModelSettingsHash,
    string? ToolName, int? ToolRevision, string? NodeId, string? TraceId, string? SpanId,
    long? UsageUnits, decimal? CostUnits, long? LatencyMs, string ObservationQuality,
    string? VerifierVerdict, string? CaseVerdict, string? RedactionNote);

public sealed record EvidenceReconcileSummary(
    [property: JsonPropertyName("metric_event_count")] int MetricEventCount,
    [property: JsonPropertyName("envelope_event_count")] int EnvelopeEventCount,
    [property: JsonPropertyName("mismatched_event_count")] int MismatchedEventCount,
    [property: JsonPropertyName("unknown_observation_count")] int UnknownObservationCount,
    [property: JsonPropertyName("measured_usage_units_sum")] long? MeasuredUsageUnitsSum);
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
    [property: JsonPropertyName("selected_vs_previous")] RevisionDelta? SelectedVsPrevious,
    /// <summary>W2-02(e):這份比較實際涵蓋的統計區間(最近 N 天)。</summary>
    [property: JsonPropertyName("window_days")] int WindowDays);
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
