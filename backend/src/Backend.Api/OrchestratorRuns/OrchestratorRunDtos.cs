using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Contexts;

namespace Backend.Api.OrchestratorRuns;

public sealed record OrchestratorRunStartRequest(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("conversation_id")] string? ConversationId);

public sealed record OrchestratorRunCancelRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);
public sealed record OrchestratorRunResumeRequest(
    [property: JsonPropertyName("input")] string? Input);

public sealed record OrchestratorRunResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("orchestrator_id")] Guid OrchestratorId,
    [property: JsonPropertyName("orchestrator_revision")] int OrchestratorRevision,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("workflow_id")] Guid WorkflowId,
    [property: JsonPropertyName("workflow_revision")] int WorkflowRevision,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("cancel_requested")] bool CancelRequested,
    [property: JsonPropertyName("state_version")] long StateVersion,
    [property: JsonPropertyName("deadline_at")] DateTime DeadlineAt,
    [property: JsonPropertyName("budgets")] JsonElement Budgets,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("command_id")] Guid? CommandId = null,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record OrchestratorRunEventResponse(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

public sealed record OrchestratorRunEventsResponse(
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("events")] IReadOnlyList<OrchestratorRunEventResponse> Events,
    [property: JsonPropertyName("next_sequence")] long NextSequence);

public enum OrchestratorRunWriteStatus { Success, Replay, NotFound, Conflict, InvalidState }
public sealed record OrchestratorRunDispatch(Guid CommandId);
public sealed record OrchestratorRunWriteResult(OrchestratorRunWriteStatus Status, OrchestratorRunResponse? Run = null, string? Message = null, OrchestratorRunDispatch? Dispatch = null, bool Replayed = false);
/// <summary>Owner-scoped discovery result.  <see cref="CommandId"/> is the command that
/// matched an idempotency key (rather than always the root's original start command).</summary>
public sealed record OrchestratorRunActiveLookup(
    OrchestratorRunResponse? Run,
    Guid? CommandId = null,
    bool IsAmbiguous = false,
    bool IsMismatch = false);
public sealed record OrchestratorRunReplayRequest(
    [property: JsonPropertyName("conversation_id")] string? ConversationId,
    [property: JsonPropertyName("orchestrator_id")] Guid? OrchestratorId,
    [property: JsonPropertyName("message")] string? Message);
public sealed record OrchestratorRunCommandClaimRequest(
    [property: JsonPropertyName("worker_id")] string? WorkerId,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds = 30);
public sealed record OrchestratorRunDispatchCompleteRequest(
    [property: JsonPropertyName("claim_token")] string? ClaimToken);
public sealed record OrchestratorRunCommandRenewRequest(
    [property: JsonPropertyName("claim_token")] string? ClaimToken,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds = 30);
public sealed record OrchestratorRunCommandClaim(
    [property: JsonPropertyName("command_id")] Guid CommandId,
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("command_type")] string CommandType,
    [property: JsonPropertyName("claim_token")] string ClaimToken,
    [property: JsonPropertyName("claim_expires_at")] DateTime ClaimExpiresAt,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("snapshot_hash")] string SnapshotHash,
    [property: JsonPropertyName("snapshot_canonical_base64")] string SnapshotCanonicalBase64,
    [property: JsonPropertyName("resume_input")] string? ResumeInput = null,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef = null,
    [property: JsonPropertyName("checkpoint_version")] long? CheckpointVersion = null,
    [property: JsonPropertyName("deadline_at")] DateTime? DeadlineAt = null);
public enum OrchestratorRunDispatchCompleteStatus { Success, NotFound, Conflict }
public sealed record OrchestratorRunRecoveryClaimRequest(
    [property: JsonPropertyName("worker_id")] string? WorkerId,
    [property: JsonPropertyName("limit")] int Limit = 20,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds = 30);
public sealed record OrchestratorRunRecoveryItem(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("claim")] OrchestratorRunCommandClaim Claim);
public sealed record OrchestratorRunRecoveryResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<OrchestratorRunRecoveryItem> Items,
    [property: JsonPropertyName("has_more")] bool HasMore);
public sealed record OrchestratorChildCreateRequest(
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("run_kind")] string? RunKind,
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_revision")] int AgentRevision,
    [property: JsonPropertyName("write_intent")] bool WriteIntent = false,
    // This is intentionally a typed-on-the-wire JSON envelope rather than an arbitrary
    // command payload. The repository validates every field before canonicalising it.
    [property: JsonPropertyName("task_envelope")] JsonElement? TaskEnvelope = null,
    // Workflow reads this from the root's immutable worker pin. Backend requires exact
    // equality so a compromised coordinator cannot inflate a child allocation.
    [property: JsonPropertyName("token_cap")] int TokenCap = 0);
