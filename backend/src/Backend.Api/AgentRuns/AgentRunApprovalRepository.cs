using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using Backend.Api.Skills;
using Dapper;
using Npgsql;

namespace Backend.Api.AgentRuns;

/// <summary>PostgreSQL approval gate. The decision and queue transition share one transaction.</summary>
public sealed partial class AgentRunApprovalRepository(NpgsqlDataSource dataSource) : IAgentRunApprovalRepository
{
    public async Task<AgentRunApprovalWriteResult> CreateAsync(string tenantId, string userId, Guid runId, AgentRunApprovalCreateRequest request, CancellationToken ct)
    {
        if (request.ExpectedVersion < 1 || request.LeaseGeneration < 1 || string.IsNullOrWhiteSpace(request.LeaseToken)
            || string.IsNullOrWhiteSpace(request.CheckpointRef) || request.CheckpointVersion < 1
            || !Role().IsMatch(request.RequiredRole ?? "") || !Fingerprint().IsMatch(request.ActionFingerprint ?? "")
            || request.ExpiresAt is not DateTime expiry || expiry <= DateTime.UtcNow || expiry > DateTime.UtcNow.AddDays(1))
            return new(AgentRunApprovalWriteStatus.InvalidState, Message: "invalid approval request");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var run = await conn.QuerySingleOrDefaultAsync<ApprovalRunRow>(new CommandDefinition(
            "SELECT id AS Id,user_id AS UserId,status AS Status,state_version AS StateVersion,lease_generation AS LeaseGeneration,lease_token_sha256 AS LeaseTokenHash,(cancel_requested_at IS NOT NULL) AS CancelRequested FROM agent_run WHERE id=@runId AND tenant_id=@tenantId FOR UPDATE",
            new { runId, tenantId }, tx, cancellationToken: ct));
        if (run is null) return new(AgentRunApprovalWriteStatus.NotFound);
        if (run.UserId != userId || run.CancelRequested || run.Status != AgentRunStatuses.Running || run.StateVersion != request.ExpectedVersion || run.LeaseGeneration != request.LeaseGeneration
            || !string.Equals(run.LeaseTokenHash, SkillHash.Sha256(request.LeaseToken!), StringComparison.Ordinal))
            return new(AgentRunApprovalWriteStatus.Conflict, Message: "run lease or state changed");

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO agent_run_approval(id,run_id,tenant_id,requested_by,required_role,action_fingerprint,expires_at,self_approval_forbidden,status,checkpoint_ref,checkpoint_version) VALUES(@id,@runId,@tenantId,@userId,@role,@fingerprint,@expiresAt,@sod,'pending',@checkpointRef,@checkpointVersion); UPDATE agent_run SET status='waiting_approval',state_version=state_version+1,checkpoint_ref=@checkpointRef,checkpoint_version=@checkpointVersion,lease_owner=NULL,lease_token_sha256=NULL,lease_expires_at=NULL,updated_at=clock_timestamp() WHERE id=@runId;",
            new { id, runId, tenantId, userId, role = request.RequiredRole, fingerprint = request.ActionFingerprint, expiresAt = expiry, sod = true, checkpointRef = request.CheckpointRef, checkpointVersion = request.CheckpointVersion }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(AgentRunApprovalWriteStatus.Success, await FindAsync(conn, tenantId, runId, id, ct));
    }

