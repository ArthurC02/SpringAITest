using System.Text.Json.Serialization;

namespace Backend.Api.AgentRuns;

/// <summary>Durable, single-use authorization for one server-fingerprinted write action.</summary>
public sealed record AgentRunApprovalCreateRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("lease_token")] string? LeaseToken,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef,
    [property: JsonPropertyName("checkpoint_version")] long CheckpointVersion,
    [property: JsonPropertyName("required_role")] string? RequiredRole,
    [property: JsonPropertyName("action_fingerprint")] string? ActionFingerprint,
    [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt,
    [property: JsonPropertyName("self_approval_forbidden")] bool SelfApprovalForbidden = true);

public sealed record AgentRunApprovalDecisionRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record AgentRunApprovalConsumeRequest(
    [property: JsonPropertyName("action_fingerprint")] string? ActionFingerprint,
    [property: JsonPropertyName("lease_token")] string? LeaseToken,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration);

public sealed record AgentRunApprovalConsumeResponse(
    [property: JsonPropertyName("effect_id")] Guid EffectId,
    [property: JsonPropertyName("outcome")] string Outcome);

public sealed record AgentRunApprovalExecutionIdentity(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role);
public sealed record AgentRunApprovalExecuteClaim(
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("approver_id")] string ApproverId,
    // This value is returned only to Workflow and is deliberately never stored
    // in clear text.  It fences an execute ACK after a recovery reclaim.
    [property: JsonPropertyName("claim_token")] string ClaimToken);

public sealed record AgentRunWriteEvidenceRequest(
    [property: JsonPropertyName("record_id")] string? RecordId,
    [property: JsonPropertyName("value")] string? Value);

/// <summary>
/// Result of the one durable write adapter shipped in D7.  The effect id is
/// already a server allocated, single-use idempotency key; callers cannot
/// choose it or learn any other effect identity.
/// </summary>
public sealed record AgentRunWriteEvidenceResponse(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("outcome")] string Outcome);

public sealed record AgentRunApprovalExecuteCompleteRequest(
    [property: JsonPropertyName("claim_token")] string? ClaimToken,
    [property: JsonPropertyName("dead_letter")] bool DeadLetter = false);

public sealed record AgentRunApprovalResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("action_fingerprint")] string ActionFingerprint,
    [property: JsonPropertyName("expires_at")] DateTime ExpiresAt,
    [property: JsonPropertyName("self_approval_forbidden")] bool SelfApprovalForbidden,
    [property: JsonPropertyName("requested_by")] string RequestedBy,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("decided_by")] string? DecidedBy,
    [property: JsonPropertyName("decided_at")] DateTime? DecidedAt,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("checkpoint_ref")] string CheckpointRef,
    [property: JsonPropertyName("checkpoint_version")] long CheckpointVersion);

/// <summary>Browser projection: never exposes checkpoint identity, action fingerprint, requester or decision reason.</summary>
public sealed record AgentRunApprovalPublicResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("expires_at")] DateTime ExpiresAt,
    [property: JsonPropertyName("self_approval_forbidden")] bool SelfApprovalForbidden,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("decided_at")] DateTime? DecidedAt);

public enum AgentRunApprovalWriteStatus { Success, NotFound, Conflict, Forbidden, Expired, Replay, InvalidState }

public sealed record AgentRunApprovalWriteResult(
    AgentRunApprovalWriteStatus Status,
    AgentRunApprovalResponse? Approval = null,
    AgentRunResponse? Run = null,
    string? Message = null);

/// <summary>
/// O3 "discoverable approval queue" projection. Cross-run, so unlike
/// <see cref="AgentRunApprovalPublicResponse"/> it also carries run/Agent identity — still
/// never action_fingerprint, checkpoint, effect identity, or lease.
/// </summary>
public sealed record AgentRunApprovalQueueItem(
    [property: JsonPropertyName("approval_id")] Guid ApprovalId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_revision")] int AgentRevision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("action_summary")] string ActionSummary,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("expires_at")] DateTime ExpiresAt,
    [property: JsonPropertyName("actionable")] bool Actionable);

public sealed record AgentRunApprovalQueuePage(
    [property: JsonPropertyName("items")] IReadOnlyList<AgentRunApprovalQueueItem> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>Repository-only keyset position; not serialized directly (see the controller's opaque cursor).</summary>
public sealed record AgentRunApprovalQueuePosition(DateTime CreatedAt, Guid Id);

/// <summary>
/// D7 ships exactly one write tool. The queue's "server-authored safe action summary" (O3 §4)
/// is this fixed constant, never derived from action_fingerprint or any caller-supplied text.
/// </summary>
public static class AgentRunApprovalActionCatalog
{
    public const string WriteEvidence = "runtime.write_evidence";
}