public sealed record OrchestratorChildResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("orchestrator_root_run_id")] Guid OrchestratorRootRunId,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("run_kind")] string RunKind,
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_revision")] int AgentRevision,
    [property: JsonPropertyName("workflow_id")] Guid WorkflowId,
    [property: JsonPropertyName("workflow_revision")] int WorkflowRevision,
    [property: JsonPropertyName("agent_snapshot_hash")] string AgentSnapshotHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("agent_run_id")] Guid AgentRunId,
    [property: JsonPropertyName("command_id")] Guid CommandId);
public sealed record OrchestratorChildStatusResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("orchestrator_root_run_id")] Guid OrchestratorRootRunId,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("run_kind")] string RunKind,
    [property: JsonPropertyName("agent_id")] Guid AgentId,
    [property: JsonPropertyName("agent_revision")] int AgentRevision,
    [property: JsonPropertyName("workflow_id")] Guid WorkflowId,
    [property: JsonPropertyName("workflow_revision")] int WorkflowRevision,
    [property: JsonPropertyName("agent_snapshot_hash")] string AgentSnapshotHash,
    [property: JsonPropertyName("agent_run_id")] Guid AgentRunId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("output")] JsonElement Output,
    [property: JsonPropertyName("citations")] JsonElement Citations,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_message")] string? ErrorMessage);
public sealed record OrchestratorRootTransitionRequest(
    [property: JsonPropertyName("expected_state_version")] long ExpectedStateVersion,
    [property: JsonPropertyName("claim_token")] string? ClaimToken,
    [property: JsonPropertyName("lease_generation")] long LeaseGeneration,
    [property: JsonPropertyName("to_status")] string? ToStatus,
    [property: JsonPropertyName("checkpoint_ref")] string? CheckpointRef = null,
    [property: JsonPropertyName("checkpoint_version")] long? CheckpointVersion = null,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null,
    [property: JsonPropertyName("events")] IReadOnlyList<OrchestratorRootEventAppend>? Events = null);
public sealed record OrchestratorRootEventAppend(
    [property: JsonPropertyName("event_type")] string? EventType,
    [property: JsonPropertyName("payload")] JsonElement? Payload);
public sealed record OrchestratorContextAcquireRequest(
    [property: JsonPropertyName("context_round")] int ContextRound,
    [property: JsonPropertyName("current_context")] JsonElement? CurrentContext,
    [property: JsonPropertyName("allowed_tools")] IReadOnlyList<string>? AllowedTools,
    [property: JsonPropertyName("allowed_knowledge_sources")] IReadOnlyList<string>? AllowedKnowledgeSources);
public sealed record OrchestratorContextAcquireResponse(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("context")] JsonElement Context,
    [property: JsonPropertyName("provenance")] IReadOnlyList<JsonElement> Provenance,
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);

public sealed record OrchestratorContextRequestResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("root_run_id")] Guid RootRunId,
    [property: JsonPropertyName("child_id")] Guid ChildId,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("context_id")] Guid ContextId,
    [property: JsonPropertyName("base_context_ref")] ContextRef? BaseContextRef,
    [property: JsonPropertyName("current_context_ref")] ContextRef? CurrentContextRef,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);