    public async Task<IReadOnlyList<AgentRunApprovalResponse>?> ListAsync(string tenantId, string userId, string role, Guid runId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM agent_run r WHERE r.id=@runId AND r.tenant_id=@tenantId AND (r.user_id=@userId OR EXISTS(SELECT 1 FROM agent_run_approval a WHERE a.run_id=r.id AND a.tenant_id=@tenantId AND a.required_role=@role AND (NOT a.self_approval_forbidden OR a.requested_by<>@userId))))", new { runId, tenantId, userId, role }, cancellationToken: ct));
        if (!exists) return null;
        var rows = await conn.QueryAsync<ApprovalRow>(new CommandDefinition(ApprovalSelect + " WHERE run_id=@runId AND tenant_id=@tenantId ORDER BY created_at", new { runId, tenantId }, cancellationToken: ct));
        // An eligible business approver receives only approvals it can act on;
        // the owner retains its full redacted queue for progress visibility.
        var owner = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM agent_run WHERE id=@runId AND tenant_id=@tenantId AND user_id=@userId)", new { runId, tenantId, userId }, cancellationToken: ct));
        return rows.Where(x => owner || (x.RequiredRole == role && (!x.SelfApprovalForbidden || x.RequestedBy != userId))).Select(ToResponse).ToArray();
    }

    public async Task<AgentRunApprovalWriteResult> DecideAsync(string tenantId, string approverId, string approverRole, Guid runId, Guid approvalId, bool approve, string idempotencyKey, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 || reason?.Length > 500)
            return new(AgentRunApprovalWriteStatus.InvalidState, Message: "invalid decision request");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ApprovalRow>(new CommandDefinition(ApprovalSelect + " WHERE id=@approvalId AND run_id=@runId AND tenant_id=@tenantId FOR UPDATE", new { approvalId, runId, tenantId }, tx, cancellationToken: ct));
        if (row is null) return new(AgentRunApprovalWriteStatus.NotFound);
        var prior = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT decision FROM agent_run_approval_decision WHERE approval_id=@approvalId AND idempotency_key_sha256=@key", new { approvalId, key = SkillHash.Sha256(idempotencyKey) }, tx, cancellationToken: ct));
        if (prior is not null) return new(AgentRunApprovalWriteStatus.Replay, ToResponse(row));
        if (row.Status != "pending") return new(AgentRunApprovalWriteStatus.Replay, ToResponse(row));
        if (row.ExpiresAt <= DateTime.UtcNow)
        {
            // Expiry is a terminal decision, not merely a response code.  If
            // left waiting_approval it has no execute row to reclaim and would
            // permanently strand the run.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_run_approval SET status='expired' WHERE id=@approvalId AND status='pending'; UPDATE agent_run SET status='failed',state_version=state_version+1,error_code='approval_expired',error_message='Approval expired before a decision.',completed_at=clock_timestamp(),updated_at=clock_timestamp() WHERE id=@runId AND status='waiting_approval';",
                new { approvalId, runId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new(AgentRunApprovalWriteStatus.Expired, (await FindAsync(conn, tenantId, runId, approvalId, ct)));
        }
        if (!string.Equals(row.RequiredRole, approverRole, StringComparison.Ordinal) || (row.SelfApprovalForbidden && row.RequestedBy == approverId)) return new(AgentRunApprovalWriteStatus.Forbidden, ToResponse(row));
        var run = await conn.QuerySingleOrDefaultAsync<ApprovalRunRow>(new CommandDefinition("SELECT id AS Id,user_id AS UserId,status AS Status,state_version AS StateVersion,lease_generation AS LeaseGeneration,lease_token_sha256 AS LeaseTokenHash,(cancel_requested_at IS NOT NULL) AS CancelRequested FROM agent_run WHERE id=@runId AND tenant_id=@tenantId FOR UPDATE", new { runId, tenantId }, tx, cancellationToken: ct));
        if (run is null) return new(AgentRunApprovalWriteStatus.NotFound);
        if (run.CancelRequested)
        {
            // Cancellation wins over an undecided approval.  Do not create an
            // execute row; the durable cancel command owns terminalization.
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_run_approval SET status='cancelled' WHERE id=@approvalId AND status='pending'",
                new { approvalId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new(AgentRunApprovalWriteStatus.InvalidState, await FindAsync(conn, tenantId, runId, approvalId, ct), Message: "run cancellation was requested");
        }
        if (run.Status != AgentRunStatuses.WaitingApproval) return new(AgentRunApprovalWriteStatus.InvalidState, ToResponse(row));
        var decision = approve ? "approved" : "rejected";
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO agent_run_approval_decision(approval_id,idempotency_key_sha256,decision,approver_id,reason) VALUES(@approvalId,@key,@decision,@approverId,@reason); UPDATE agent_run_approval SET status=@decision,decision=@decision,decided_by=@approverId,decided_at=clock_timestamp(),reason=@reason WHERE id=@approvalId; UPDATE agent_run SET status=@next,state_version=state_version+1,updated_at=clock_timestamp() WHERE id=@runId; INSERT INTO agent_run_approval_execute(approval_id,run_id,tenant_id,approver_id,status) SELECT @approvalId,@runId,@tenantId,@approverId,'queued' WHERE @decision='approved' ON CONFLICT (approval_id) DO NOTHING;", new { approvalId, key = SkillHash.Sha256(idempotencyKey), decision, approverId, reason, runId, tenantId, next = approve ? AgentRunStatuses.Queued : AgentRunStatuses.Failed }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new(AgentRunApprovalWriteStatus.Success, await FindAsync(conn, tenantId, runId, approvalId, ct));
    }

    public async Task<(AgentRunApprovalWriteStatus Status, AgentRunApprovalConsumeResponse? Response, string? Message)> ConsumeAsync(string tenantId, Guid runId, Guid approvalId, AgentRunApprovalConsumeRequest request, CancellationToken ct)
    {
        if (!Fingerprint().IsMatch(request.ActionFingerprint ?? "") || request.LeaseGeneration < 1 || string.IsNullOrWhiteSpace(request.LeaseToken)) return (AgentRunApprovalWriteStatus.InvalidState, null, "invalid consume request");
        await using var conn = await dataSource.OpenConnectionAsync(ct); await using var tx = await conn.BeginTransactionAsync(ct);
        var approval = await conn.QuerySingleOrDefaultAsync<ApprovalRow>(new CommandDefinition(ApprovalSelect + " WHERE id=@approvalId AND run_id=@runId AND tenant_id=@tenantId FOR UPDATE", new { approvalId,runId,tenantId },tx,cancellationToken:ct));
        if (approval is null) return (AgentRunApprovalWriteStatus.NotFound,null,null);
        if (approval.ExpiresAt <= DateTime.UtcNow) return (AgentRunApprovalWriteStatus.Expired,null,null);
        if (!string.Equals(approval.ActionFingerprint,request.ActionFingerprint,StringComparison.Ordinal)) return (AgentRunApprovalWriteStatus.Conflict,null,"approval action changed");
        var run = await conn.QuerySingleOrDefaultAsync<ApprovalRunRow>(new CommandDefinition("SELECT id AS Id,user_id AS UserId,status AS Status,state_version AS StateVersion,lease_generation AS LeaseGeneration,lease_token_sha256 AS LeaseTokenHash,(cancel_requested_at IS NOT NULL) AS CancelRequested FROM agent_run WHERE id=@runId AND tenant_id=@tenantId FOR UPDATE",new {runId,tenantId},tx,cancellationToken:ct));
        if (run is null) return (AgentRunApprovalWriteStatus.NotFound,null,null);
        if (run.CancelRequested) return (AgentRunApprovalWriteStatus.InvalidState,null,"run cancellation was requested");
        if (run.LeaseGeneration != request.LeaseGeneration || !string.Equals(run.LeaseTokenHash,SkillHash.Sha256(request.LeaseToken!),StringComparison.Ordinal)) return (AgentRunApprovalWriteStatus.Conflict,null,"stale write lease");
        var existing = await conn.QuerySingleOrDefaultAsync<EffectRow>(new CommandDefinition("SELECT id AS Id,status AS Status FROM agent_run_write_effect WHERE run_id=@runId AND action_fingerprint=@fingerprint FOR UPDATE",new {runId,fingerprint=request.ActionFingerprint},tx,cancellationToken:ct));
        if (existing is not null) { await tx.CommitAsync(ct); return (existing.Status == "completed" ? AgentRunApprovalWriteStatus.Replay : AgentRunApprovalWriteStatus.Success,new AgentRunApprovalConsumeResponse(existing.Id,existing.Status == "reserved" ? "granted" : existing.Status),null); }
        if (approval.Status != "approved" || run.Status != AgentRunStatuses.Running) return (AgentRunApprovalWriteStatus.InvalidState,null,"approval is not executable");
        var effectId=Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO agent_run_write_effect(id,approval_id,run_id,action_fingerprint,status) VALUES(@effectId,@approvalId,@runId,@fingerprint,'reserved'); UPDATE agent_run_approval SET status='consumed' WHERE id=@approvalId AND status='approved';",new {effectId,approvalId,runId,fingerprint=request.ActionFingerprint},tx,cancellationToken:ct));
        await tx.CommitAsync(ct); return (AgentRunApprovalWriteStatus.Success,new AgentRunApprovalConsumeResponse(effectId,"granted"),null);
    }

    public async Task<AgentRunApprovalWriteStatus> CompleteEffectAsync(string tenantId, Guid runId, Guid effectId, bool succeeded, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var target = succeeded ? "completed" : "failed";
        var existing = await conn.QuerySingleOrDefaultAsync<EffectRow>(new CommandDefinition(
            "SELECT e.id AS Id,e.status AS Status FROM agent_run_write_effect e JOIN agent_run r ON r.id=e.run_id WHERE e.id=@effectId AND e.run_id=@runId AND r.tenant_id=@tenantId",
            new { effectId, runId, tenantId }, cancellationToken: ct));
        if (existing is null) return AgentRunApprovalWriteStatus.NotFound;
        if (existing.Status == target) return AgentRunApprovalWriteStatus.Success;
        if (existing.Status != "reserved") return AgentRunApprovalWriteStatus.Conflict;
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_write_effect SET status=@target,completed_at=clock_timestamp() WHERE id=@effectId AND status='reserved'",
            new { effectId, target }, cancellationToken: ct));
        return changed == 1 ? AgentRunApprovalWriteStatus.Success : AgentRunApprovalWriteStatus.Conflict;
    }

    public async Task<(AgentRunApprovalWriteStatus Status, AgentRunWriteEvidenceResponse? Response, string? Message)> WriteEvidenceAsync(string tenantId, Guid runId, Guid effectId, AgentRunWriteEvidenceRequest request, CancellationToken ct)
    {
        var recordId = request.RecordId?.Trim();
        var value = request.Value;
        if (string.IsNullOrWhiteSpace(recordId) || recordId.Length > 128 || value is null || value.Length > 4_000)
            return (AgentRunApprovalWriteStatus.InvalidState, null, "invalid write evidence");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // This lock is the last cancellation fence before a write becomes
        // externally durable.  A cancel that commits first rejects the
        // evidence/outbox transaction; a write that holds it first is the one
        // allowed, atomic claimed effect of the cancel/write race.
        var run = await conn.QuerySingleOrDefaultAsync<ApprovalRunRow>(new CommandDefinition(
            "SELECT id AS Id,user_id AS UserId,status AS Status,state_version AS StateVersion,lease_generation AS LeaseGeneration,lease_token_sha256 AS LeaseTokenHash,(cancel_requested_at IS NOT NULL) AS CancelRequested FROM agent_run WHERE id=@runId AND tenant_id=@tenantId FOR UPDATE",
            new { runId, tenantId }, tx, cancellationToken: ct));
        if (run is null) return (AgentRunApprovalWriteStatus.NotFound, null, null);
        if (run.CancelRequested) return (AgentRunApprovalWriteStatus.InvalidState, null, "run cancellation was requested");
        var effect = await conn.QuerySingleOrDefaultAsync<EffectRow>(new CommandDefinition(
            "SELECT e.id AS Id,e.status AS Status,e.evidence->>'record_id' AS EvidenceRecordId,e.evidence->>'value' AS EvidenceValue FROM agent_run_write_effect e JOIN agent_run r ON r.id=e.run_id WHERE e.id=@effectId AND e.run_id=@runId AND r.tenant_id=@tenantId FOR UPDATE",
            new { effectId, runId, tenantId }, tx, cancellationToken: ct));
        if (effect is null) return (AgentRunApprovalWriteStatus.NotFound, null, null);
        if (effect.Status == "completed")
        {
            if (effect.EvidenceRecordId == recordId && effect.EvidenceValue == value)
            {
                await tx.CommitAsync(ct);
                return (AgentRunApprovalWriteStatus.Replay, new AgentRunWriteEvidenceResponse(1, "replayed"), null);
            }
            return (AgentRunApprovalWriteStatus.Conflict, null, "write effect payload changed");
        }
        if (effect.Status != "reserved") return (AgentRunApprovalWriteStatus.Conflict, null, "write effect is not reservable");

        // Store the business record and its integration/audit outbox entry in
        // the same transaction.  `effect_id` is the primary key of both
        // records, so a process restart cannot manufacture a second write.
        var payload = JsonSerializer.Serialize(new { record_id = recordId, value });
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_write_effect SET status='completed',evidence=CAST(@payload AS jsonb),completed_at=clock_timestamp() WHERE id=@effectId AND status='reserved'; INSERT INTO agent_run_write_outbox(effect_id,run_id,tenant_id,payload,status,delivered_at) VALUES(@effectId,@runId,@tenantId,CAST(@payload AS jsonb),'delivered',clock_timestamp()) ON CONFLICT(effect_id) DO NOTHING;",
            new { effectId, runId, tenantId, payload }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return (AgentRunApprovalWriteStatus.Success, new AgentRunWriteEvidenceResponse(1, "written"), null);
    }

    public async Task<AgentRunApprovalExecutionIdentity?> GetExecutionIdentityAsync(string tenantId, string approverId, Guid runId, Guid approvalId, CancellationToken ct)
    { await using var conn=await dataSource.OpenConnectionAsync(ct); var row=await conn.QuerySingleOrDefaultAsync<ExecutionIdentityRow>(new CommandDefinition("SELECT r.user_id AS UserId,r.caller_role AS Role FROM agent_run r JOIN agent_run_approval a ON a.run_id=r.id WHERE r.id=@runId AND r.tenant_id=@tenantId AND a.id=@approvalId AND a.tenant_id=@tenantId AND a.status IN ('approved','consumed') AND a.decided_by=@approverId",new{tenantId,approverId,runId,approvalId},cancellationToken:ct)); return row is null?null:new AgentRunApprovalExecutionIdentity(row.UserId,row.Role); }
    public async Task<AgentRunApprovalExecuteClaim?> ClaimExecuteAsync(string tenantId, Guid runId, Guid approvalId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ReconcileTerminalExecutionsAsync(conn, tx, approvalId, ct);
        var row = await conn.QuerySingleOrDefaultAsync<ExecuteRow>(new CommandDefinition(
            "SELECT approval_id AS ApprovalId,run_id AS RunId,tenant_id AS TenantId,approver_id AS ApproverId,status AS Status,claim_expires_at AS ClaimExpiresAt FROM agent_run_approval_execute WHERE approval_id=@approvalId AND run_id=@runId AND tenant_id=@tenantId FOR UPDATE",
            new { approvalId, runId, tenantId }, tx, cancellationToken: ct));
        if (row is null || row.Status == "completed" || row.Status == "dead_letter" || (row.Status == "claimed" && row.ClaimExpiresAt > DateTime.UtcNow)) return null;
        var token = RandomNumberGenerator.GetHexString(32);
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_approval_execute SET status='claimed',attempts=attempts+1,claim_token_sha256=@hash,claim_expires_at=clock_timestamp()+interval '60 seconds',completed_at=NULL WHERE approval_id=@approvalId AND status IN ('queued','claimed') AND (status='queued' OR claim_expires_at < clock_timestamp())",
            new { approvalId, hash = SkillHash.Sha256(token) }, tx, cancellationToken: ct));
        if (changed != 1) return null;
        await tx.CommitAsync(ct);
        return new AgentRunApprovalExecuteClaim(row.ApprovalId, row.RunId, row.TenantId, row.ApproverId, token);
    }

    public async Task<IReadOnlyList<AgentRunApprovalExecuteClaim>> ClaimExecuteRecoveryAsync(int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, 100);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await ReconcileTerminalExecutionsAsync(conn, tx, null, ct);
            await tx.CommitAsync(ct);
        }
        var candidates = (await conn.QueryAsync<Guid>(new CommandDefinition(
            "SELECT e.approval_id FROM agent_run_approval_execute e JOIN agent_run_approval a ON a.id=e.approval_id JOIN agent_run r ON r.id=e.run_id WHERE e.status='queued' OR (e.status='claimed' AND e.claim_expires_at < clock_timestamp()) ORDER BY e.created_at LIMIT @take",
            new { take }, cancellationToken: ct))).ToArray();
        var claimed = new List<AgentRunApprovalExecuteClaim>(candidates.Length);
        foreach (var id in candidates)
        {
            // ClaimExecuteAsync locks and fences each row, so concurrent
            // scanners may observe the candidate but cannot both execute it.
            var row = await conn.QuerySingleOrDefaultAsync<ExecuteRow>(new CommandDefinition(
                "SELECT approval_id AS ApprovalId,run_id AS RunId,tenant_id AS TenantId FROM agent_run_approval_execute WHERE approval_id=@id", new { id }, cancellationToken: ct));
            if (row is not null)
            {
                var claim = await ClaimExecuteAsync(row.TenantId, row.RunId, row.ApprovalId, ct);
                if (claim is not null) claimed.Add(claim);
            }
        }
        return claimed;
    }

    public async Task<AgentRunApprovalWriteStatus> CompleteExecuteAsync(Guid approvalId, string claimToken, bool deadLetter, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claimToken)) return AgentRunApprovalWriteStatus.InvalidState;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var execution = await conn.QuerySingleOrDefaultAsync<ExecuteRow>(new CommandDefinition(
            "SELECT approval_id AS ApprovalId,run_id AS RunId FROM agent_run_approval_execute WHERE approval_id=@approvalId AND status='claimed' AND claim_token_sha256=@hash AND claim_expires_at > clock_timestamp() FOR UPDATE",
            new { approvalId, hash = SkillHash.Sha256(claimToken) }, tx, cancellationToken: ct));
        if (execution is null) return AgentRunApprovalWriteStatus.Conflict;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_approval_execute SET status=@status,completed_at=clock_timestamp() WHERE approval_id=@approvalId",
            new { approvalId, status = deadLetter ? "dead_letter" : "completed" }, tx, cancellationToken: ct));
        if (deadLetter)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_run SET status='failed',state_version=state_version+1,error_code='approved_write_unrecoverable',error_message='Approved write could not be safely resumed.',completed_at=clock_timestamp(),lease_owner=NULL,lease_token_sha256=NULL,lease_expires_at=NULL,updated_at=clock_timestamp() WHERE id=@runId AND status IN ('queued','running') AND cancel_requested_at IS NULL",
                new { runId = execution.RunId }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return AgentRunApprovalWriteStatus.Success;
    }

    private static async Task ReconcileTerminalExecutionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? approvalId, CancellationToken ct)
    {
        // A cancellation/expiry may occur after the browser decision but before
        // Workflow obtains its claim.  Terminalize the execute command here so
        // it cannot be reclaimed forever.  Expiry also fails a still-active run
        // without ever invoking a write boundary.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_approval_execute e SET status='dead_letter',completed_at=clock_timestamp() FROM agent_run_approval a JOIN agent_run r ON r.id=a.run_id WHERE e.approval_id=a.id AND e.status IN ('queued','claimed') AND (@approvalId IS NULL OR e.approval_id=@approvalId) AND (r.status IN ('completed','failed','cancelled') OR r.cancel_requested_at IS NOT NULL OR a.status IN ('rejected','expired','cancelled') OR a.expires_at <= clock_timestamp()); UPDATE agent_run_approval a SET status='expired' FROM agent_run_approval_execute e JOIN agent_run r ON r.id=e.run_id WHERE a.id=e.approval_id AND e.status='dead_letter' AND a.expires_at <= clock_timestamp() AND a.status IN ('approved','consumed'); UPDATE agent_run r SET status='failed',state_version=state_version+1,error_code='approval_expired',error_message='Approval expired before the write could execute.',completed_at=clock_timestamp(),lease_owner=NULL,lease_token_sha256=NULL,lease_expires_at=NULL,updated_at=clock_timestamp() FROM agent_run_approval_execute e JOIN agent_run_approval a ON a.id=e.approval_id WHERE r.id=e.run_id AND e.status='dead_letter' AND a.expires_at <= clock_timestamp() AND r.status IN ('queued','running')",
            new { approvalId }, tx, cancellationToken: ct));
    }

    private static AgentRunApprovalResponse ToResponse(ApprovalRow row) => new(row.Id,row.RunId,row.Status,row.RequiredRole,row.ActionFingerprint,row.ExpiresAt,row.SelfApprovalForbidden,row.RequestedBy,row.Decision,row.DecidedBy,row.DecidedAt,row.Reason,row.CheckpointRef,row.CheckpointVersion);
    private const string ApprovalSelect = "SELECT id AS Id,run_id AS RunId,status AS Status,required_role AS RequiredRole,action_fingerprint AS ActionFingerprint,expires_at AS ExpiresAt,self_approval_forbidden AS SelfApprovalForbidden,requested_by AS RequestedBy,decision AS Decision,decided_by AS DecidedBy,decided_at AS DecidedAt,reason AS Reason,checkpoint_ref AS CheckpointRef,checkpoint_version AS CheckpointVersion FROM agent_run_approval";
    private static async Task<AgentRunApprovalResponse?> FindAsync(NpgsqlConnection conn,string tenantId,Guid runId,Guid id,CancellationToken ct) => (await conn.QuerySingleAsync<ApprovalRow>(new CommandDefinition(ApprovalSelect + " WHERE id=@id AND run_id=@runId AND tenant_id=@tenantId",new { id,runId,tenantId },cancellationToken:ct))) is { } row ? ToResponse(row) : null;
    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")] private static partial Regex Role();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Fingerprint();
    private sealed class ApprovalRunRow { public Guid Id {get;init;} public string UserId {get;init;}=""; public string Status {get;init;}=""; public long StateVersion {get;init;} public long LeaseGeneration {get;init;} public string? LeaseTokenHash {get;init;} public bool CancelRequested {get;init;} }
    private sealed class ApprovalRow { public Guid Id {get;init;} public Guid RunId {get;init;} public string Status {get;init;}=""; public string RequiredRole {get;init;}=""; public string ActionFingerprint {get;init;}=""; public DateTime ExpiresAt {get;init;} public bool SelfApprovalForbidden {get;init;} public string RequestedBy {get;init;}=""; public string? Decision {get;init;} public string? DecidedBy {get;init;} public DateTime? DecidedAt {get;init;} public string? Reason {get;init;} public string CheckpointRef {get;init;}=""; public long CheckpointVersion {get;init;} }
    private sealed class EffectRow { public Guid Id {get;init;} public string Status {get;init;}=""; public string? EvidenceRecordId {get;init;} public string? EvidenceValue {get;init;} }
    private sealed class ExecutionIdentityRow { public string UserId {get;init;}=""; public string Role {get;init;}=""; }
    private sealed class ExecuteRow { public Guid ApprovalId {get;init;} public Guid RunId {get;init;} public string TenantId {get;init;}=""; public string ApproverId {get;init;}=""; public string Status {get;init;}=""; public DateTime? ClaimExpiresAt {get;init;} }
}
