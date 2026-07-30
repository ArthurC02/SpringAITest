using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Backend.Api.AgentRuns;
using Backend.Api.Orchestrators;
using Backend.Api.Skills;
using Backend.Api.Workflows;
using Backend.Api.Contexts;
using Dapper;
using Npgsql;

namespace Backend.Api.OrchestratorRuns;

/// <summary>PostgreSQL authority for D5 root aggregates.  It deliberately never writes D3
/// <c>agent_run</c>; Workflow later creates child Agent runs with this root ID as provenance.</summary>
public sealed class OrchestratorRunRepository(NpgsqlDataSource dataSource, IContextRepository? contexts = null, ContextEnrichmentState? contextState = null) : IOrchestratorRunRepository
{
    public async Task<OrchestratorRunWriteResult> CreateAsync(string tenant, string user, string role, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> capabilities, Guid oid, string conversation, string message, string key, CancellationToken ct)
    {
        var requestHash = SkillHash.Sha256($"{oid:D}\0{conversation}\0{message}");
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var prior = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("SELECT " + Columns + " FROM orchestrator_run WHERE tenant_id=@tenant AND user_id=@user AND idempotency_key_sha256=@key FOR UPDATE", new { tenant, user, key = SkillHash.Sha256(key) }, tx, cancellationToken: ct));
        if (prior is not null) { await tx.RollbackAsync(ct); return prior.RequestHash == requestHash ? new(OrchestratorRunWriteStatus.Replay, ToResponse(prior), Dispatch: new(prior.CommandId ?? prior.Id), Replayed: true) : new(OrchestratorRunWriteStatus.Conflict, Message: "Idempotency-Key was used for a different request"); }
        var source = await LockSourcesAsync(c, tx, tenant, oid, ct);
        if (source is null) { await tx.RollbackAsync(ct); return new(OrchestratorRunWriteStatus.NotFound); }
        if (!source.Enabled) { await tx.RollbackAsync(ct); return new(OrchestratorRunWriteStatus.InvalidState, Message: "Orchestrator must be enabled and published"); }
        var id = Guid.NewGuid(); var commandId = Guid.NewGuid();
        var snapshot = AgentRunSnapshotBuilder.BuildOrchestratorRoot(id, tenant, user, role, groups, capabilities, oid, source.Revision, source.Definition, source.DefinitionSha256, source.Workflow, source.Workers, source.Verifier, message);
        try { await c.ExecuteAsync(new CommandDefinition("INSERT INTO orchestrator_run(id,tenant_id,user_id,caller_role,orchestrator_id,orchestrator_revision,conversation_id,workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,workflow_dispatch_snapshot,workflow_dispatch_snapshot_canonical,workflow_dispatch_snapshot_sha256,request_sha256,idempotency_key_sha256,status,state_version,deadline_at) VALUES(@id,@tenant,@user,@role,@oid,@revision,@conversation,@wid,@wrev,@snapshot::jsonb,@bytes,@hash,@snapshot::jsonb,@bytes,@hash,@requestHash,@keyHash,'queued',1,clock_timestamp()+make_interval(secs=>@timeout)); INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload) VALUES(@id,1,'run_created',@hash,'{}'::jsonb); INSERT INTO orchestrator_run_command(id,run_id,command_type) VALUES(@commandId,@id,'start')", new { id, commandId, tenant, user, role, oid, revision = source.Revision, conversation, wid = source.Workflow.WorkflowId, wrev = source.Workflow.Revision, snapshot = snapshot.StoredSnapshot, bytes = snapshot.CanonicalBytes, hash = snapshot.SnapshotHash, requestHash, keyHash = SkillHash.Sha256(key), timeout = snapshot.EffectiveTimeoutSeconds }, tx, cancellationToken: ct)); await tx.CommitAsync(ct); return new(OrchestratorRunWriteStatus.Success, ToResponse(await Load(c, tenant, user, id, ct)!), Dispatch: new(commandId)); }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            // A same-key concurrent insert observes no row before its insert, then loses the
            // unique-key race.  Re-read after rollback so a retried logical turn has exactly
            // the normal replay semantics instead of a spurious conflict.
            var raced = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                "SELECT " + Columns + " FROM orchestrator_run WHERE tenant_id=@tenant AND user_id=@user AND idempotency_key_sha256=@key",
                new { tenant, user, key = SkillHash.Sha256(key) }, cancellationToken: ct));
            if (raced is not null && raced.RequestHash == requestHash)
                return new(OrchestratorRunWriteStatus.Replay, ToResponse(raced), Dispatch: new(raced.CommandId ?? raced.Id), Replayed: true);
            return new(OrchestratorRunWriteStatus.Conflict, Message: "An active root run already exists for this conversation and Orchestrator");
        }
    }
    public async Task<OrchestratorRunResponse?> GetAsync(string tenant, string user, Guid id, CancellationToken ct) { await using var c = await dataSource.OpenConnectionAsync(ct); return ToResponse(await Load(c, tenant, user, id, ct)); }
    public async Task<OrchestratorRunActiveLookup> FindActiveAsync(string tenant, string user, string conversation, CancellationToken ct) { await using var c = await dataSource.OpenConnectionAsync(ct); var rows = (await c.QueryAsync<Row>(new CommandDefinition("SELECT " + Columns + " FROM orchestrator_run WHERE tenant_id=@tenant AND user_id=@user AND conversation_id=@conversation AND status IN ('queued','running','waiting_input') ORDER BY created_at DESC LIMIT 2", new { tenant, user, conversation }, cancellationToken: ct))).ToArray(); return rows.Length switch { 1 => new OrchestratorRunActiveLookup(ToResponse(rows[0])), > 1 => new OrchestratorRunActiveLookup(null, null, true), _ => new OrchestratorRunActiveLookup(null) }; }
    public async Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenant, string user, string key, OrchestratorRunReplayRequest replay, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var hash = SkillHash.Sha256(key);
        var operation = key.Split(':', 2)[0];
        if (operation is not ("chat" or "resume" or "switch"))
            return new OrchestratorRunActiveLookup(null, IsMismatch: true);
        // The command lookup is deliberately owner-scoped and returns the matching command,
        // not the start command projected by Columns.  A retry after a lost resume response
        // must never dispatch a stale start command.
        var rows = (await c.QueryAsync<Row>(new CommandDefinition(
            "SELECT " + Columns + " FROM orchestrator_run WHERE tenant_id=@tenant AND user_id=@user AND (idempotency_key_sha256=@hash OR EXISTS (SELECT 1 FROM orchestrator_run_command c WHERE c.run_id=orchestrator_run.id AND c.idempotency_key_sha256=@hash)) ORDER BY created_at DESC LIMIT 2",
            new { tenant, user, hash }, cancellationToken: ct))).ToArray();
        if (rows.Length == 1)
        {
            var row = rows[0];
            if (!string.Equals(row.Conversation, replay.ConversationId, StringComparison.Ordinal)
                || replay.OrchestratorId is Guid requestedOrchestrator && row.OrchestratorId != requestedOrchestrator)
                return new OrchestratorRunActiveLookup(null, IsMismatch: true);
            var startFingerprint = SkillHash.Sha256(replay.Message!);
            var durableStart = SkillHash.Sha256($"{row.OrchestratorId:D}\0{row.Conversation}\0{replay.Message}");
            var rootKeyMatch = await c.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT idempotency_key_sha256=@hash FROM orchestrator_run WHERE id=@id", new { id = row.Id, hash }, cancellationToken: ct));
            if (operation == "chat" && rootKeyMatch && string.Equals(durableStart, row.RequestHash, StringComparison.Ordinal))
                return new OrchestratorRunActiveLookup(ToResponse(row), row.CommandId);
            var commands = (await c.QueryAsync<ReplayCommand>(new CommandDefinition(
                "SELECT id Id,command_type Type,command_input_sha256 InputHash FROM orchestrator_run_command WHERE run_id=@runId AND idempotency_key_sha256=@hash",
                new { runId = row.Id, hash }, cancellationToken: ct))).ToArray();
            var command = operation == "resume" ? commands.FirstOrDefault(x => x.Type == "resume"
                && string.Equals(x.InputHash, startFingerprint, StringComparison.Ordinal)
                ) : null;
            return command is null
                ? new OrchestratorRunActiveLookup(null, IsMismatch: true)
                : new OrchestratorRunActiveLookup(ToResponse(row), command.Id);
        }
        return rows.Length switch
        {
            > 1 => new OrchestratorRunActiveLookup(null, null, true),
            _ => new OrchestratorRunActiveLookup(null),
        };
    }
    public async Task<OrchestratorRunEventsResponse?> EventsAsync(string tenant, string user, Guid id, long after, int limit, CancellationToken ct) { await using var c = await dataSource.OpenConnectionAsync(ct); if (await Load(c, tenant, user, id, ct) is null) return null; var e = (await c.QueryAsync<Event>(new CommandDefinition("SELECT sequence Sequence,event_type Type,snapshot_sha256 Hash,payload::text Payload,created_at At FROM orchestrator_run_event WHERE run_id=@id AND sequence>@after ORDER BY sequence LIMIT @limit", new { id, after, limit }, cancellationToken: ct))).Select(x => new OrchestratorRunEventResponse(x.Sequence, x.Type, x.Hash, JsonDocument.Parse(x.Payload).RootElement.Clone(), x.At)).ToArray(); return new(id, e, e.Length == 0 ? after : e[^1].Sequence); }
    public async Task<OrchestratorRunWriteResult> CancelAsync(string tenant, string user, Guid id, string? reason, string key, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT " + Columns + " FROM orchestrator_run WHERE id=@id AND tenant_id=@tenant AND user_id=@user FOR UPDATE",
            new { id, tenant, user }, tx, cancellationToken: ct));
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return new(OrchestratorRunWriteStatus.NotFound);
        }

        // A deadline terminalization is not a cancel command and must never be replayed or
        // mutated by a late cancellation request.
        if (row.Status == "timed_out")
        {
            await tx.RollbackAsync(ct);
            return new(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal");
        }

        var keyHash = SkillHash.Sha256(key);
        var prior = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT idempotency_key_sha256 FROM orchestrator_run_command WHERE run_id=@id AND command_type='cancel' FOR UPDATE",
            new { id }, tx, cancellationToken: ct));
        if (prior is not null)
        {
            await tx.RollbackAsync(ct);
            return string.Equals(prior, keyHash, StringComparison.Ordinal)
                ? new(OrchestratorRunWriteStatus.Replay, ToResponse(row), Replayed: true)
                : new(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal");
        }

        if (row.Status is "completed" or "failed" or "cancelled" or "timed_out")
        {
            await tx.RollbackAsync(ct);
            return new(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal");
        }

        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE orchestrator_run SET status='cancelled',cancel_requested_at=clock_timestamp(),state_version=state_version+1,updated_at=clock_timestamp() WHERE id=@id;"
            + " UPDATE orchestrator_run_child SET status='cancelled',updated_at=clock_timestamp() WHERE orchestrator_root_run_id=@id AND status IN ('queued','running');"
            + " UPDATE agent_run SET status='cancelled',cancel_requested_at=COALESCE(cancel_requested_at,clock_timestamp()),cancel_requested_by=@by,completed_at=COALESCE(completed_at,clock_timestamp()),updated_at=clock_timestamp() WHERE orchestrator_root_run_id=@id AND status IN ('queued','running','waiting_input');"
            + " INSERT INTO orchestrator_run_command(id,run_id,command_type,idempotency_key_sha256,completed_at) VALUES(@cancelCommand,@id,'cancel',@keyHash,clock_timestamp());"
            + " INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload) VALUES(@id,(SELECT COALESCE(MAX(sequence),0)+1 FROM orchestrator_run_event WHERE run_id=@id),'run_cancelled',@hash,@payload::jsonb)",
            new { id, cancelCommand = Guid.NewGuid(), keyHash, by = "root:" + id.ToString("D"), hash = row.Hash, payload = JsonSerializer.Serialize(new { reason }) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(OrchestratorRunWriteStatus.Success, ToResponse(await Load(c, tenant, user, id, ct)));
    }
    public Task<OrchestratorRunWriteResult> ResumeAsync(string tenant, string user, Guid id, string input, string key, CancellationToken ct)
        => ResumeCoreAsync(tenant, user, id, input, key, ct);

    private async Task<OrchestratorRunWriteResult> ResumeCoreAsync(string tenant, string user, Guid id, string input, string key, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT " + Columns + " FROM orchestrator_run WHERE id=@id AND tenant_id=@tenant AND user_id=@user FOR UPDATE",
            new { id, tenant, user }, tx, cancellationToken: ct));
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return new(OrchestratorRunWriteStatus.NotFound);
        }

        // The database clock is authoritative.  This must precede replay/state checks so an
        // expired waiting root cannot be re-queued by a late or replayed resume request.
        if (await ExpireDeadlineAsync(c, tx, tenant, user, id, ct))
        {
            await tx.CommitAsync(ct);
            return new(OrchestratorRunWriteStatus.Conflict, Message: "Root run deadline has expired");
        }

        var keyHash = SkillHash.Sha256(key);
        var inputHash = SkillHash.Sha256(input);
        var prior = await c.QuerySingleOrDefaultAsync<ResumeReplay>(new CommandDefinition(
            "SELECT id CommandId,command_input_sha256 InputHash FROM orchestrator_run_command WHERE run_id=@id AND command_type='resume' AND idempotency_key_sha256=@keyHash",
            new { id, keyHash }, tx, cancellationToken: ct));
        if (prior is not null)
        {
            await tx.RollbackAsync(ct);
            return string.Equals(prior.InputHash, inputHash, StringComparison.Ordinal)
                ? new(OrchestratorRunWriteStatus.Replay, ToResponse(row), Dispatch: new(prior.CommandId), Replayed: true)
                : new(OrchestratorRunWriteStatus.Conflict, Message: "Idempotency-Key was already used with different resume input");
        }

        if (row.Status != "waiting_input" || string.IsNullOrWhiteSpace(row.CheckpointRef) || row.CheckpointVersion < 1)
        {
            await tx.RollbackAsync(ct);
            return new(OrchestratorRunWriteStatus.InvalidState, Message: "Root run is not waiting for input");
        }

        var command = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { input });
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE orchestrator_run SET status='queued',state_version=state_version+1,updated_at=clock_timestamp() WHERE id=@id;"
            + " INSERT INTO orchestrator_run_command(id,run_id,command_type,idempotency_key_sha256,command_input,command_input_sha256) VALUES(@command,@id,'resume',@keyHash,@payload::jsonb,@inputHash);"
            + " INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload) VALUES(@id,(SELECT COALESCE(MAX(sequence),0)+1 FROM orchestrator_run_event WHERE run_id=@id),'root_resumed',@hash,@payload::jsonb)",
            new { id, command, keyHash, payload, inputHash, hash = row.Hash }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(OrchestratorRunWriteStatus.Success, ToResponse(await Load(c, tenant, user, id, ct)), Dispatch: new(command));
    }
    public async Task<string?> ExecutionArtifactAsync(string tenant, string user, Guid id, CancellationToken ct) { await using var c = await dataSource.OpenConnectionAsync(ct); var x = await c.QuerySingleOrDefaultAsync<(byte[] Bytes, string Hash)>(new CommandDefinition("SELECT execution_snapshot_canonical Bytes,snapshot_sha256 Hash FROM orchestrator_run WHERE id=@id AND tenant_id=@tenant AND user_id=@user", new { id, tenant, user }, cancellationToken: ct)); return x.Bytes is null ? null : JsonSerializer.Serialize(new { snapshot_hash = x.Hash, snapshot_canonical_base64 = Convert.ToBase64String(x.Bytes) }); }
    public async Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct)
    {
        var run = await GetAsync(tenant, user, runId, ct);
        if (run is null) return null;
        if (run.DeadlineAt <= DateTime.UtcNow) { await ExpireDeadlineAsync(tenant, user, runId, ct); return null; }
        var claim = await ClaimCommandCoreAsync(tenant, user, runId, commandId, workerId, leaseSeconds, ct);
        return claim is null ? null : claim with { DeadlineAt = run.DeadlineAt };
    }
    private async Task<OrchestratorRunCommandClaim?> ClaimCommandCoreAsync(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 256 || leaseSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(workerId));
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition("SELECT c.id CommandId,c.command_type CommandType,c.command_input::text CommandInput,c.completed_at CompletedAt,c.claim_expires_at ClaimExpiresAt,c.lease_generation LeaseGeneration,r.checkpoint_ref CheckpointRef,r.checkpoint_version CheckpointVersion,r.snapshot_sha256 SnapshotHash,r.execution_snapshot_canonical Snapshot FROM orchestrator_run_command c JOIN orchestrator_run r ON r.id=c.run_id WHERE c.id=@commandId AND c.run_id=@runId AND r.tenant_id=@tenant AND r.user_id=@user FOR UPDATE OF c,r", new { commandId, runId, tenant, user }, tx, cancellationToken: ct));
        if (row is null || row.CompletedAt is not null) { await tx.RollbackAsync(ct); return null; }
        if (row.ClaimExpiresAt is DateTime expiry && expiry > DateTime.UtcNow) { await tx.RollbackAsync(ct); return null; }
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var until = DateTime.UtcNow.AddSeconds(leaseSeconds); var generation = checked(row.LeaseGeneration + 1);
        await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run_command SET claim_owner=@worker,claim_token_sha256=@tokenHash,claim_expires_at=@until,dispatch_attempt=dispatch_attempt+1,lease_generation=@generation WHERE id=@commandId", new { worker = workerId, tokenHash = SkillHash.Sha256(token), until, generation, commandId }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run SET status=CASE WHEN status='queued' THEN 'running' ELSE status END,state_version=state_version+1,updated_at=clock_timestamp() WHERE id=@runId", new { runId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return new(commandId, runId, row.CommandType, token, until, generation, row.SnapshotHash, Convert.ToBase64String(row.Snapshot), ResumeInput(row.CommandInput), row.CommandType == "resume" ? row.CheckpointRef : null, row.CommandType == "resume" ? row.CheckpointVersion : null);
    }
    public async Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string tenant, string user, Guid runId, Guid commandId, string claimToken, long leaseGeneration, int leaseSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claimToken) || claimToken.Length > 256 || leaseGeneration < 1 || leaseSeconds is < 1 or > 300) return null;
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<ClaimRow>(new CommandDefinition("SELECT c.id CommandId,c.command_type CommandType,c.completed_at CompletedAt,c.claim_expires_at ClaimExpiresAt,c.lease_generation LeaseGeneration,r.snapshot_sha256 SnapshotHash,r.execution_snapshot_canonical Snapshot FROM orchestrator_run_command c JOIN orchestrator_run r ON r.id=c.run_id WHERE c.id=@commandId AND c.run_id=@runId AND r.tenant_id=@tenant AND r.user_id=@user FOR UPDATE OF c,r", new { commandId, runId, tenant, user }, tx, cancellationToken: ct));
        if (row is null || row.CompletedAt is not null || row.LeaseGeneration != leaseGeneration || row.ClaimExpiresAt <= DateTime.UtcNow) { await tx.RollbackAsync(ct); return null; }
        var until = DateTime.UtcNow.AddSeconds(leaseSeconds); var updated = await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run_command SET claim_expires_at=@until WHERE id=@commandId AND lease_generation=@leaseGeneration AND claim_token_sha256=@hash AND claim_expires_at>clock_timestamp()", new { commandId, leaseGeneration, hash = SkillHash.Sha256(claimToken), until }, tx, cancellationToken: ct));
        if (updated != 1) { await tx.RollbackAsync(ct); return null; }
        await tx.CommitAsync(ct); return new(commandId, runId, row.CommandType, claimToken, until, leaseGeneration, row.SnapshotHash, Convert.ToBase64String(row.Snapshot));
    }
    public async Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string tenant, string user, Guid runId, Guid commandId, string claimToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claimToken)) return OrchestratorRunDispatchCompleteStatus.Conflict;
        // Dispatch acknowledgement is not terminal completion.  Keep the fenced execution lease
        // so Workflow can publish the root terminal result, and make an expired worker reclaimable.
        await using var c = await dataSource.OpenConnectionAsync(ct); var count = await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run_command c SET dispatch_completed_at=clock_timestamp() WHERE c.id=@commandId AND c.run_id=@runId AND EXISTS(SELECT 1 FROM orchestrator_run r WHERE r.id=@runId AND r.tenant_id=@tenant AND r.user_id=@user) AND c.completed_at IS NULL AND c.claim_expires_at>clock_timestamp() AND c.claim_token_sha256=@hash", new { commandId, runId, tenant, user, hash = SkillHash.Sha256(claimToken) }, cancellationToken: ct));
        if (count == 1) return OrchestratorRunDispatchCompleteStatus.Success;
        var exists = await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM orchestrator_run_command c JOIN orchestrator_run r ON r.id=c.run_id WHERE c.id=@commandId AND c.run_id=@runId AND r.tenant_id=@tenant AND r.user_id=@user)", new { commandId, runId, tenant, user }, cancellationToken: ct)); return exists ? OrchestratorRunDispatchCompleteStatus.Conflict : OrchestratorRunDispatchCompleteStatus.NotFound;
    }
    public async Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string workerId, int limit, int leaseSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 256 || limit is < 1 or > 100 || leaseSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(workerId));
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var expired = (await c.QueryAsync<RecoveryCandidate>(new CommandDefinition("SELECT DISTINCT c.id CommandId,c.run_id RunId,r.tenant_id TenantId,r.user_id UserId,r.caller_role Role FROM orchestrator_run_command c JOIN orchestrator_run r ON r.id=c.run_id WHERE c.completed_at IS NULL AND r.deadline_at<=clock_timestamp() AND r.status IN ('queued','running','waiting_input')", cancellationToken: ct))).AsList();
        foreach (var item in expired) await ExpireDeadlineAsync(item.TenantId, item.UserId, item.RunId, ct);
        var candidates = (await c.QueryAsync<RecoveryCandidate>(new CommandDefinition("SELECT c.id CommandId,c.run_id RunId,r.tenant_id TenantId,r.user_id UserId,r.caller_role Role FROM orchestrator_run_command c JOIN orchestrator_run r ON r.id=c.run_id WHERE c.completed_at IS NULL AND (c.claim_expires_at IS NULL OR c.claim_expires_at<=clock_timestamp()) AND r.deadline_at>clock_timestamp() AND r.status IN ('queued','running') ORDER BY c.created_at LIMIT @limit", new { limit = limit + 1 }, cancellationToken: ct))).AsList();
        var items = new List<OrchestratorRunRecoveryItem>();
        foreach (var candidate in candidates.Take(limit))
        { var claim = await ClaimCommandAsync(candidate.TenantId, candidate.UserId, candidate.RunId, candidate.CommandId, workerId, leaseSeconds, ct); if (claim is not null) items.Add(new(candidate.TenantId, candidate.UserId, candidate.Role, claim)); }
        return new(items, candidates.Count > limit);
    }
    public async Task<OrchestratorChildResponse?> CreateChildAsync(string tenant, string user, Guid rootRunId, OrchestratorChildCreateRequest request, CancellationToken ct)
    {
        if (contextState?.Enabled is true && contexts is not null)
        {
            var stored = await contexts.GetLatestReadyForRunAsync(tenant, user, rootRunId, ct);
            request = request with { TaskEnvelope = ContextTaskEnvelopeProjection.ApplyIfAvailable(request.TaskEnvelope, stored, request.RunKind?.Trim() ?? "") };
        }
        var (task, kind, envelope) = OrchestratorTaskEnvelope.ValidateChild(request);
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var root = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("SELECT " + Columns + " FROM orchestrator_run WHERE id=@rootRunId AND tenant_id=@tenant AND user_id=@user AND status IN ('queued','running') FOR UPDATE", new { rootRunId, tenant, user }, tx, cancellationToken: ct));
        if (root is null) { await tx.RollbackAsync(ct); return null; }
        using var snapshot = JsonDocument.Parse(root.Snapshot); var value = snapshot.RootElement; var pin = FindPin(value, kind, request.AgentId, request.AgentRevision); if (pin is null || request.TokenCap != pin.Value.TokenCap) { await tx.RollbackAsync(ct); return null; }
        var limits = value.GetProperty("limits"); var maxChildren = limits.GetProperty("max_child_runs").GetInt32(); var maxConcurrency = limits.GetProperty("max_concurrency").GetInt32();
        var childCount = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM orchestrator_run_child WHERE orchestrator_root_run_id=@rootRunId", new { rootRunId }, tx, cancellationToken: ct));
        var activeCount = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM orchestrator_run_child WHERE orchestrator_root_run_id=@rootRunId AND status IN ('queued','running')", new { rootRunId }, tx, cancellationToken: ct));
        if (childCount >= maxChildren || activeCount >= maxConcurrency) { await tx.RollbackAsync(ct); return null; }
        var source = await LoadChildSourceAsync(c, tx, tenant, request.AgentId, request.AgentRevision, pin.Value, ct);
        if (source is null) { await tx.RollbackAsync(ct); return null; }
        var caller = value.GetProperty("caller"); var callerRole = caller.GetProperty("role").GetString()!;
        var groups = Strings(caller.GetProperty("groups"));
        var capabilities = Strings(caller.GetProperty("tool_grants")).Select(x => "tool.use:" + x)
            .Concat(Strings(caller.GetProperty("knowledge_grants")).Select(x => "knowledge.read:" + x)).ToArray();
        if (AgentRunSnapshotBuilder.ValidateExecutionContract(source.Agent, source.Workflow, source.Skills, tenant, user, callerRole, groups).Count > 0) { await tx.RollbackAsync(ct); return null; }
        var agentRunId = Guid.NewGuid(); var commandId = Guid.NewGuid();
        var agentSnapshot = AgentRunSnapshotBuilder.Build(agentRunId, tenant, user, callerRole, groups, capabilities, source.Agent, source.Workflow, source.Skills, kind == "verifier" ? "orchestrator-verifier" : "orchestrator-worker", pin.Value.TokenCap, new OrchestratorChildSnapshotProvenance(rootRunId, task, request.Attempt));
        var commandInput = JsonSerializer.Serialize(new { message = envelope.Objective, task_envelope = JsonDocument.Parse(envelope.Canonical).RootElement });
        if (!AgentRunCommandInput.TryParseAndValidate(commandInput, "start", 0, 0, null, AgentRunCommandValidationMode.DirectClaim, out var validatedInput, out _)) { await tx.RollbackAsync(ct); throw new InvalidOperationException("Generated child command input is invalid"); }
        var commandHash = AgentRunCommandInput.CanonicalSha256(validatedInput, "start");
        var id = Guid.NewGuid(); var artifact = JsonSerializer.Serialize(new { orchestrator_root_run_id = rootRunId, task_id = task, attempt = request.Attempt, run_kind = kind, root_snapshot_hash = root.Hash, agent_snapshot_hash = pin.Value.Hash, agent_run_snapshot_hash = agentSnapshot.SnapshotHash, agent_id = request.AgentId, agent_revision = request.AgentRevision, workflow_id = pin.Value.WorkflowId, workflow_revision = pin.Value.WorkflowRevision, command_input = JsonDocument.Parse(commandInput).RootElement });
        try
        {
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO agent_run(id,tenant_id,root_run_id,parent_run_id,task_id,run_kind,user_id,caller_role,agent_id,agent_revision,workflow_id,workflow_revision,execution_snapshot,execution_snapshot_canonical,snapshot_sha256,status,state_version,deadline_at,orchestrator_root_run_id) VALUES(@agentRunId,@tenant,@agentRunId,NULL,@task,@kind,@user,@callerRole,@agentId,@agentRevision,@workflowId,@workflowRevision,@snapshot::jsonb,@snapshotBytes,@snapshotHash,'queued',1,clock_timestamp()+make_interval(secs=>@timeout),@rootRunId)", new { agentRunId, tenant, task, kind, user, callerRole, agentId = request.AgentId, agentRevision = request.AgentRevision, workflowId = pin.Value.WorkflowId, workflowRevision = pin.Value.WorkflowRevision, snapshot = agentSnapshot.StoredSnapshot, snapshotBytes = agentSnapshot.CanonicalBytes, snapshotHash = agentSnapshot.SnapshotHash, timeout = agentSnapshot.EffectiveTimeoutSeconds, rootRunId }, tx, cancellationToken: ct));
            foreach (var skill in source.Skills) { var position = source.Agent.SkillBindings.Single(x => x.Skill == skill.Name).Position; await c.ExecuteAsync(new CommandDefinition("INSERT INTO agent_run_skill(run_id,position,skill_id,skill_revision,skill_name,kind,definition_sha256,package_sha256) VALUES(@agentRunId,@position,@skillId,@revision,@name,@kind,@definitionHash,@packageHash)", new { agentRunId, position, skillId = skill.SkillId, revision = skill.Revision, name = skill.Name, kind = skill.Kind, definitionHash = skill.DefinitionSha256, packageHash = skill.PackageSha256 }, tx, cancellationToken: ct)); }
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO agent_run_event(run_id,sequence,event_id,event_type,snapshot_sha256,payload) VALUES(@agentRunId,1,@eventId,'run_created',@snapshotHash,@payload::jsonb); UPDATE agent_run SET latest_event_sequence=1 WHERE id=@agentRunId; INSERT INTO agent_run_command(id,tenant_id,user_id,run_id,command_type,idempotency_key_sha256,request_sha256,command_input,command_input_sha256,dispatch_attempts) VALUES(@commandId,@tenant,@user,@agentRunId,'start',@keyHash,@requestHash,@commandInput::jsonb,@commandHash,0)", new { agentRunId, eventId = Guid.NewGuid(), snapshotHash = agentSnapshot.SnapshotHash, payload = JsonSerializer.Serialize(new { orchestrator_root_run_id = rootRunId, task_id = task, attempt = request.Attempt }), commandId, tenant, user, keyHash = SkillHash.Sha256($"orchestrator-child:{rootRunId:D}:{task}:{request.Attempt}:{kind}"), requestHash = SkillHash.Sha256(envelope.Canonical), commandInput, commandHash }, tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO orchestrator_run_child(id,orchestrator_root_run_id,task_id,attempt,run_kind,agent_id,agent_revision,workflow_id,workflow_revision,agent_snapshot_sha256,agent_run_id,command_id,task_envelope,dispatch_artifact,status) VALUES(@id,@rootRunId,@task,@attempt,@kind,@agentId,@agentRevision,@workflowId,@workflowRevision,@hash,@agentRunId,@commandId,@envelope::jsonb,@artifact::jsonb,'queued'); INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload) VALUES(@rootRunId,(SELECT COALESCE(MAX(sequence),0)+1 FROM orchestrator_run_event WHERE run_id=@rootRunId),'child_created',@rootHash,@eventPayload::jsonb)", new { id, rootRunId, task, attempt = request.Attempt, kind, agentId = request.AgentId, agentRevision = request.AgentRevision, workflowId = pin.Value.WorkflowId, workflowRevision = pin.Value.WorkflowRevision, hash = pin.Value.Hash, agentRunId, commandId, envelope = envelope.Canonical, artifact, rootHash = root.Hash, eventPayload = OrchestratorRunEvents.ChildCreated(id, agentRunId, task, request.Attempt, kind) }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct); return new(id, rootRunId, task, request.Attempt, kind, request.AgentId, request.AgentRevision, pin.Value.WorkflowId, pin.Value.WorkflowRevision, pin.Value.Hash, "queued", agentRunId, commandId);
        }
        catch (PostgresException e) when (e.SqlState == "23505") { await tx.RollbackAsync(ct); return null; }
    }
    public async Task<OrchestratorChildStatusResponse?> GetChildAsync(string tenant, string user, Guid rootRunId, Guid childId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<ChildStatusRow>(new CommandDefinition("SELECT ch.id Id,ch.orchestrator_root_run_id RootRunId,ch.task_id TaskId,ch.attempt Attempt,ch.run_kind RunKind,ch.agent_id AgentId,ch.agent_revision AgentRevision,ch.workflow_id WorkflowId,ch.workflow_revision WorkflowRevision,ch.agent_snapshot_sha256 AgentSnapshotHash,ch.agent_run_id AgentRunId,COALESCE(ar.status,ch.status) Status,ar.result::text Result,ar.error_code ErrorCode,ar.error_message ErrorMessage FROM orchestrator_run_child ch JOIN orchestrator_run root ON root.id=ch.orchestrator_root_run_id LEFT JOIN agent_run ar ON ar.id=ch.agent_run_id WHERE ch.id=@childId AND ch.orchestrator_root_run_id=@rootRunId AND root.tenant_id=@tenant AND root.user_id=@user", new { tenant, user, rootRunId, childId }, cancellationToken: ct));
        if (row is null || row.AgentRunId is null) return null;
        var output = row.Result is null ? EmptyObject() : JsonDocument.Parse(row.Result).RootElement.Clone();
        var citations = output.ValueKind == JsonValueKind.Object && output.TryGetProperty("citations", out var cits) && cits.ValueKind == JsonValueKind.Array ? cits.Clone() : EmptyArray();
        return new(row.Id, row.RootRunId, row.TaskId, row.Attempt, row.RunKind, row.AgentId, row.AgentRevision, row.WorkflowId, row.WorkflowRevision, row.AgentSnapshotHash, row.AgentRunId.Value, row.Status, output, citations, row.ErrorCode, row.ErrorMessage);
    }
    public async Task<OrchestratorRunWriteResult> TransitionAsync(string tenant, string user, Guid rootRunId, OrchestratorRootTransitionRequest request, CancellationToken ct)
    {
        if (request.ExpectedStateVersion < 1 || request.LeaseGeneration < 1 || string.IsNullOrWhiteSpace(request.ClaimToken) || request.ClaimToken.Length > 256 || request.ToStatus is not ("waiting_input" or "completed" or "failed" or "cancelled" or "timed_out") || !WithinJson(request.Result, 1024 * 1024) || !ValidEvents(request.Events) || (request.ToStatus == "waiting_input" && (string.IsNullOrWhiteSpace(request.CheckpointRef) || request.CheckpointVersion is null || request.CheckpointVersion < 1))) { return new(OrchestratorRunWriteStatus.InvalidState, Message: "Invalid root transition"); }
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var root = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("SELECT " + Columns + " FROM orchestrator_run WHERE id=@rootRunId AND tenant_id=@tenant AND user_id=@user FOR UPDATE", new { rootRunId, tenant, user }, tx, cancellationToken: ct));
        if (root is null) { await tx.RollbackAsync(ct); return new(OrchestratorRunWriteStatus.NotFound); }
        if (await ExpireDeadlineAsync(c, tx, tenant, user, rootRunId, ct)) { await tx.CommitAsync(ct); return new(OrchestratorRunWriteStatus.Conflict, Message: "Root run deadline has expired"); }
        if (root.Status is "completed" or "failed" or "cancelled" || root.Version != request.ExpectedStateVersion) { await tx.RollbackAsync(ct); return new(OrchestratorRunWriteStatus.Conflict, ToResponse(root), "Root state changed"); }
        var command = await c.QuerySingleOrDefaultAsync<RootLeaseRow>(new CommandDefinition("SELECT claim_token_sha256 TokenHash,claim_expires_at ExpiresAt,lease_generation Generation FROM orchestrator_run_command WHERE run_id=@rootRunId AND command_type IN ('start','resume') AND completed_at IS NULL ORDER BY created_at DESC LIMIT 1 FOR UPDATE", new { rootRunId }, tx, cancellationToken: ct));
        if (command is null || command.Generation != request.LeaseGeneration || command.ExpiresAt <= DateTime.UtcNow || !string.Equals(command.TokenHash, SkillHash.Sha256(request.ClaimToken), StringComparison.Ordinal)) { await tx.RollbackAsync(ct); return new(OrchestratorRunWriteStatus.Conflict, ToResponse(root), "Root command lease changed"); }
        var waiting = request.ToStatus == "waiting_input"; var changed = await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run SET status=@status,state_version=state_version+1,result=@result::jsonb,error_code=@errorCode,error_message=@errorMessage,checkpoint_ref=CASE WHEN @waiting THEN @checkpointRef ELSE checkpoint_ref END,checkpoint_version=CASE WHEN @waiting THEN @checkpointVersion ELSE checkpoint_version END,completed_at=CASE WHEN @waiting THEN NULL ELSE clock_timestamp() END,updated_at=clock_timestamp() WHERE id=@rootRunId AND state_version=@expected AND status IN ('queued','running')", new { rootRunId, status = request.ToStatus, expected = request.ExpectedStateVersion, result = JsonText(request.Result) ?? "null", errorCode = Trim(request.ErrorCode, 100), errorMessage = Trim(request.ErrorMessage, 500), waiting, checkpointRef = request.CheckpointRef, checkpointVersion = request.CheckpointVersion ?? 0 }, tx, cancellationToken: ct));
        if (changed != 1) { await tx.RollbackAsync(ct); return new(OrchestratorRunWriteStatus.Conflict, ToResponse(root), "Root state changed"); }
        foreach (var e in request.Events ?? Array.Empty<OrchestratorRootEventAppend>()) await AppendRootEventAsync(c, tx, rootRunId, root.Hash, e.EventType!, JsonText(e.Payload) ?? "{}", ct);
        await AppendRootEventAsync(c, tx, rootRunId, root.Hash, waiting ? "root_waiting_input" : "root_terminal", OrchestratorRunEvents.RootTerminal(request.ToStatus!), ct);
        await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run_command SET completed_at=clock_timestamp(),claim_owner=NULL,claim_token_sha256=NULL,claim_expires_at=NULL WHERE run_id=@rootRunId AND command_type IN ('start','resume') AND lease_generation=@generation AND claim_token_sha256=@hash", new { rootRunId, generation = request.LeaseGeneration, hash = SkillHash.Sha256(request.ClaimToken) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct); return new(OrchestratorRunWriteStatus.Success, await GetAsync(tenant, user, rootRunId, ct));
    }
    public async Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string tenant, string user, Guid rootRunId, OrchestratorContextAcquireRequest request, CancellationToken ct)
    {
        if (request.ContextRound < 1 || request.CurrentContext is not { ValueKind: JsonValueKind.Object } || request.CurrentContext.Value.GetRawText().Length > 65_536) return null;
        await using var c = await dataSource.OpenConnectionAsync(ct); var root = await Load(c, tenant, user, rootRunId, ct); if (root is null) return null;
        using var snapshot = JsonDocument.Parse(root.Snapshot); var authority = snapshot.RootElement.GetProperty("authority");
        var tools = Strings(authority.GetProperty("context_tools")).OrderBy(x => x, StringComparer.Ordinal).ToArray(); var knowledge = Strings(authority.GetProperty("knowledge_sources")).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!tools.SequenceEqual((request.AllowedTools ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal) || !knowledge.SequenceEqual((request.AllowedKnowledgeSources ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal)) return null;
        // No implicit connector exists in Backend.  Do not echo caller/model context or invent
        // provenance: an unavailable server-owned adapter is an explicit, safe insufficiency.
        var missing = tools.Select(x => "context-tool:" + x).Concat(knowledge.Select(x => "knowledge-source:" + x)).DefaultIfEmpty("context-adapter-unavailable").ToArray();
        var clarified = request.CurrentContext.Value.TryGetProperty("user_input", out var clarification) && clarification.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(clarification.GetString());
        // Flag off must stay bit-identical to the pre-E1 contract, including the clarification
        // short circuit; only the enabled path routes clarification through a new revision.
        if (contextState?.Enabled is not true)
            return clarified
                ? new(true, request.CurrentContext.Value.Clone(), Array.Empty<JsonElement>(), Array.Empty<string>())
                : new(false, EmptyObject(), Array.Empty<JsonElement>(), missing);
        if (clarified) return new(false, EmptyObject(), Array.Empty<JsonElement>(), new[] { "clarification-revision-required" });
        var stored = contexts is null ? null : await contexts.GetLatestReadyForRunAsync(tenant, user, rootRunId, ct);
        if (stored is not null && ContextAcquireProjection.Build(stored) is { } projection) return projection;
        return new(false, EmptyObject(), Array.Empty<JsonElement>(), missing);
    }
    public async Task<OrchestratorContextRequestResponse?> GetOrCreateContextRequestAsync(string tenant, string user, Guid rootRunId, Guid childId, CancellationToken ct)
    {
        if (contextState?.Enabled is not true) return null;
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var child = await LoadContextChildAsync(c, tx, tenant, user, rootRunId, childId, ct);
        if (child is null) { await tx.RollbackAsync(ct); return null; }
        var existing = await LoadContextRequestAsync(c, tx, rootRunId, childId, null, true, ct);
        if (existing is not null) { await tx.CommitAsync(ct); return existing.ToResponse(); }
        var role = child.RunKind == "verifier" ? "verifier" : "worker";
        var baseRef = ContextRefFromEnvelope(child.TaskEnvelope);
        var now = DateTime.UtcNow; var id = Guid.NewGuid(); var contextId = Guid.NewGuid();
        try
        {
            await c.ExecuteAsync(new CommandDefinition("INSERT INTO context_request(id,orchestrator_root_run_id,orchestrator_child_id,context_id,task_id,role,base_context_ref,version,created_at,updated_at) VALUES(@id,@rootRunId,@childId,@contextId,@taskId,@role,@base::jsonb,1,@now,@now)", new { id, rootRunId, childId, contextId, taskId = child.TaskId, role, @base = JsonSerializer.Serialize(baseRef), now }, tx, cancellationToken: ct));
            await AppendRootEventAsync(c, tx, rootRunId, child.RootSnapshotHash, "context.requested", JsonSerializer.Serialize(new { context_request_id = id, child_id = childId, task_id = child.TaskId, role }), ct);
            await tx.CommitAsync(ct);
            return new OrchestratorContextRequestResponse(id, rootRunId, childId, child.TaskId, role, contextId, baseRef, null, 1, now, now);
        }
        catch (PostgresException e) when (e.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);
            return await GetContextRequestAsync(tenant, user, rootRunId, childId,
                await c.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT id FROM context_request WHERE orchestrator_root_run_id=@rootRunId AND orchestrator_child_id=@childId", new { rootRunId, childId }, cancellationToken: ct)), ct);
        }
    }
    public async Task<OrchestratorContextRequestResponse?> GetContextRequestAsync(string tenant, string user, Guid rootRunId, Guid childId, Guid requestId, CancellationToken ct)
    {
        if (contextState?.Enabled is not true) return null;
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var owned = await c.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM orchestrator_run_child ch JOIN orchestrator_run root ON root.id=ch.orchestrator_root_run_id WHERE ch.id=@childId AND ch.orchestrator_root_run_id=@rootRunId AND root.tenant_id=@tenant AND root.user_id=@user)", new { childId, rootRunId, tenant, user }, cancellationToken: ct));
        return owned ? (await LoadContextRequestAsync(c, null, rootRunId, childId, requestId, false, ct))?.ToResponse() : null;
    }
    public async Task<OrchestratorContextDeltaResult> AppendContextDeltaAsync(string tenant, string user, Guid rootRunId, Guid childId, Guid requestId, long expectedVersion, OrchestratorContextDeltaRequest delta, CancellationToken ct)
    {
        if (contextState?.Enabled is not true || contexts is not ContextRepository contextRepository) return new(OrchestratorContextDeltaStatus.NotFound);
        await using var c = await dataSource.OpenConnectionAsync(ct); await using var tx = await c.BeginTransactionAsync(ct);
        var child = await LoadContextChildAsync(c, tx, tenant, user, rootRunId, childId, ct);
        var stored = child is null ? null : await LoadContextRequestAsync(c, tx, rootRunId, childId, requestId, true, ct);
        if (child is null || stored is null) { await tx.RollbackAsync(ct); return new(OrchestratorContextDeltaStatus.NotFound); }
        if (stored.Version != expectedVersion) { await tx.RollbackAsync(ct); return new(OrchestratorContextDeltaStatus.Conflict); }
        var views = delta.Views ?? Array.Empty<ContextViewInput>();
        if (views.Count != 1 || !string.Equals(views[0].ViewType, stored.Role, StringComparison.Ordinal)) throw new ArgumentException("Context delta must contain exactly the task role view");
        var revision = await contextRepository.CreateRevisionAsync(c, tx, tenant, user, stored.ContextId, new ContextRevisionSubmitRequest(rootRunId, delta.Definition, delta.Evidence, views, delta.Measurements, delta.AsOf, delta.ExpiresAt), ct);
        var next = checked(stored.Version + 1);
        var updated = await c.ExecuteAsync(new CommandDefinition("UPDATE context_request SET current_context_ref=@current::jsonb,version=@next,updated_at=clock_timestamp() WHERE id=@requestId AND version=@expected", new { requestId, current = JsonSerializer.Serialize(revision.Revision.ContextRef), next, expected = expectedVersion }, tx, cancellationToken: ct));
        if (updated != 1) { await tx.RollbackAsync(ct); return new(OrchestratorContextDeltaStatus.Conflict); }
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO context_delta(id,context_request_id,version,context_id,revision) VALUES(@id,@requestId,@next,@contextId,@revision)", new { id = Guid.NewGuid(), requestId, next, contextId = stored.ContextId, revision = revision.Revision.Revision }, tx, cancellationToken: ct));
        await AppendRootEventAsync(c, tx, rootRunId, child.RootSnapshotHash, "context.delta_applied", JsonSerializer.Serialize(new { context_request_id = requestId, child_id = childId, task_id = child.TaskId, revision = revision.Revision.Revision, status = revision.Revision.Status, readiness = revision.Revision.Readiness }), ct);
        await tx.CommitAsync(ct);
        return new(OrchestratorContextDeltaStatus.Success, revision.Revision, next);
    }
    private static async Task<ContextChildRow?> LoadContextChildAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenant, string user, Guid rootRunId, Guid childId, CancellationToken ct)
        => await c.QuerySingleOrDefaultAsync<ContextChildRow>(new CommandDefinition("SELECT ch.task_id TaskId,ch.run_kind RunKind,ch.task_envelope::text TaskEnvelope,root.snapshot_sha256 RootSnapshotHash FROM orchestrator_run_child ch JOIN orchestrator_run root ON root.id=ch.orchestrator_root_run_id WHERE ch.id=@childId AND ch.orchestrator_root_run_id=@rootRunId AND root.tenant_id=@tenant AND root.user_id=@user FOR UPDATE OF ch,root", new { childId, rootRunId, tenant, user }, tx, cancellationToken: ct));
    private static async Task<ContextRequestRow?> LoadContextRequestAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid rootRunId, Guid childId, Guid? requestId, bool forUpdate, CancellationToken ct)
        => await c.QuerySingleOrDefaultAsync<ContextRequestRow>(new CommandDefinition("SELECT id Id,orchestrator_root_run_id RootRunId,orchestrator_child_id ChildId,context_id ContextId,task_id TaskId,role Role,base_context_ref::text BaseContextRef,current_context_ref::text CurrentContextRef,version Version,created_at CreatedAt,updated_at UpdatedAt FROM context_request WHERE orchestrator_root_run_id=@rootRunId AND orchestrator_child_id=@childId AND (@requestId IS NULL OR id=@requestId)" + (forUpdate ? " FOR UPDATE" : ""), new { rootRunId, childId, requestId }, tx, cancellationToken: ct));
    private static ContextRef? ContextRefFromEnvelope(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        if (!document.RootElement.TryGetProperty("context_ref", out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("context_id", out var contextId) || !Guid.TryParse(contextId.GetString(), out var id)
            || !value.TryGetProperty("revision", out var revision) || !revision.TryGetInt32(out var number) || number < 1
            || !value.TryGetProperty("view_id", out var viewId) || !Guid.TryParse(viewId.GetString(), out var view)) return null;
        return new ContextRef(id, number, view);
    }
    private static ContextRef? ContextRefFromJson(string? value)
        => string.IsNullOrWhiteSpace(value) || value == "null" ? null : JsonSerializer.Deserialize<ContextRef>(value);
    private static async Task AppendRootEventAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid rootRunId, string snapshotHash, string type, string payload, CancellationToken ct)
        => await c.ExecuteAsync(new CommandDefinition("INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload) VALUES(@rootRunId,(SELECT COALESCE(MAX(sequence),0)+1 FROM orchestrator_run_event WHERE run_id=@rootRunId),@type,@snapshotHash,@payload::jsonb)", new { rootRunId, type, snapshotHash, payload }, tx, cancellationToken: ct));
    private static async Task<ChildSource?> LoadChildSourceAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenant, Guid agentId, int revision, (Guid WorkflowId, int WorkflowRevision, string Hash, int TokenCap) pin, CancellationToken ct)
    {
        var agent = await c.QuerySingleOrDefaultAsync<AgentRow>(new CommandDefinition("SELECT a.name Name,r.canonical_definition Definition,r.definition_sha256 DefinitionSha256,r.runtime_workflow_id WorkflowId,r.runtime_workflow_revision WorkflowRevision,r.prompt_manifest_revision PromptManifestRevision,r.prompt_manifest_sha256 PromptManifestSha256 FROM agent a JOIN agent_revision r ON r.agent_id=a.id AND r.revision=@revision WHERE a.id=@agentId AND a.tenant_id=@tenant AND a.enabled AND a.published_revision=@revision AND r.status='published' FOR SHARE OF a,r", new { agentId, revision, tenant }, tx, cancellationToken: ct));
        if (agent is null || agent.Definition is null || agent.WorkflowId != pin.WorkflowId || agent.WorkflowRevision != pin.WorkflowRevision || !SkillHash.MatchesSha256(agent.Definition, agent.DefinitionSha256) || !string.Equals(agent.DefinitionSha256, pin.Hash, StringComparison.Ordinal)) return null;
        var workflow = await c.QuerySingleOrDefaultAsync<WorkflowSourceRow>(new CommandDefinition("SELECT wr.schema_version SchemaVersion,wr.definition_canonical Definition,wr.definition_sha256 DefinitionSha256,wr.compiler_contract_version CompilerContractVersion FROM workflow w JOIN workflow_revision wr ON wr.workflow_id=w.id AND wr.revision=@revision WHERE w.id=@workflowId AND w.enabled AND w.kind='agent-runtime' AND w.tenant_id IN (@tenant,@systemTenant) FOR SHARE OF w,wr", new { workflowId = pin.WorkflowId, revision = pin.WorkflowRevision, tenant, systemTenant = Backend.Api.Agents.AgentDefaults.SystemTenant }, tx, cancellationToken: ct));
        if (workflow?.Definition is null || !SkillHash.MatchesSha256(workflow.Definition, workflow.DefinitionSha256)) return null;
        var skills = (await c.QueryAsync<ChildSkillRow>(new CommandDefinition("SELECT ars.position Position,s.id SkillId,s.name Name,sr.revision Revision,sr.kind Kind,sr.definition Definition,sr.definition_sha256 DefinitionSha256,sr.package Package,sr.package_sha256 PackageSha256 FROM agent_revision_skill ars JOIN skill s ON s.id=ars.skill_id JOIN skill_revision sr ON sr.skill_id=s.id AND sr.revision=ars.skill_revision WHERE ars.agent_id=@agentId AND ars.agent_revision=@revision AND ars.enabled AND s.enabled ORDER BY ars.position FOR SHARE OF ars,s,sr", new { agentId, revision }, tx, cancellationToken: ct))).AsList();
        if (skills.Any(x => !string.Equals(SkillHash.Sha256(x.Definition), x.DefinitionSha256, StringComparison.Ordinal) || x.PackageSha256 is not null && !string.Equals(SkillHash.Sha256(x.Package ?? Array.Empty<byte>()), x.PackageSha256, StringComparison.Ordinal))) return null;
        var bindings = skills.Select(x => new Backend.Api.Agents.AgentRevisionSkillInfo(x.Name, x.Revision, x.Position, true)).ToArray();
        return new(new PublishedAgentSnapshotSource(agentId, agent.Name, revision, Encoding.UTF8.GetString(agent.Definition), agent.DefinitionSha256, pin.WorkflowId, pin.WorkflowRevision, bindings, agent.PromptManifestRevision, agent.PromptManifestSha256), new WorkflowSnapshotSource(pin.WorkflowId, pin.WorkflowRevision, workflow.SchemaVersion, Encoding.UTF8.GetString(workflow.Definition), workflow.DefinitionSha256, workflow.CompilerContractVersion), skills.Select(x => new SkillSnapshotSource(x.SkillId, x.Name, AgentRunSnapshotBuilder.SkillDescriptionOf(x.Definition), x.Revision, x.Kind, x.Definition, x.DefinitionSha256, x.PackageSha256 ?? (x.Package is null ? null : SkillHash.Sha256(x.Package)))).ToArray());
    }
    private static IReadOnlyCollection<string> Strings(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
    private static bool WithinJson(JsonElement? v, int max) => v is null || v.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || v.Value.GetRawText().Length <= max;
    private static bool ValidEvents(IReadOnlyList<OrchestratorRootEventAppend>? events) => events is null || events.Count <= 200 && events.All(x => !string.IsNullOrWhiteSpace(x.EventType) && x.EventType!.Length <= 100 && !x.EventType.Any(char.IsControl) && WithinJson(x.Payload, 64 * 1024));
    private static string? JsonText(JsonElement? v) => v is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value.GetRawText() : null;
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : null;
    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone(); private static JsonElement EmptyArray() => JsonDocument.Parse("[]").RootElement.Clone();
    private static (Guid WorkflowId, int WorkflowRevision, string Hash, int TokenCap)? FindPin(JsonElement snapshot, string kind, Guid agentId, int revision)
    { IEnumerable<JsonElement> pins = kind == "verifier" ? [snapshot.GetProperty("verifier")] : snapshot.GetProperty("workers").EnumerateArray(); foreach (var pin in pins) if (Guid.TryParse(pin.GetProperty("agent_id").GetString(), out var id) && id == agentId && pin.GetProperty("agent_revision").GetInt32() == revision && Guid.TryParse(pin.GetProperty("workflow_id").GetString(), out var workflowId) && pin.TryGetProperty("token_cap", out var cap) && cap.TryGetInt32(out var tokenCap) && tokenCap > 0) return (workflowId, pin.GetProperty("workflow_revision").GetInt32(), pin.GetProperty("snapshot_hash").GetString()!, tokenCap); return null; }
    private static async Task<LockedSources?> LockSourcesAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenant, Guid oid, CancellationToken ct)
    {
        var root = await c.QuerySingleOrDefaultAsync<RootRow>(new CommandDefinition("SELECT o.enabled Enabled,r.revision Revision,r.canonical_definition Definition,r.definition_sha256 DefinitionSha256,wr.workflow_id WorkflowId,wr.revision WorkflowRevision,wr.schema_version SchemaVersion,wr.definition_canonical WorkflowDefinition,wr.definition_sha256 WorkflowDefinitionSha256,wr.compiler_contract_version CompilerContractVersion FROM orchestrator o JOIN orchestrator_revision r ON r.orchestrator_id=o.id AND r.revision=o.published_revision AND r.status='published' JOIN workflow w ON w.id=r.workflow_id JOIN workflow_revision wr ON wr.workflow_id=w.id AND wr.revision=r.workflow_revision WHERE o.id=@oid AND o.tenant_id=@tenant AND w.tenant_id=@tenant AND w.enabled AND w.published_revision=r.workflow_revision AND w.kind='orchestrator' FOR SHARE OF o,r,w,wr", new { oid, tenant }, tx, cancellationToken: ct));
        if (root is null || root.Definition is null || root.WorkflowDefinition is null || !SkillHash.MatchesSha256(root.Definition, root.DefinitionSha256) || !SkillHash.MatchesSha256(root.WorkflowDefinition, root.WorkflowDefinitionSha256)) return null;
        var definition = Encoding.UTF8.GetString(root.Definition); var refs = OrchestratorCanonicalizer.AgentRefs(definition); var sources = new List<PublishedAgentSnapshotSource>();
        foreach (var reference in refs)
        {
            var row = await c.QuerySingleOrDefaultAsync<AgentRow>(new CommandDefinition("SELECT a.name Name,r.canonical_definition Definition,r.definition_sha256 DefinitionSha256,r.runtime_workflow_id WorkflowId,r.runtime_workflow_revision WorkflowRevision FROM agent a JOIN agent_revision r ON r.agent_id=a.id AND r.revision=@revision WHERE a.id=@id AND a.tenant_id=@tenant AND a.enabled AND a.published_revision=@revision AND r.status='published' FOR SHARE OF a,r", new { id = reference.AgentId, revision = reference.Revision, tenant }, tx, cancellationToken: ct));
            if (row is null || row.Definition is null || row.WorkflowId is null || row.WorkflowRevision is null || !SkillHash.MatchesSha256(row.Definition, row.DefinitionSha256)) return null;
            var visible = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition("SELECT w.id FROM workflow w JOIN workflow_revision r ON r.workflow_id=w.id AND r.revision=@revision WHERE w.id=@id AND w.tenant_id IN (@tenant,@systemTenant) AND w.enabled AND w.published_revision=@revision AND w.kind='agent-runtime' FOR SHARE OF w,r", new { id = row.WorkflowId, revision = row.WorkflowRevision, tenant, systemTenant = Backend.Api.Agents.AgentDefaults.SystemTenant }, tx, cancellationToken: ct));
            if (visible is null) return null;
            var skills = (await c.QueryAsync<Backend.Api.Agents.AgentRevisionSkillInfo>(new CommandDefinition("SELECT s.name Skill,ars.skill_revision SkillRevision,ars.position Position,ars.enabled Enabled FROM agent_revision_skill ars JOIN skill s ON s.id=ars.skill_id WHERE ars.agent_id=@id AND ars.agent_revision=@revision ORDER BY ars.position,s.name FOR SHARE OF ars,s", new { id = reference.AgentId, revision = reference.Revision }, tx, cancellationToken: ct))).AsList();
            var canonicalAgent = Encoding.UTF8.GetString(row.Definition);
            if (OrchestratorReferencePolicy.ValidateAgentDefinition(canonicalAgent, reference).Count > 0) return null;
            var source = new PublishedAgentSnapshotSource(reference.AgentId, row.Name, reference.Revision, canonicalAgent, row.DefinitionSha256, row.WorkflowId.Value, row.WorkflowRevision.Value, skills); sources.Add(source);
        }
        var verifier = refs.SingleOrDefault(x => x.Verifier); if (verifier is null) return null;
        return new LockedSources(root.Enabled, root.Revision, definition, root.DefinitionSha256, new WorkflowSnapshotSource(root.WorkflowId, root.WorkflowRevision, root.SchemaVersion, Encoding.UTF8.GetString(root.WorkflowDefinition), root.WorkflowDefinitionSha256, root.CompilerContractVersion), sources.Where(x => x.AgentId != verifier.AgentId || x.Revision != verifier.Revision).ToArray(), sources.Single(x => x.AgentId == verifier.AgentId && x.Revision == verifier.Revision));
    }
    private static async Task<bool> ExpireDeadlineAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tenant, string user, Guid runId, CancellationToken ct)
    {
        var hash = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition("UPDATE orchestrator_run SET status='timed_out',error_code='deadline_exceeded',error_message='Root run deadline expired',completed_at=clock_timestamp(),state_version=state_version+1,updated_at=clock_timestamp() WHERE id=@runId AND tenant_id=@tenant AND user_id=@user AND status IN ('queued','running','waiting_input') AND deadline_at<=clock_timestamp() RETURNING snapshot_sha256", new { runId, tenant, user }, tx, cancellationToken: ct));
        if (hash is null) return false;
        await c.ExecuteAsync(new CommandDefinition("UPDATE orchestrator_run_child SET status='cancelled',updated_at=clock_timestamp() WHERE orchestrator_root_run_id=@runId AND status IN ('queued','running'); UPDATE agent_run SET status='cancelled',cancel_requested_at=COALESCE(cancel_requested_at,clock_timestamp()),cancel_requested_by='deadline',completed_at=COALESCE(completed_at,clock_timestamp()),updated_at=clock_timestamp() WHERE orchestrator_root_run_id=@runId AND status IN ('queued','running','waiting_input'); UPDATE agent_run_command c SET dispatch_completed_at=COALESCE(c.dispatch_completed_at,clock_timestamp()),dispatch_claim_owner=NULL,dispatch_claim_token_sha256=NULL,dispatch_claim_expires_at=NULL FROM agent_run a WHERE c.run_id=a.id AND a.orchestrator_root_run_id=@runId AND a.status='cancelled'; UPDATE orchestrator_run_command SET completed_at=COALESCE(completed_at,clock_timestamp()) WHERE run_id=@runId; INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload) VALUES(@runId,(SELECT COALESCE(MAX(sequence),0)+1 FROM orchestrator_run_event WHERE run_id=@runId),'root_timed_out',@hash,'{}'::jsonb)", new { runId, hash }, tx, cancellationToken: ct));
        return true;
    }
    private async Task<bool> ExpireDeadlineAsync(string tenant, string user, Guid runId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var expired = await ExpireDeadlineAsync(c, tx, tenant, user, runId, ct);
        await tx.CommitAsync(ct);
        return expired;
    }
    private const string Columns = "id Id,orchestrator_id OrchestratorId,orchestrator_revision OrchestratorRevision,conversation_id Conversation,workflow_id WorkflowId,workflow_revision WorkflowRevision,snapshot_sha256 Hash,status Status,(cancel_requested_at IS NOT NULL) CancelRequested,state_version Version,deadline_at Deadline,execution_snapshot::text Snapshot,created_at Created,updated_at Updated,request_sha256 RequestHash,(SELECT id FROM orchestrator_run_command c WHERE c.run_id=orchestrator_run.id AND c.command_type='start' ORDER BY created_at LIMIT 1) CommandId,result::text Result,error_code ErrorCode,error_message ErrorMessage,checkpoint_ref CheckpointRef,checkpoint_version CheckpointVersion";
    private static async Task<Row?> Load(NpgsqlConnection c, string tenant, string user, Guid id, CancellationToken ct) => await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition("SELECT " + Columns + " FROM orchestrator_run WHERE id=@id AND tenant_id=@tenant AND user_id=@user", new { id, tenant, user }, cancellationToken: ct));
    private static OrchestratorRunResponse? ToResponse(Row? x) { if (x is null) return null; using var doc = JsonDocument.Parse(x.Snapshot); return new(x.Id, x.OrchestratorId, x.OrchestratorRevision, x.Conversation, x.WorkflowId, x.WorkflowRevision, x.Hash, x.Status, x.CancelRequested, x.Version, x.Deadline, Budgets(doc.RootElement), x.Created, x.Updated, x.CommandId, x.Result is null ? null : JsonDocument.Parse(x.Result).RootElement.Clone(), x.ErrorCode, x.ErrorMessage); }
    private static JsonElement Budgets(JsonElement snapshot) { var limits = snapshot.GetProperty("limits"); var value = new System.Text.Json.Nodes.JsonObject { { "maxContextRounds", limits.GetProperty("max_context_rounds").GetInt32() }, { "maxTasks", limits.GetProperty("max_tasks").GetInt32() }, { "maxChildRuns", limits.GetProperty("max_child_runs").GetInt32() }, { "maxConcurrency", limits.GetProperty("max_concurrency").GetInt32() }, { "maxRepairRounds", limits.GetProperty("max_repair_rounds").GetInt32() }, { "timeoutSeconds", limits.GetProperty("timeout_seconds").GetDouble() }, { "tokenBudget", snapshot.GetProperty("token_budget").GetInt32() } }; return JsonDocument.Parse(value.ToJsonString()).RootElement.Clone(); }
    private sealed record Row(Guid Id, Guid OrchestratorId, int OrchestratorRevision, string Conversation, Guid WorkflowId, int WorkflowRevision, string Hash, string Status, bool CancelRequested, long Version, DateTime Deadline, string Snapshot, DateTime Created, DateTime Updated, string RequestHash, Guid? CommandId, string? Result, string? ErrorCode, string? ErrorMessage, string? CheckpointRef, long CheckpointVersion); private sealed record Event(long Sequence, string Type, string Hash, string Payload, DateTime At);
    private sealed record ContextChildRow(string TaskId, string RunKind, string TaskEnvelope, string RootSnapshotHash);
    private sealed record ContextRequestRow(Guid Id, Guid RootRunId, Guid ChildId, Guid ContextId, string TaskId, string Role, string? BaseContextRef, string? CurrentContextRef, long Version, DateTime CreatedAt, DateTime UpdatedAt)
    { public OrchestratorContextRequestResponse ToResponse() => new(Id, RootRunId, ChildId, TaskId, Role, ContextId, ContextRefFromJson(BaseContextRef), ContextRefFromJson(CurrentContextRef), Version, CreatedAt, UpdatedAt); }
    private sealed class RootRow { public bool Enabled { get; init; } public int Revision { get; init; } public byte[]? Definition { get; init; } public string DefinitionSha256 { get; init; } = ""; public Guid WorkflowId { get; init; } public int WorkflowRevision { get; init; } public int SchemaVersion { get; init; } public byte[]? WorkflowDefinition { get; init; } public string WorkflowDefinitionSha256 { get; init; } = ""; public string CompilerContractVersion { get; init; } = ""; }
    private sealed class AgentRow { public string Name { get; init; } = ""; public byte[]? Definition { get; init; } public string DefinitionSha256 { get; init; } = ""; public Guid? WorkflowId { get; init; } public int? WorkflowRevision { get; init; } public int? PromptManifestRevision { get; init; } public string? PromptManifestSha256 { get; init; } }
    private sealed record WorkflowSourceRow(int SchemaVersion, byte[]? Definition, string DefinitionSha256, string CompilerContractVersion);
    private sealed record ChildSkillRow(int Position, Guid SkillId, string Name, int Revision, string Kind, string Definition, string DefinitionSha256, byte[]? Package, string? PackageSha256);
    private sealed record ChildSource(PublishedAgentSnapshotSource Agent, WorkflowSnapshotSource Workflow, IReadOnlyList<SkillSnapshotSource> Skills);
    private sealed record ChildStatusRow(Guid Id, Guid RootRunId, string TaskId, int Attempt, string RunKind, Guid AgentId, int AgentRevision, Guid WorkflowId, int WorkflowRevision, string AgentSnapshotHash, Guid? AgentRunId, string Status, string? Result, string? ErrorCode, string? ErrorMessage);
    private sealed record RootLeaseRow(string? TokenHash, DateTime? ExpiresAt, long Generation);
    private static string? ResumeInput(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("input", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null; } catch (JsonException) { return null; } }
    private sealed class ClaimRow { public Guid CommandId { get; init; } public string CommandType { get; init; } = ""; public string? CommandInput { get; init; } public string? CheckpointRef { get; init; } public long? CheckpointVersion { get; init; } public DateTime? CompletedAt { get; init; } public DateTime? ClaimExpiresAt { get; init; } public long LeaseGeneration { get; init; } public string SnapshotHash { get; init; } = ""; public byte[] Snapshot { get; init; } = Array.Empty<byte>(); }
    private sealed record RecoveryCandidate(Guid CommandId, Guid RunId, string TenantId, string UserId, string Role);
    private sealed record ResumeReplay(Guid CommandId, string? InputHash); private sealed record ReplayCommand(Guid Id, string Type, string? InputHash);
    private sealed record LockedSources(bool Enabled, int Revision, string Definition, string DefinitionSha256, WorkflowSnapshotSource Workflow, IReadOnlyList<PublishedAgentSnapshotSource> Workers, PublishedAgentSnapshotSource Verifier);
}
