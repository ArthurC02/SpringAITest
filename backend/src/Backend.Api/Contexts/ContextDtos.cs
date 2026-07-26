using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Api.Contexts;

public static class ContextStatuses
{
    public const string NeedMoreContext = "NEED_MORE_CONTEXT";
    public const string NeedsClarification = "NEEDS_CLARIFICATION";
    public const string BlockedByPolicy = "BLOCKED_BY_POLICY";
    public const string InsufficientData = "INSUFFICIENT_DATA";
    public const string Ready = "READY";
    public const string ReadyWithAssumptions = "READY_WITH_ASSUMPTIONS";

    public static bool IsReady(string status) => status is Ready or ReadyWithAssumptions;
}

public sealed record ContextRevisionSubmitRequest(
    [property: JsonPropertyName("root_run_id")] Guid? RootRunId,
    [property: JsonPropertyName("definition")] JsonElement? Definition,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ContextEvidenceInput>? Evidence = null,
    [property: JsonPropertyName("views")] IReadOnlyList<ContextViewInput>? Views = null,
    [property: JsonPropertyName("measurements")] ContextObjectiveMeasurements? Measurements = null,
    [property: JsonPropertyName("as_of")] DateTime? AsOf = null,
    [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt = null);

public sealed record ContextObjectiveMeasurements(
    [property: JsonPropertyName("context_round")] int ContextRound,
    [property: JsonPropertyName("max_context_rounds")] int MaxContextRounds,
    [property: JsonPropertyName("critical_ambiguity")] bool CriticalAmbiguity = false,
    [property: JsonPropertyName("deadline_exhausted")] bool DeadlineExhausted = false,
    [property: JsonPropertyName("assumptions_count")] int AssumptionsCount = 0,
    [property: JsonPropertyName("policy_violations")] IReadOnlyList<string>? PolicyViolations = null,
    [property: JsonPropertyName("retrieval_gaps")] IReadOnlyList<ContextSourceFailure>? SourceFailures = null);

public sealed record ContextSourceFailure(
    [property: JsonPropertyName("source_id")] string? SourceId,
    [property: JsonPropertyName("failure_code")] string? FailureCode);

public sealed record ContextEvidenceInput(
    [property: JsonPropertyName("evidence_type")] string? EvidenceType,
    [property: JsonPropertyName("source_id")] string? SourceId,
    [property: JsonPropertyName("snapshot_id")] string? SnapshotId,
    [property: JsonPropertyName("content_ref")] string? ContentRef,
    [property: JsonPropertyName("content_hash")] string? ContentHash,
    [property: JsonPropertyName("scope")] JsonElement? Scope = null,
    [property: JsonPropertyName("observations")] JsonElement? Observations = null,
    [property: JsonPropertyName("acl_decision_id")] string? AclDecisionId = null,
    [property: JsonPropertyName("observed_at")] DateTime? ObservedAt = null,
    [property: JsonPropertyName("lineage")] JsonElement? Lineage = null);

public sealed record ContextViewInput(
    [property: JsonPropertyName("view_type")] string? ViewType,
    [property: JsonPropertyName("definition")] JsonElement? Definition);

public sealed record ContextRef(
    [property: JsonPropertyName("context_id")] Guid ContextId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("view_id")] Guid ViewId);

public sealed record ContextRevisionResponse(
    [property: JsonPropertyName("context_id")] Guid ContextId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("root_run_id")] Guid? RootRunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("readiness")] decimal Readiness,
    [property: JsonPropertyName("unmet_requirements")] IReadOnlyList<string> UnmetRequirements,
    [property: JsonPropertyName("policy_id")] Guid PolicyId,
    [property: JsonPropertyName("definition")] JsonElement Definition,
    [property: JsonPropertyName("as_of")] DateTime AsOf,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt,
    [property: JsonPropertyName("context_ref")] ContextRef? ContextRef = null,
    [property: JsonPropertyName("selected_source_id")] string? SelectedSourceId = null,
    [property: JsonPropertyName("adapter_id")] string? AdapterId = null);

public sealed record ContextViewResponse(
    [property: JsonPropertyName("view_id")] Guid ViewId,
    [property: JsonPropertyName("context_id")] Guid ContextId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("view_type")] string ViewType,
    [property: JsonPropertyName("definition")] JsonElement Definition);

public sealed record ContextPolicyResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] JsonElement Values,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("sources")] IReadOnlyList<ContextSourceCatalogEntry>? Sources = null);

public sealed record ContextSourceCatalogEntry(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("evidence_type")] string EvidenceType,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("authority_class")] string AuthorityClass,
    [property: JsonPropertyName("adapter_id")] string AdapterId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds,
    [property: JsonPropertyName("minimum_deadline_seconds")] int MinimumDeadlineSeconds);

public sealed record ContextStoredRevision(
    ContextRevisionResponse Revision,
    IReadOnlyList<ContextEvidenceInput> Evidence,
    IReadOnlyList<ContextViewResponse> Views,
    string? AdapterId = null);