/// <summary>Workflow can submit content, but never ownership, task lineage, or view role.</summary>
public sealed record OrchestratorContextDeltaRequest(
    [property: JsonPropertyName("definition")] JsonElement? Definition,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ContextEvidenceInput>? Evidence = null,
    [property: JsonPropertyName("views")] IReadOnlyList<ContextViewInput>? Views = null,
    [property: JsonPropertyName("measurements")] ContextObjectiveMeasurements? Measurements = null,
    [property: JsonPropertyName("as_of")] DateTime? AsOf = null,
    [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt = null);

public enum OrchestratorContextDeltaStatus { Success, NotFound, Conflict }
public sealed record OrchestratorContextDeltaResult(
    OrchestratorContextDeltaStatus Status,
    ContextRevisionResponse? Revision = null,
    long Version = 0);

/// <summary>
/// Public root-event payloads, shared by both repositories so the two authorities cannot
/// drift.  Redaction happens at the producer: the durable command identity is an internal
/// execution claim, and <c>EventsAsync</c> returns payloads verbatim to owners.
/// </summary>
public static class OrchestratorRunEvents
{
    public static string ChildCreated(Guid childId, Guid agentRunId, string taskId, int attempt, string runKind)
        => JsonSerializer.Serialize(new
        {
            child_id = childId,
            agent_run_id = agentRunId,
            task_id = taskId,
            attempt,
            run_kind = runKind,
        });

    /// <summary>Child output/context belongs to the child authority: only its hash and citation
    /// count reach the public root stream.</summary>
    public static string ChildTerminal(
        Guid childId, Guid agentRunId, string taskId, int attempt, string runKind,
        Guid agentId, int agentRevision, string agentSnapshotHash,
        string status, JsonElement? result, string? errorCode)
    {
        var raw = result is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value.GetRawText() : null;
        return JsonSerializer.Serialize(new
        {
            child_id = childId,
            agent_run_id = agentRunId,
            task_id = taskId,
            attempt,
            run_kind = runKind,
            agent_id = agentId,
            agent_revision = agentRevision,
            agent_snapshot_hash = agentSnapshotHash,
            status,
            result_sha256 = raw is null ? null : Skills.SkillHash.Sha256(raw),
            citations = new { count = Citations(result) },
            error_code = Normalize(errorCode, 100),
        });
    }

    /// <summary>Also the payload of <c>root_waiting_input</c>: both carry the reached status.</summary>
    public static string RootTerminal(string status) => JsonSerializer.Serialize(new { status });

    private static int Citations(JsonElement? result)
        => result is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty("citations", out var citations)
           && citations.ValueKind == JsonValueKind.Array
            ? citations.GetArrayLength()
            : 0;

    private static string? Normalize(string? value, int max)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized[..Math.Min(normalized.Length, max)];
    }
}

public interface IOrchestratorRunRepository
{
    Task<OrchestratorRunWriteResult> CreateAsync(string tenantId, string userId, string role,
        IReadOnlyCollection<string> groups, IReadOnlyCollection<string> capabilities,
        Guid orchestratorId, string conversationId, string message, string idempotencyKey, CancellationToken ct);
    Task<OrchestratorRunResponse?> GetAsync(string tenantId, string userId, Guid runId, CancellationToken ct);
    Task<OrchestratorRunActiveLookup> FindActiveAsync(string tenantId, string userId, string conversationId, CancellationToken ct);
    Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenantId, string userId, string idempotencyKey, OrchestratorRunReplayRequest replay, CancellationToken ct);
    Task<OrchestratorRunEventsResponse?> EventsAsync(string tenantId, string userId, Guid runId, long after, int limit, CancellationToken ct);
    Task<OrchestratorRunWriteResult> CancelAsync(string tenantId, string userId, Guid runId, string? reason, string idempotencyKey, CancellationToken ct);
    Task<OrchestratorRunWriteResult> ResumeAsync(string tenantId, string userId, Guid runId, string input, string idempotencyKey, CancellationToken ct);
    Task<string?> ExecutionArtifactAsync(string tenantId, string userId, Guid runId, CancellationToken ct);
    Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string tenantId, string userId, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct);
    Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string tenantId, string userId, Guid runId, Guid commandId, string claimToken, long leaseGeneration, int leaseSeconds, CancellationToken ct);
    Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string tenantId, string userId, Guid runId, Guid commandId, string claimToken, CancellationToken ct);
    Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string workerId, int limit, int leaseSeconds, CancellationToken ct);
    Task<OrchestratorChildResponse?> CreateChildAsync(string tenantId, string userId, Guid rootRunId, OrchestratorChildCreateRequest request, CancellationToken ct);
    Task<OrchestratorChildStatusResponse?> GetChildAsync(string tenantId, string userId, Guid rootRunId, Guid childId, CancellationToken ct);
    Task<OrchestratorRunWriteResult> TransitionAsync(string tenantId, string userId, Guid rootRunId, OrchestratorRootTransitionRequest request, CancellationToken ct);
    Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string tenantId, string userId, Guid rootRunId, OrchestratorContextAcquireRequest request, CancellationToken ct);
    Task<OrchestratorContextRequestResponse?> GetOrCreateContextRequestAsync(string tenantId, string userId, Guid rootRunId, Guid childId, CancellationToken ct);
    Task<OrchestratorContextRequestResponse?> GetContextRequestAsync(string tenantId, string userId, Guid rootRunId, Guid childId, Guid requestId, CancellationToken ct);
    Task<OrchestratorContextDeltaResult> AppendContextDeltaAsync(string tenantId, string userId, Guid rootRunId, Guid childId, Guid requestId, long expectedVersion, OrchestratorContextDeltaRequest request, CancellationToken ct);
}
