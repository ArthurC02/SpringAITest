using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Api.AgentRuns;

public static class AgentRunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string WaitingInput = "waiting_input";
    public const string WaitingApproval = "waiting_approval";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { Queued, Running, WaitingInput, WaitingApproval, Completed, Failed, Cancelled },
        StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(
        new[] { Completed, Failed, Cancelled },
        StringComparer.Ordinal);

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Queued, Running) => true,
        (Queued, Cancelled) => true,
        (Running, WaitingInput) => true,
        (Running, WaitingApproval) => true,
        (Running, Completed) => true,
        (Running, Failed) => true,
        (Running, Cancelled) => true,
        (WaitingInput, Queued) => true,
        (WaitingInput, Running) => true,
        (WaitingInput, Cancelled) => true,
        (WaitingApproval, Queued) => true,
        (WaitingApproval, Running) => true,
        (WaitingApproval, Cancelled) => true,
        _ => false,
    };
}

public sealed record DirectAgentRunStartRequest(
    [property: JsonPropertyName("message")] string? Message);

public sealed record AgentRunResumeRequest(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("expected_checkpoint_version")] long? ExpectedCheckpointVersion);

public sealed record AgentRunCancelRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record AgentRunDispatchCompleteRequest(
    [property: JsonPropertyName("claim_token")] string? ClaimToken);

public sealed record AgentRunCommandClaimRequest(
    [property: JsonPropertyName("worker_id")] string? WorkerId,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds = 30);

public sealed record AgentRunRecoveryClaimRequest(
    [property: JsonPropertyName("worker_id")] string? WorkerId,
    [property: JsonPropertyName("limit")] int Limit = 20,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds = 30);

public sealed record AgentRunRecoveryClaimResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<AgentRunRecoveryItem> Items,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>
/// Internal Workflow-only recovery envelope. Input contains the original user message/reason and
/// must never be embedded in <see cref="AgentRunResponse"/> or proxied by Platform to browsers.
/// </summary>
public sealed record AgentRunRecoveryItem(
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("command_type")] string CommandType,
    [property: JsonPropertyName("input")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Input,
    [property: JsonPropertyName("tenant_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId,
    [property: JsonPropertyName("user_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId,
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("snapshot")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Snapshot,
    [property: JsonPropertyName("run_status")] string RunStatus,
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("checkpoint_generation")] long CheckpointGeneration,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef,
    [property: JsonPropertyName("checkpoint_version")] long CheckpointVersion,
    [property: JsonPropertyName("event_ack_cursor")] long EventAckCursor,
    [property: JsonPropertyName("deadline_at")] DateTime DeadlineAt,
    [property: JsonPropertyName("lease_token")] string LeaseToken,
    [property: JsonPropertyName("lease_expires_at")] DateTime LeaseExpiresAt,
    [property: JsonPropertyName("claim_token")] string ClaimToken,
    [property: JsonPropertyName("claim_expires_at")] DateTime ClaimExpiresAt,
    [property: JsonPropertyName("dispatch_attempt")] int DispatchAttempt,
    [property: JsonPropertyName("target_terminal")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TargetTerminal = null);

public sealed record AgentRunTransitionRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("to_status")] string? ToStatus,
    [property: JsonPropertyName("lease_token")] string? LeaseToken = null,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration = 0,
    [property: JsonPropertyName("expected_event_ack_cursor")] long? ExpectedEventAckCursor = null,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef = null,
    [property: JsonPropertyName("checkpoint_version")] long? CheckpointVersion = null,
    [property: JsonPropertyName("pending_input")] JsonElement? PendingInput = null,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record AgentRunEventsAppendRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("lease_token")] string? LeaseToken,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("event_cursor_start")] long EventCursorStart,
    [property: JsonPropertyName("events")] IReadOnlyList<AgentRunEventAppend>? Events);

public sealed record AgentRunEventAppend(
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("node_id")] string? NodeId,
    [property: JsonPropertyName("snapshot_hash")] string? SnapshotHash,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

public sealed record AgentRunLeaseRequest(
    [property: JsonPropertyName("expected_version")] long ExpectedVersion,
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("duration_seconds")] int DurationSeconds = 30);

public sealed record AgentRunLeaseResponse(
    [property: JsonPropertyName("lease_token")] string LeaseToken,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("lease_expires_at")] DateTime LeaseExpiresAt,
    [property: JsonPropertyName("checkpoint_generation")] long CheckpointGeneration,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef,
    [property: JsonPropertyName("checkpoint_version")] long CheckpointVersion,
    [property: JsonPropertyName("event_ack_cursor")] long EventAckCursor,
    [property: JsonPropertyName("run")] AgentRunResponse Run);

public sealed record AgentRunResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("root_run_id")] Guid RootRunId,
    [property: JsonPropertyName("parent_run_id")] Guid? ParentRunId,
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("run_kind")] string RunKind,
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_revision")] int AgentRevision,
    [property: JsonPropertyName("workflow_id")] Guid WorkflowId,
    [property: JsonPropertyName("workflow_revision")] int WorkflowRevision,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("checkpoint_generation")] long CheckpointGeneration,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef,
    [property: JsonPropertyName("checkpoint_version")] long CheckpointVersion,
    [property: JsonPropertyName("event_ack_cursor")] long EventAckCursor,
    [property: JsonPropertyName("cancel_requested")] bool CancelRequested,
    [property: JsonPropertyName("pending_input")] JsonElement? PendingInput,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("latest_event_sequence")] long LatestEventSequence,
    [property: JsonPropertyName("started_at")] DateTime? StartedAt,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("deadline_at")] DateTime DeadlineAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("completed_at")] DateTime? CompletedAt,
    [property: JsonPropertyName("skills")] IReadOnlyList<AgentRunSkillPinResponse> Skills,
    [property: JsonPropertyName("runtime_limits")] JsonElement RuntimeLimits,
    [property: JsonPropertyName("command_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? CommandId = null);

public sealed record AgentRunSkillPinResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("package_sha256")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackageSha256);

public sealed record AgentRunEventResponse(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("node_id")] string? NodeId,
    [property: JsonPropertyName("lease_generation")] long? LeaseGeneration,
    [property: JsonPropertyName("event_cursor")] long? EventCursor,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

public sealed record AgentRunEventsResponse(
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("events")] IReadOnlyList<AgentRunEventResponse> Events,
    [property: JsonPropertyName("next_sequence")] long NextSequence);

public enum AgentRunWriteStatus
{
    Success,
    Replay,
    NotFound,
    Conflict,
    InvalidState,
}

public sealed record AgentRunWriteResult(
    AgentRunWriteStatus Status,
    AgentRunResponse? Run = null,
    string? Message = null,
    AgentRunCommandDispatch? Dispatch = null,
    bool Replayed = false);

public sealed record AgentRunCommandDispatch(
    Guid CommandId,
    string ClaimToken,
    DateTime ClaimExpiresAt,
    int DispatchAttempt);

public enum AgentRunDispatchCompleteStatus
{
    Success,
    NotFound,
    Conflict,
}

public sealed record AgentRunLeaseResult(
    AgentRunWriteStatus Status,
    AgentRunLeaseResponse? Lease = null,
    string? Message = null);

public sealed record AgentRunCommandClaimResult(
    AgentRunWriteStatus Status,
    AgentRunRecoveryItem? Item = null,
    string? Message = null);

public sealed record SkillExecutionArtifact(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("package_sha256")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackageSha256,
    [property: JsonPropertyName("package_base64")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PackageBase64);
