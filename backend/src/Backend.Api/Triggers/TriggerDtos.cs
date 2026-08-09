using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Triggers;

/// <summary>
/// O5 one-shot durable trigger lifecycle (04-operations-trigger-plan.md §6). One-shot has exactly
/// one occurrence, so the trigger's own status simply mirrors that occurrence's terminal outcome
/// (see <see cref="TriggerOccurrenceStatuses.ToTriggerStatus"/>) — there is no second state machine.
/// </summary>
public static class TriggerStatuses
{
    public const string Scheduled = "scheduled";
    public const string Cancelled = "cancelled";
    public const string Fired = "fired";
    public const string Misfired = "misfired";
    public const string Failed = "failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[] { Scheduled, Cancelled, Fired, Misfired, Failed }, StringComparer.Ordinal);
}

/// <summary>
/// Bounded delivery vocabulary for the fire ledger (§6.1 "delivery status", §6.3 fail-closed
/// reasons). Every non-fired outcome is a fixed reason code — a raw downstream message is never
/// persisted here, so an operator-visible occurrence can never leak an internal error string.
/// </summary>
public static class TriggerOccurrenceStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Fired = "fired";
    public const string SkippedCancelled = "skipped_cancelled";
    public const string SkippedMisfired = "skipped_misfired";
    public const string FailedPrincipalUnavailable = "failed_principal_unavailable";
    public const string FailedTargetMissing = "failed_target_missing";
    public const string FailedTargetUnpublished = "failed_target_unpublished";
    public const string FailedTargetRevisionChanged = "failed_target_revision_changed";
    public const string FailedDispatchDisabled = "failed_dispatch_disabled";
    public const string FailedRootRejected = "failed_root_rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        new[]
        {
            Pending, Claimed, Fired, SkippedCancelled, SkippedMisfired, FailedPrincipalUnavailable,
            FailedTargetMissing, FailedTargetUnpublished, FailedTargetRevisionChanged,
            FailedDispatchDisabled, FailedRootRejected,
        },
        StringComparer.Ordinal);

    /// <summary>Terminal occurrence outcome projected onto the one-shot trigger's own status.</summary>
    public static string ToTriggerStatus(string occurrenceStatus) => occurrenceStatus switch
    {
        Fired => TriggerStatuses.Fired,
        SkippedCancelled => TriggerStatuses.Cancelled,
        SkippedMisfired => TriggerStatuses.Misfired,
        _ => TriggerStatuses.Failed,
    };
}

/// <summary>
/// §6.1's explicitly chosen authority model: an <b>immutable grant snapshot</b> taken from the
/// creator at trigger-creation time. Firing never re-reads the creator's current permissions, so a
/// trigger cannot silently drift into a wider authority than the one an operator reviewed. Holds
/// exactly the identity fields D5 root creation needs — nothing more is snapshotted, and none of it
/// except <c>UserId</c> is ever projected into a response DTO.
/// </summary>
public sealed record TriggerPrincipal(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("groups")] IReadOnlyList<string> Groups,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

public sealed record TriggerCreateRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("orchestrator_id")] Guid OrchestratorId,
    [property: JsonPropertyName("orchestrator_revision")] int OrchestratorRevision,
    /// <summary>Absolute UTC instant. Timezone conversion is deliberately a client-side input concern.</summary>
    [property: JsonPropertyName("fire_at")] DateTime? FireAt,
    [property: JsonPropertyName("misfire_window_seconds")] int? MisfireWindowSeconds,
    [property: JsonPropertyName("input_mapping")] JsonElement? InputMapping);

/// <summary>Server-validated creation input; the controller never hands raw request text to a repository.</summary>
public sealed record TriggerCreateInput(
    string Name,
    string Description,
    Guid OrchestratorId,
    int OrchestratorRevision,
    string InputMappingCanonical,
    DateTime FireAt,
    int MisfireWindowSeconds);

