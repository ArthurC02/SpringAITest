using System.Text.Json;
using Backend.Api.Skills;
using Dapper;
using Npgsql;

namespace Backend.Api.Triggers;

/// <summary>
/// Dapper authority for O5 triggers. Parity contract with
/// <see cref="Data.InMemory.InMemoryTriggerRepository"/> lives on <see cref="ITriggerRepository"/>;
/// the two implementations are deliberately independent, so any change here needs the same change
/// there (see backend/AGENTS.md "Dapper ↔ InMemory parity is not automatic").
/// </summary>
public sealed class TriggerRepository(NpgsqlDataSource dataSource) : ITriggerRepository
{
    private const string TriggerColumns =
        "id Id,tenant_id TenantId,name Name,description Description,orchestrator_id OrchestratorId," +
        "orchestrator_revision OrchestratorRevision,input_mapping InputMapping,fire_at FireAt," +
        "misfire_window_seconds MisfireWindowSeconds,status Status,principal_snapshot::text Principal," +
        "version Version,created_at CreatedAt,updated_at UpdatedAt";

    private const string OccurrenceColumns =
        "id Id,trigger_id TriggerId,tenant_id TenantId,scheduled_for ScheduledFor,status Status," +
        "root_run_id RootRunId,created_at CreatedAt,updated_at UpdatedAt";

