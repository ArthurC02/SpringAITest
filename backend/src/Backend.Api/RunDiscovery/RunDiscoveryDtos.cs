using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.AgentRuns;

namespace Backend.Api.RunDiscovery;

/// <summary>
/// O2 unified runs/tasks discovery kinds (04-operations-trigger-plan.md §3). "direct-agent" /
/// "worker" / "verifier" are copied verbatim from <c>agent_run.run_kind</c>; "orchestrator" is
/// synthesized for D5 root aggregates, which live in a separate table with no run_kind column.
/// </summary>
public static class RunDiscoveryKinds
{
    public const string DirectAgent = "direct-agent";
    public const string Worker = "worker";
    public const string Verifier = "verifier";
    public const string Orchestrator = "orchestrator";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { DirectAgent, Worker, Verifier, Orchestrator }, StringComparer.Ordinal);

    /// <summary>
    /// Kinds sourced from the <c>agent_run</c> table — the same table
    /// <see cref="AgentRunController"/>'s class-level <c>[AdminOnly]</c> gate protects end to end
    /// (GET /api/runs/{id} included). Only an ADMIN caller may see these in the unified list; a
    /// non-ADMIN <c>workflow.manage</c> holder still sees their own orchestrator (root) items.
    /// </summary>
    public static readonly IReadOnlySet<string> AgentRunSourced = new HashSet<string>(
        new[] { DirectAgent, Worker, Verifier }, StringComparer.Ordinal);
}

/// <summary>Union of the agent_run and orchestrator_run status vocabularies (only orchestrator_run has 'timed_out').</summary>
public static class RunDiscoveryStatuses
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        AgentRunStatuses.All.Append("timed_out"), StringComparer.Ordinal);
}

public sealed record RunChildProgress(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("queued")] int Queued,
    [property: JsonPropertyName("running")] int Running,
    [property: JsonPropertyName("completed")] int Completed,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("cancelled")] int Cancelled);

/// <summary>
/// O2 "safe summary" projection (04-operations-trigger-plan.md §3): pinned revisions, elapsed,
/// budget summary, last event, child progress, error class. Deliberately narrower than the
/// existing per-run detail responses — it never carries prompt text, tool argument/result values,
/// checkpoint identity, effect identity, lease, or idempotency keys (same redaction posture as
/// D5's public root events and the O3 approval queue projection).
/// </summary>
public sealed record RunSummaryItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("orchestrator_root_run_id")] Guid? OrchestratorRootRunId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("agent_id")] Guid? AgentId,
    [property: JsonPropertyName("agent_revision")] int? AgentRevision,
    [property: JsonPropertyName("orchestrator_id")] Guid? OrchestratorId,
    [property: JsonPropertyName("orchestrator_revision")] int? OrchestratorRevision,
    [property: JsonPropertyName("workflow_id")] Guid WorkflowId,
    [property: JsonPropertyName("workflow_revision")] int WorkflowRevision,
    [property: JsonPropertyName("cancel_requested")] bool CancelRequested,
    [property: JsonPropertyName("budget_summary")] JsonElement BudgetSummary,
    [property: JsonPropertyName("last_event_type")] string? LastEventType,
    [property: JsonPropertyName("last_event_at")] DateTime? LastEventAt,
    [property: JsonPropertyName("child_progress")] RunChildProgress? ChildProgress,
    [property: JsonPropertyName("error_class")] string? ErrorClass,
    [property: JsonPropertyName("pending_approval")] bool PendingApproval,
    [property: JsonPropertyName("needs_recovery")] bool NeedsRecovery,
    [property: JsonPropertyName("started_at")] DateTime? StartedAt,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("completed_at")] DateTime? CompletedAt,
    [property: JsonPropertyName("elapsed_seconds")] double ElapsedSeconds);

public sealed record RunDiscoveryPage(
    [property: JsonPropertyName("items")] IReadOnlyList<RunSummaryItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>Repository-only keyset position (created_at DESC, id DESC); not serialized directly (see the controller's opaque cursor).</summary>
public sealed record RunDiscoveryPosition(DateTime CreatedAt, Guid Id);

/// <summary>
/// O2's caller-supplied filter set (04-operations-trigger-plan.md §3): status, kind, Agent/
/// Orchestrator identity, created range, approval/recovery state. "owner" is deliberately not a
/// field here: every implementation already scopes to the caller's own tenant+identity (see
/// <see cref="IRunDiscoveryRepository"/>), so a caller can never be shown a run any other owner
/// started — the controller answers a foreign "owner" query with an empty page, the same
/// indistinguishable-from-missing posture O2 §3's acceptance criteria require for cross-tenant
/// queries.
/// </summary>
public sealed record RunDiscoveryFilter(
    string? Kind,
    string? Status,
    Guid? AgentId,
    Guid? OrchestratorId,
    DateTime? CreatedFrom,
    DateTime? CreatedTo,
    bool? PendingApproval,
    bool? NeedsRecovery);

/// <summary>
/// RUN_DISCOVERY_ENABLED kill switch (default off, fail closed — 04-operations-trigger-plan.md
/// §8). Same posture as <see cref="AgentWriteToolsState"/>: Program.cs's pre-auth 404 gate is
/// authoritative, this DI state only backs the controller's own defense-in-depth check.
/// </summary>
public sealed record RunDiscoveryState(bool Enabled);