public sealed record Trigger(
    Guid Id,
    string TenantId,
    string Name,
    string Description,
    Guid OrchestratorId,
    int OrchestratorRevision,
    string InputMappingCanonical,
    DateTime FireAt,
    int MisfireWindowSeconds,
    string Status,
    TriggerPrincipal Principal,
    long Version,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// §1's DTO allowlist: the immutable principal snapshot is reduced to its <c>created_by</c>
/// summary, and lease/claim token/idempotency identity never appear at all. <c>version</c> is the
/// same optimistic-lock value the ETag carries, projected into the body like D4's
/// <c>draft_version</c> — a list row therefore already carries the version its cancel needs.
/// </summary>
public sealed record TriggerResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("orchestrator_id")] Guid OrchestratorId,
    [property: JsonPropertyName("orchestrator_revision")] int OrchestratorRevision,
    [property: JsonPropertyName("input_mapping")][property: JsonConverter(typeof(RawJsonConverter))] string InputMapping,
    [property: JsonPropertyName("fire_at")] DateTime FireAt,
    [property: JsonPropertyName("misfire_window_seconds")] int MisfireWindowSeconds,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt)
{
    public static TriggerResponse From(Trigger x) => new(
        x.Id, x.Name, x.Description, x.OrchestratorId, x.OrchestratorRevision, x.InputMappingCanonical,
        x.FireAt, x.MisfireWindowSeconds, x.Status, x.Version, x.Principal.UserId, x.CreatedAt, x.UpdatedAt);
}

public sealed record TriggerListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TriggerResponse> Items);

public sealed record TriggerOccurrence(
    Guid Id,
    Guid TriggerId,
    string TenantId,
    DateTime ScheduledFor,
    string Status,
    Guid? RootRunId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Fire-history projection: no claim token, lease generation, worker id, or idempotency key.</summary>
public sealed record TriggerOccurrenceResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("trigger_id")] Guid TriggerId,
    [property: JsonPropertyName("scheduled_for")] DateTime ScheduledFor,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("root_run_id")] Guid? RootRunId,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt)
{
    public static TriggerOccurrenceResponse From(TriggerOccurrence x) =>
        new(x.Id, x.TriggerId, x.ScheduledFor, x.Status, x.RootRunId, x.CreatedAt, x.UpdatedAt);
}