    public async Task<TriggerWriteResult> CreateAsync(
        string tenantId, TriggerPrincipal principal, TriggerCreateInput input, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var fireAt = Utc(input.FireAt);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TriggerRow>(new CommandDefinition(
            $"""
             INSERT INTO agent_trigger(id,tenant_id,name,description,orchestrator_id,orchestrator_revision,
               input_mapping,fire_at,misfire_window_seconds,created_by,principal_snapshot)
             VALUES(@id,@tenantId,@name,@description,@orchestratorId,@orchestratorRevision,
               @inputMapping,@fireAt,@misfireWindowSeconds,@createdBy,@principal::jsonb)
             -- New rows always insert with the default status='scheduled', matching the partial
             -- unique index's predicate (see DbBootstrap uq_agent_trigger_tenant_name), so a
             -- cancelled/fired/misfired row with the same name never blocks this insert.
             ON CONFLICT(tenant_id,name) WHERE status='scheduled' DO NOTHING
             RETURNING {TriggerColumns}
             """,
            new
            {
                id,
                tenantId,
                name = input.Name,
                description = input.Description,
                orchestratorId = input.OrchestratorId,
                orchestratorRevision = input.OrchestratorRevision,
                inputMapping = input.InputMappingCanonical,
                fireAt,
                misfireWindowSeconds = input.MisfireWindowSeconds,
                createdBy = principal.UserId,
                principal = JsonSerializer.Serialize(principal),
            },
            tx,
            cancellationToken: ct));
        if (row is null)
        {
            return new(TriggerWriteStatus.Duplicate);
        }

        // One-shot: exactly one occurrence, written in the same transaction as the definition, with
        // the derived stable identity (§6.3) so any re-derivation is a primary-key no-op.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO agent_trigger_occurrence(id,trigger_id,tenant_id,scheduled_for)
            VALUES(@id,@triggerId,@tenantId,@scheduledFor)
            ON CONFLICT(id) DO NOTHING
            """,
            new { id = TriggerOccurrenceId.For(id, fireAt), triggerId = id, tenantId, scheduledFor = fireAt },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(TriggerWriteStatus.Success, row.ToModel());
    }

    public async Task<IReadOnlyList<Trigger>> ListAsync(string tenantId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TriggerRow>(new CommandDefinition(
            $"SELECT {TriggerColumns} FROM agent_trigger WHERE tenant_id=@tenantId ORDER BY fire_at DESC,id DESC LIMIT @limit",
            new { tenantId, limit },
            cancellationToken: ct));
        return rows.Select(x => x.ToModel()).ToArray();
    }

    public async Task<Trigger?> GetAsync(string tenantId, Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<TriggerRow>(new CommandDefinition(
            $"SELECT {TriggerColumns} FROM agent_trigger WHERE tenant_id=@tenantId AND id=@id",
            new { tenantId, id },
            cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<TriggerWriteResult> CancelAsync(
        string tenantId, Guid id, long expectedVersion, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var current = await connection.QuerySingleOrDefaultAsync<TriggerRow>(new CommandDefinition(
            $"SELECT {TriggerColumns} FROM agent_trigger WHERE tenant_id=@tenantId AND id=@id FOR UPDATE",
            new { tenantId, id },
            tx,
            cancellationToken: ct));
        if (current is null)
        {
            return new(TriggerWriteStatus.NotFound);
        }
        if (current.Version != expectedVersion)
        {
            return new(TriggerWriteStatus.VersionConflict, CurrentVersion: current.Version);
        }
        if (current.Status != TriggerStatuses.Scheduled)
        {
            return new(TriggerWriteStatus.InvalidState, current.ToModel(), current.Version);
        }

        var row = await connection.QuerySingleAsync<TriggerRow>(new CommandDefinition(
            $"""
             UPDATE agent_trigger SET status='cancelled',version=version+1,updated_at=now()
             WHERE tenant_id=@tenantId AND id=@id RETURNING {TriggerColumns}
             """,
            new { tenantId, id },
            tx,
            cancellationToken: ct));
        // Only future claims stop: a lease already held by a worker keeps its snapshot and finishes
        // (§6.3 rollback semantics), and an already-fired occurrence is untouched.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_trigger_occurrence SET status='skipped_cancelled',updated_at=now() WHERE trigger_id=@id AND status='pending'",
            new { id },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(TriggerWriteStatus.Success, row.ToModel());
    }

    public async Task<IReadOnlyList<TriggerOccurrence>?> OccurrencesAsync(
        string tenantId, Guid triggerId, TriggerOccurrencePosition? position, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM agent_trigger WHERE tenant_id=@tenantId AND id=@triggerId)",
                new { tenantId, triggerId },
                cancellationToken: ct)))
        {
            return null;
        }

        var rows = await connection.QueryAsync<OccurrenceRow>(new CommandDefinition(
            $"""
             SELECT {OccurrenceColumns} FROM agent_trigger_occurrence
             WHERE tenant_id=@tenantId AND trigger_id=@triggerId
               AND (@cursorAt::timestamptz IS NULL OR (scheduled_for,id) < (@cursorAt,@cursorId))
             ORDER BY scheduled_for DESC,id DESC LIMIT @limit
             """,
            new
            {
                tenantId,
                triggerId,
                cursorAt = position is null ? (DateTime?)null : Utc(position.ScheduledFor),
                cursorId = position?.Id ?? Guid.Empty,
                limit,
            },
            cancellationToken: ct));
        return rows.Select(x => x.ToModel()).ToArray();
    }

    public async Task<TriggerClaimBatch> ClaimDueAsync(
        string workerId, int leaseSeconds, int limit, CancellationToken ct)
    {
        var claimToken = Guid.NewGuid().ToString("N");
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // SKIP LOCKED is the exclusivity: two concurrent pollers can never lease the same row, and
        // an expired lease is reclaimable without an operator action.
        //
        // The database clock is authoritative (same stance as OrchestratorRunRepository): the due
        // predicate, the expired-lease predicate, the new lease expiry and the returned instant all
        // read now(), which is *statement-stable* and is the same value the existing
        // updated_at=now() writes. clock_timestamp() would advance between those four uses and hand
        // back microsecond-different times for one claim.
        var rows = await connection.QueryAsync<ClaimRow>(new CommandDefinition(
            """
            WITH due AS (
              SELECT o.id FROM agent_trigger_occurrence o
              WHERE o.scheduled_for <= now()
                AND (o.status='pending' OR (o.status='claimed' AND o.claim_expires_at < now()))
              ORDER BY o.scheduled_for
              LIMIT @limit
              FOR UPDATE SKIP LOCKED)
            UPDATE agent_trigger_occurrence o
            SET status='claimed',claim_owner=@workerId,claim_token_sha256=@tokenHash,
                claim_expires_at=now() + make_interval(secs => @leaseSeconds),attempt=o.attempt+1,
                updated_at=now()
            FROM due,agent_trigger t
            WHERE o.id=due.id AND t.id=o.trigger_id
            RETURNING now() ClaimedAt,o.id OccurrenceId,o.trigger_id TriggerId,o.tenant_id TenantId,
              o.scheduled_for ScheduledFor,o.status OccurrenceStatus,o.root_run_id RootRunId,
              o.created_at OccurrenceCreatedAt,o.updated_at OccurrenceUpdatedAt,
              t.name Name,t.description Description,t.orchestrator_id OrchestratorId,
              t.orchestrator_revision OrchestratorRevision,t.input_mapping InputMapping,t.fire_at FireAt,
              t.misfire_window_seconds MisfireWindowSeconds,t.status TriggerStatus,
              t.principal_snapshot::text Principal,t.version Version,
              t.created_at TriggerCreatedAt,t.updated_at TriggerUpdatedAt
            """,
            new
            {
                limit,
                workerId,
                tokenHash = SkillHash.Sha256(claimToken),
                leaseSeconds = (double)leaseSeconds,
            },
            cancellationToken: ct));
        var claims = rows.ToArray();
        // An empty pass has no claim to resolve, so there is nothing for Now to be compared against;
        // the host clock only ever fills a value nobody reads.
        return new TriggerClaimBatch(
            claims.Length == 0 ? DateTime.UtcNow : claims[0].ClaimedAt,
            claims.Select(x => x.ToClaim(claimToken)).ToArray());
    }

    public async Task<bool> CompleteOccurrenceAsync(
        Guid occurrenceId, string claimToken, string status, Guid? rootRunId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var triggerId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            """
            UPDATE agent_trigger_occurrence
            SET status=@status,root_run_id=COALESCE(@rootRunId,root_run_id),
                claim_owner=NULL,claim_token_sha256=NULL,claim_expires_at=NULL,updated_at=now()
            WHERE id=@occurrenceId AND status='claimed' AND claim_token_sha256=@tokenHash
            RETURNING trigger_id
            """,
            new { occurrenceId, status, rootRunId, tokenHash = SkillHash.Sha256(claimToken) },
            tx,
            cancellationToken: ct));
        if (triggerId is null)
        {
            return false;
        }

        // A trigger cancelled while this occurrence was already leased stays cancelled: the guard
        // below never rewrites a terminal trigger status.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_trigger SET status=@triggerStatus,version=version+1,updated_at=now() WHERE id=@triggerId AND status='scheduled'",
            new { triggerId, triggerStatus = TriggerOccurrenceStatuses.ToTriggerStatus(status) },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private sealed record TriggerRow(
        Guid Id, string TenantId, string Name, string Description, Guid OrchestratorId,
        int OrchestratorRevision, string InputMapping, DateTime FireAt, int MisfireWindowSeconds,
        string Status, string Principal, long Version, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public Trigger ToModel() => new(
            Id, TenantId, Name, Description, OrchestratorId, OrchestratorRevision, InputMapping,
            FireAt, MisfireWindowSeconds, Status, TriggerPrincipals.Parse(Principal), Version,
            CreatedAt, UpdatedAt);
    }

    private sealed record OccurrenceRow(
        Guid Id, Guid TriggerId, string TenantId, DateTime ScheduledFor, string Status,
        Guid? RootRunId, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public TriggerOccurrence ToModel() =>
            new(Id, TriggerId, TenantId, ScheduledFor, Status, RootRunId, CreatedAt, UpdatedAt);
    }

    private sealed record ClaimRow(
        DateTime ClaimedAt,
        Guid OccurrenceId, Guid TriggerId, string TenantId, DateTime ScheduledFor,
        string OccurrenceStatus, Guid? RootRunId, DateTime OccurrenceCreatedAt,
        DateTime OccurrenceUpdatedAt, string Name, string Description, Guid OrchestratorId,
        int OrchestratorRevision, string InputMapping, DateTime FireAt, int MisfireWindowSeconds,
        string TriggerStatus, string Principal, long Version, DateTime TriggerCreatedAt,
        DateTime TriggerUpdatedAt)
    {
        public TriggerClaim ToClaim(string claimToken) => new(
            new TriggerOccurrence(
                OccurrenceId, TriggerId, TenantId, ScheduledFor, OccurrenceStatus, RootRunId,
                OccurrenceCreatedAt, OccurrenceUpdatedAt),
            new Trigger(
                TriggerId, TenantId, Name, Description, OrchestratorId, OrchestratorRevision,
                InputMapping, FireAt, MisfireWindowSeconds, TriggerStatus,
                TriggerPrincipals.Parse(Principal), Version, TriggerCreatedAt, TriggerUpdatedAt),
            claimToken);
    }
}

/// <summary>Single parse point for the persisted grant snapshot, shared by both implementations.</summary>
public static class TriggerPrincipals
{
    public static TriggerPrincipal Parse(string json)
        => JsonSerializer.Deserialize<TriggerPrincipal>(json)
           ?? new TriggerPrincipal(string.Empty, string.Empty, [], []);
}
