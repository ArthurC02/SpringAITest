using System.Text.Json.Serialization;

namespace Backend.Api.CheckpointRetention;

public sealed record CheckpointRetentionCandidate(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("snapshot_sha256")] string SnapshotSha256,
    [property: JsonPropertyName("max_generation")] long MaxGeneration,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef,
    [property: JsonPropertyName("completed_at")] DateTime CompletedAt);

public sealed record CheckpointRetentionPage(
    [property: JsonPropertyName("items")] IReadOnlyList<CheckpointRetentionCandidate> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record CheckpointRetentionAckRequest(
    [property: JsonPropertyName("candidate_id")] string? CandidateId,
    [property: JsonPropertyName("deleted_threads")] int DeletedThreads,
    [property: JsonPropertyName("deleted_root_contexts")] int DeletedRootContexts,
    [property: JsonPropertyName("evidence_ref")] string? EvidenceRef);

public sealed record CheckpointRetentionPosition(DateTime CompletedAt, Guid RunId, byte Kind);

public sealed record CheckpointRetentionRow(
    int KindOrder,
    string Kind,
    string Source,
    string TenantId,
    string UserId,
    Guid RunId,
    string SnapshotSha256,
    long MaxGeneration,
    string? CheckpointRef,
    DateTime CompletedAt);
