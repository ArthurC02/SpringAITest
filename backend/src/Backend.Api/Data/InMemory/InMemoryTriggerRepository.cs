using Backend.Api.Common;
using Backend.Api.Triggers;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// Lite/test parity for <see cref="Backend.Api.Triggers.TriggerRepository"/>. Every mutation runs
/// under one gate and writes all of its rows before releasing it, mirroring the Dapper side's
/// all-or-nothing transactions: a partially applied cancel or completion must never be observable
/// (see backend/AGENTS.md "Dapper ↔ InMemory parity is not automatic").
/// </summary>
public sealed class InMemoryTriggerRepository : ITriggerRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _triggers = [];
    private readonly Dictionary<Guid, OccurrenceEntry> _occurrences = [];

    public Task<TriggerWriteResult> CreateAsync(
        string tenantId, TriggerPrincipal principal, TriggerCreateInput input, CancellationToken ct)
    {
        lock (_gate)
        {
            // Uniqueness only holds while scheduled (see DbBootstrap uq_agent_trigger_tenant_name):
            // cancel + recreate under the same name is the documented one-shot reschedule path.
            if (_triggers.Values.Any(x =>
                    x.TenantId == tenantId && string.Equals(x.Name, input.Name, StringComparison.Ordinal)
                    && x.Status == TriggerStatuses.Scheduled))
            {
                return Task.FromResult(new TriggerWriteResult(TriggerWriteStatus.Duplicate));
            }

            var now = DateTime.UtcNow;
            var fireAt = Utc(input.FireAt);
            var entry = new Entry(
                Guid.NewGuid(), tenantId, input.Name, input.Description, input.OrchestratorId,
                input.OrchestratorRevision, input.InputMappingCanonical, fireAt,
                input.MisfireWindowSeconds, principal, now)
            {
                Status = TriggerStatuses.Scheduled,
                Version = 1,
                UpdatedAt = now,
            };
            _triggers.Add(entry.Id, entry);
            var occurrenceId = TriggerOccurrenceId.For(entry.Id, fireAt);
            if (!_occurrences.ContainsKey(occurrenceId))
            {
                _occurrences.Add(occurrenceId, new OccurrenceEntry(occurrenceId, entry.Id, tenantId, fireAt, now)
                {
                    Status = TriggerOccurrenceStatuses.Pending,
                    UpdatedAt = now,
                });
            }

            return Task.FromResult(new TriggerWriteResult(TriggerWriteStatus.Success, entry.ToModel()));
        }
    }

    public Task<IReadOnlyList<Trigger>> ListAsync(string tenantId, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<Trigger> items = _triggers.Values
                .Where(x => x.TenantId == tenantId)
                .OrderByDescending(x => x.FireAt).ThenByDescending(x => x.Id)
                .Take(limit)
                .Select(x => x.ToModel())
                .ToArray();
            return Task.FromResult(items);
        }
    }

    public Task<Trigger?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _triggers.TryGetValue(id, out var entry) && entry.TenantId == tenantId
                    ? entry.ToModel()
                    : null);
        }
    }

    public Task<TriggerWriteResult> CancelAsync(
        string tenantId, Guid id, long expectedVersion, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_triggers.TryGetValue(id, out var entry) || entry.TenantId != tenantId)
            {
                return Task.FromResult(new TriggerWriteResult(TriggerWriteStatus.NotFound));
            }
            if (entry.Version != expectedVersion)
            {
                return Task.FromResult(
                    new TriggerWriteResult(TriggerWriteStatus.VersionConflict, CurrentVersion: entry.Version));
            }
            if (entry.Status != TriggerStatuses.Scheduled)
            {
                return Task.FromResult(
                    new TriggerWriteResult(TriggerWriteStatus.InvalidState, entry.ToModel(), entry.Version));
            }

            entry.Status = TriggerStatuses.Cancelled;
            entry.Version++;
            entry.UpdatedAt = DateTime.UtcNow;
            foreach (var occurrence in _occurrences.Values.Where(x =>
                         x.TriggerId == id && x.Status == TriggerOccurrenceStatuses.Pending))
            {
                occurrence.Status = TriggerOccurrenceStatuses.SkippedCancelled;
                occurrence.UpdatedAt = entry.UpdatedAt;
            }

            return Task.FromResult(new TriggerWriteResult(TriggerWriteStatus.Success, entry.ToModel()));
        }
    }

    public Task<IReadOnlyList<TriggerOccurrence>?> OccurrencesAsync(
        string tenantId, Guid triggerId, TriggerOccurrencePosition? position, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_triggers.TryGetValue(triggerId, out var entry) || entry.TenantId != tenantId)
            {
                return Task.FromResult<IReadOnlyList<TriggerOccurrence>?>(null);
            }

            IReadOnlyList<TriggerOccurrence> items = _occurrences.Values
                .Where(x => x.TriggerId == triggerId && x.TenantId == tenantId)
                .Where(x => position is null
                            || KeysetCursor.FollowsPosition(x.ScheduledFor, x.Id, position.ScheduledFor, position.Id))
                .OrderByDescending(x => x.ScheduledFor).ThenByDescending(x => x.Id)
                .Take(limit)
                .Select(x => x.ToModel())
                .ToArray();
            return Task.FromResult<IReadOnlyList<TriggerOccurrence>?>(items);
        }
    }

    public Task<IReadOnlyList<TriggerClaim>> ClaimDueAsync(
        string workerId, DateTime now, int leaseSeconds, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var moment = Utc(now);
            var claimToken = Guid.NewGuid().ToString("N");
            var due = _occurrences.Values
                .Where(x => x.ScheduledFor <= moment
                            && (x.Status == TriggerOccurrenceStatuses.Pending
                                || (x.Status == TriggerOccurrenceStatuses.Claimed
                                    && x.ClaimExpiresAt < moment)))
                .OrderBy(x => x.ScheduledFor)
                .Take(limit)
                .ToArray();
            var claims = new List<TriggerClaim>(due.Length);
            foreach (var occurrence in due)
            {
                occurrence.Status = TriggerOccurrenceStatuses.Claimed;
                occurrence.ClaimOwner = workerId;
                occurrence.ClaimToken = claimToken;
                occurrence.ClaimExpiresAt = moment.AddSeconds(leaseSeconds);
                occurrence.Attempt++;
                occurrence.UpdatedAt = DateTime.UtcNow;
                claims.Add(new TriggerClaim(
                    occurrence.ToModel(), _triggers[occurrence.TriggerId].ToModel(), claimToken));
            }

            return Task.FromResult<IReadOnlyList<TriggerClaim>>(claims);
        }
    }

    public Task<bool> CompleteOccurrenceAsync(
        Guid occurrenceId, string claimToken, string status, Guid? rootRunId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_occurrences.TryGetValue(occurrenceId, out var occurrence)
                || occurrence.Status != TriggerOccurrenceStatuses.Claimed
                || !string.Equals(occurrence.ClaimToken, claimToken, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            var now = DateTime.UtcNow;
            occurrence.Status = status;
            occurrence.RootRunId = rootRunId ?? occurrence.RootRunId;
            occurrence.ClaimOwner = null;
            occurrence.ClaimToken = null;
            occurrence.ClaimExpiresAt = null;
            occurrence.UpdatedAt = now;
            if (_triggers.TryGetValue(occurrence.TriggerId, out var entry)
                && entry.Status == TriggerStatuses.Scheduled)
            {
                entry.Status = TriggerOccurrenceStatuses.ToTriggerStatus(status);
                entry.Version++;
                entry.UpdatedAt = now;
            }

            return Task.FromResult(true);
        }
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private sealed record Entry(
        Guid Id, string TenantId, string Name, string Description, Guid OrchestratorId,
        int OrchestratorRevision, string InputMapping, DateTime FireAt, int MisfireWindowSeconds,
        TriggerPrincipal Principal, DateTime CreatedAt)
    {
        public string Status { get; set; } = TriggerStatuses.Scheduled;
        public long Version { get; set; } = 1;
        public DateTime UpdatedAt { get; set; }

        public Trigger ToModel() => new(
            Id, TenantId, Name, Description, OrchestratorId, OrchestratorRevision, InputMapping,
            FireAt, MisfireWindowSeconds, Status, Principal, Version, CreatedAt, UpdatedAt);
    }

    private sealed record OccurrenceEntry(
        Guid Id, Guid TriggerId, string TenantId, DateTime ScheduledFor, DateTime CreatedAt)
    {
        public string Status { get; set; } = TriggerOccurrenceStatuses.Pending;
        public string? ClaimOwner { get; set; }
        public string? ClaimToken { get; set; }
        public DateTime? ClaimExpiresAt { get; set; }
        public int Attempt { get; set; }
        public Guid? RootRunId { get; set; }
        public DateTime UpdatedAt { get; set; }

        public TriggerOccurrence ToModel() =>
            new(Id, TriggerId, TenantId, ScheduledFor, Status, RootRunId, CreatedAt, UpdatedAt);
    }
}