public sealed record TriggerOccurrencePage(
    [property: JsonPropertyName("items")] IReadOnlyList<TriggerOccurrenceResponse> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

/// <summary>Repository-only keyset position (scheduled_for DESC, id DESC), mirroring O2/O3's cursor.</summary>
public sealed record TriggerOccurrencePosition(DateTime ScheduledFor, Guid Id);

/// <summary>A leased occurrence plus the trigger revision it must fire under. Internal only.</summary>
public sealed record TriggerClaim(TriggerOccurrence Occurrence, Trigger Trigger, string ClaimToken);

/// <summary>
/// A claim pass plus the <b>authoritative</b> instant it was taken at. The store owns "now" — the
/// Dapper side reads the database clock (the same one <c>updated_at=now()</c> already writes), so
/// the due predicate, the lease expiry and every downstream comparison (misfire) all sit on one
/// clock instead of mixing a .NET host wall clock into rows stamped by PostgreSQL.
/// </summary>
public sealed record TriggerClaimBatch(DateTime Now, IReadOnlyList<TriggerClaim> Claims);

public enum TriggerWriteStatus { Success, NotFound, VersionConflict, Duplicate, InvalidState }

public sealed record TriggerWriteResult(
    TriggerWriteStatus Status, Trigger? Trigger = null, long? CurrentVersion = null);

/// <summary>
/// AGENT_TRIGGERS_ENABLED kill switch (default off, fail closed — §8). Program.cs's pre-auth 404
/// gate is authoritative; <see cref="Enabled"/> only backs the controller's defense-in-depth check,
/// same posture as <see cref="RunDiscovery.RunDiscoveryState"/>. <see cref="DispatchEnabled"/> is
/// MULTI_AGENT_DISPATCH_ENABLED: a trigger may exist while D5 dispatch is off, but firing one then
/// creates no run at all (§6.3) — it records <c>failed_dispatch_disabled</c> instead.
/// </summary>
public sealed record AgentTriggersState(bool Enabled, bool DispatchEnabled);

/// <summary>
/// §6.3 "every occurrence has a stable identity": derived from (trigger id, scheduled instant), so
/// re-deriving it after a restart yields the same primary key and a duplicate insert is a no-op.
/// </summary>
public static class TriggerOccurrenceId
{
    public static Guid For(Guid triggerId, DateTime scheduledFor)
    {
        var utc = DateTime.SpecifyKind(scheduledFor.ToUniversalTime(), DateTimeKind.Utc);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{triggerId:D}|{utc.Ticks}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}

/// <summary>
/// §6.3's "sanitized input mapping": a fixed, flat, string-valued JSON object validated at creation
/// time and stored canonically. Nested objects/arrays, non-string values, and unbounded sizes are
/// rejected, so the pinned mapping can never grow into a place to smuggle secrets or headers. The
/// required <c>message</c> entry is the only value that reaches the D5 root command.
/// </summary>
public static class TriggerInputMapping
{
    public const string MessageKey = "message";
    public const int MaxEntries = 32;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 16384;
    public const int MaxUtf8Bytes = 65536;

    /// <summary>Canonical JSON text, or an <see cref="ApiException"/> 400 naming the failure.</summary>
    public static string Validate(JsonElement? mapping)
    {
        var element = mapping ?? default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(400, "input_mapping must be an object");
        }

        var count = 0;
        string? message = null;
        foreach (var property in element.EnumerateObject())
        {
            if (++count > MaxEntries)
            {
                throw new ApiException(400, "input_mapping has too many entries");
            }
            if (!IsCanonicalKey(property.Name))
            {
                throw new ApiException(400, "input_mapping key is invalid");
            }
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(400, "input_mapping values must be strings");
            }

            var value = property.Value.GetString() ?? string.Empty;
            if (value.Length > MaxValueLength || value.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
            {
                throw new ApiException(400, "input_mapping value is invalid");
            }
            if (property.NameEquals(MessageKey))
            {
                message = value.Trim();
            }
        }

        if (string.IsNullOrEmpty(message))
        {
            throw new ApiException(400, "input_mapping must contain a non-blank message");
        }

        var canonical = CanonicalJsonTree.NormalizeBody(element);
        if (Encoding.UTF8.GetByteCount(canonical) > MaxUtf8Bytes)
        {
            throw new ApiException(400, "input_mapping is too large");
        }

        return canonical;
    }

    /// <summary>The pinned message the fire path hands to the existing D5 root command.</summary>
    public static string Message(string canonical)
    {
        using var document = JsonDocument.Parse(canonical);
        return document.RootElement.GetProperty(MessageKey).GetString()!.Trim();
    }

    private static bool IsCanonicalKey(string key)
        => key.Length is > 0 and <= MaxKeyLength
           && (char.IsAsciiLetterLower(key[0]) || char.IsAsciiDigit(key[0]))
           && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');
}

/// <summary>
/// O5 durable trigger authority (§6.1: Backend owns definition/revision/ETag, the fire occurrence
/// ledger, claim/lease and delivery status). Both implementations must agree on: tenant scoping,
/// optimistic-lock semantics, the exclusive claim (only one concurrent caller may lease a due
/// occurrence), the terminal-status write being fenced by the claim token, and the one-shot
/// trigger status mirroring its occurrence outcome.
/// </summary>
public interface ITriggerRepository
{
    Task<TriggerWriteResult> CreateAsync(
        string tenantId, TriggerPrincipal principal, TriggerCreateInput input, CancellationToken ct);

    Task<IReadOnlyList<Trigger>> ListAsync(string tenantId, int limit, CancellationToken ct);

    Task<Trigger?> GetAsync(string tenantId, Guid id, CancellationToken ct);

    /// <summary>Stops future claims (§6.3 rollback semantics); an already-fired root is untouched.</summary>
    Task<TriggerWriteResult> CancelAsync(
        string tenantId, Guid id, long expectedVersion, CancellationToken ct);

    /// <summary>Null when the trigger is not visible to this tenant (never an empty page).</summary>
    Task<IReadOnlyList<TriggerOccurrence>?> OccurrencesAsync(
        string tenantId, Guid triggerId, TriggerOccurrencePosition? position, int limit, CancellationToken ct);

    /// <summary>
    /// Leases every due, not-yet-terminal occurrence. Concurrent callers must never lease the same
    /// occurrence: exactly one wins, the loser sees it as unavailable until the lease expires.
    /// "Due" is decided by the store's own clock, which is also returned so the caller resolves the
    /// claim against the very instant the lease was cut from.
    /// </summary>
    Task<TriggerClaimBatch> ClaimDueAsync(
        string workerId, int leaseSeconds, int limit, CancellationToken ct);

    /// <summary>
    /// Fenced terminal write: rejected unless <paramref name="claimToken"/> still owns the lease, so
    /// a resurrected worker whose lease already expired can never overwrite the newer outcome.
    /// </summary>
    Task<bool> CompleteOccurrenceAsync(
        Guid occurrenceId, string claimToken, string status, Guid? rootRunId, CancellationToken ct);
}
