using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Skills;
using Backend.Api.Workflows;
using Dapper;
using Npgsql;

namespace Backend.Api.AgentRuns;

public sealed class AgentRunRepository : IAgentRunRepository
{
    private readonly ILogger<AgentRunRepository>? _logger;
    private static readonly IReadOnlySet<string> CancelAuditEventTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "checkpoint_budget",
            "context_acquisition_planned",
            "input_requested",
            "workflow_completed",
            "model_step",
            "output_validated",
            "response_proposed",
            "rule_decision",
            "run_cancelled",
            "run_preflight",
            "run_resumed",
            "run_terminal",
            "runtime_denied",
            "skill_file_read",
            "skill_scope_entered",
            "skill_scope_exited",
            "tool_completed",
            "tool_deduplicated",
        };

    private static readonly IReadOnlySet<string> DeadlineCleanupAuditEventTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "deadline_exceeded",
        };

    private const string RunColumns =
        "id AS Id, root_run_id AS RootRunId, parent_run_id AS ParentRunId, task_id AS TaskId,"
        + " run_kind AS RunKind, agent_id AS AgentId, agent_revision AS AgentRevision,"
        + " workflow_id AS WorkflowId, workflow_revision AS WorkflowRevision,"
        + " snapshot_sha256 AS SnapshotHash, status AS Status, state_version AS StateVersion,"
        + " lease_generation AS LeaseGeneration,"
        + " checkpoint_generation AS CheckpointGeneration,"
        + " checkpoint_ref AS CheckpointRef, checkpoint_version AS CheckpointVersion,"
        + " event_ack_cursor AS EventAckCursor,"
        + " (cancel_requested_at IS NOT NULL) AS CancelRequested,"
        + " pending_input::text AS PendingInput, result::text AS Result,"
        + " error_code AS ErrorCode, error_message AS ErrorMessage,"
        + " latest_event_sequence AS LatestEventSequence, started_at AS StartedAt,"
        + " created_at AS CreatedAt, deadline_at AS DeadlineAt,"
        + " updated_at AS UpdatedAt, completed_at AS CompletedAt,"
        + " (execution_snapshot->'agent'->'runtime_limits')::text AS RuntimeLimits,"
        + " (SELECT COALESCE(jsonb_agg(jsonb_build_object("
        + " 'name',p.skill_name,'revision',p.skill_revision,'kind',p.kind,"
        + " 'definition_sha256',p.definition_sha256,'package_sha256',p.package_sha256)"
        + " ORDER BY p.position),'[]'::jsonb)::text"
        + " FROM agent_run_skill p WHERE p.run_id=agent_run.id) AS SkillPins,"
        + " lease_owner AS LeaseOwner,lease_token_sha256 AS LeaseTokenHash,"
        + " lease_expires_at AS LeaseExpiresAt,lease_command_id AS LeaseCommandId";

    private readonly NpgsqlDataSource _dataSource;

    public AgentRunRepository(
        NpgsqlDataSource dataSource,
        ILogger<AgentRunRepository>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public Task<AgentRunWriteResult> CreateDirectAsync(
        string tenantId,
        string userId,
        string role,
        Guid agentId,
        string message,
        string idempotencyKey,
        CancellationToken ct)
        => CreateDirectAsync(
            tenantId,
            userId,
            role,
            Array.Empty<string>(),
            agentId,
            message,
            idempotencyKey,
            ct);

    public async Task<AgentRunWriteResult> CreateDirectAsync(
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> capabilityClaims,
        Guid agentId,
        string message,
        string idempotencyKey,
        CancellationToken ct)
        => await CreateDirectAsync(
            tenantId,
            userId,
            role,
            Array.Empty<string>(),
            capabilityClaims,
            agentId,
            message,
            idempotencyKey,
            ct);

    public async Task<AgentRunWriteResult> CreateDirectAsync(
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        Guid agentId,
        string message,
        string idempotencyKey,
        CancellationToken ct)
    {
        var keyHash = SkillHash.Sha256(idempotencyKey);
        var requestHash = SkillHash.Sha256($"{agentId:D}\0{message}");
        var commandInput = JsonSerializer.Serialize(new { message });
        var prior = await FindCommandAsync(
            tenantId, userId, "start", keyHash, requestHash, ct);
        if (prior is not null)
        {
            return prior;
        }
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var source = await conn.QuerySingleOrDefaultAsync<AgentSourceRow>(new CommandDefinition(
                "SELECT a.name AS Name, a.enabled AS Enabled, a.published_revision AS PublishedRevision,"
                + " r.canonical_definition AS CanonicalDefinition,"
                + " r.runtime_workflow_id AS WorkflowId,"
                + " r.runtime_workflow_revision AS WorkflowRevision,"
                + " r.definition_sha256 AS DefinitionSha256,"
                + " r.prompt_manifest_revision AS PromptManifestRevision,"
                + " r.prompt_manifest_sha256 AS PromptManifestSha256"
                + " FROM agent a"
                + " JOIN agent_revision r ON r.agent_id = a.id AND r.revision = a.published_revision"
                + " WHERE a.tenant_id = @tenantId AND a.id = @agentId AND a.enabled"
                + " FOR SHARE OF a, r",
                new { tenantId, agentId }, tx, cancellationToken: ct));
            if (source is null)
            {
                var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM agent WHERE tenant_id=@tenantId AND id=@agentId)",
                    new { tenantId, agentId }, tx, cancellationToken: ct));
                await tx.RollbackAsync(ct);
                return exists
                    ? InvalidState("Agent 尚未發布或已停用")
                    : NotFound("找不到可執行的已發布 Agent");
            }

            if (source.PublishedRevision is not int agentRevision
                || source.WorkflowId is not Guid workflowId
                || source.WorkflowRevision is not int workflowRevision)
            {
                await tx.RollbackAsync(ct);
                return InvalidState("Agent 未授權 ADMIN direct test 或不具 worker execution role");
            }

            var definition = AgentCanonicalizer.ReadAuthoritativeDefinition(
                source.CanonicalDefinition,
                source.DefinitionSha256,
                $"Agent revision {agentId:D}#{agentRevision}");
            var definitionNode = JsonNode.Parse(definition)!.AsObject();
            var executionRoles = definitionNode["execution_roles"]?.AsArray()
                                 ?? new JsonArray();
            var audience = definitionNode["audience"]?.AsArray() ?? new JsonArray();
            if (!JsonArrayContains(executionRoles, "worker")
                || !AgentAudience.Matches(
                    audience.Select(item => item!.GetValue<string>()),
                    role,
                    groups,
                    allowLegacyPublishedRoles: true))
            {
                await tx.RollbackAsync(ct);
                return InvalidState(
                    "Agent does not allow this caller or the worker execution role");
            }

            if (workflowId != Guid.Parse(AgentDefaults.RuntimeWorkflowId)
                || workflowRevision is not (AgentDefaults.PreviousRuntimeWorkflowRevision or AgentDefaults.RuntimeWorkflowRevision))
            {
                await tx.RollbackAsync(ct);
                return InvalidState("Agent-Runtime Workflow revision 無法執行");
            }

            var workflow = await conn.QuerySingleOrDefaultAsync<WorkflowRow>(new CommandDefinition(
                "SELECT wr.schema_version AS SchemaVersion, wr.definition_canonical AS CanonicalDefinition,"
                + " wr.definition_sha256 AS DefinitionSha256,"
                + " wr.compiler_contract_version AS CompilerContractVersion"
                + " FROM workflow w JOIN workflow_revision wr"
                + " ON wr.workflow_id=w.id AND wr.revision=@workflowRevision"
                + " WHERE w.id=@workflowId AND w.enabled AND w.kind='agent-runtime'"
                + " AND (w.tenant_id=@tenantId OR w.tenant_id=@systemTenant)"
                + " FOR SHARE OF w, wr",
                new
                {
                    workflowId,
                    workflowRevision,
                    tenantId,
                    systemTenant = AgentDefaults.SystemTenant,
                },
                tx,
                cancellationToken: ct));
            if (workflow is null)
            {
                await tx.RollbackAsync(ct);
                return InvalidState("Agent-Runtime Workflow revision 無法執行");
            }

            // The DB seed stores the hash of the checked-in immutable fixture bytes. Verify that
            // source first, then emit a recursively sorted definition/hash pair that Python can
            // independently recompute after JSON parsing.
            if (workflow.SchemaVersion != 1
                || workflow.CompilerContractVersion != WorkflowCompilerContracts.Current
                || workflow.CanonicalDefinition is null
                || !SkillHash.MatchesSha256(workflow.CanonicalDefinition, workflow.DefinitionSha256)
                || (workflowRevision == AgentDefaults.RuntimeWorkflowRevision
                    && !workflow.CanonicalDefinition.AsSpan().SequenceEqual(
                        Encoding.UTF8.GetBytes(AgentDefaults.RuntimeWorkflowDefinition))))
            {
                throw new InvalidOperationException(
                    $"Workflow revision hash mismatch：{workflowId:D}#{workflowRevision}");
            }

            var storedWorkflowCanonical = new UTF8Encoding(false, true)
                .GetString(workflow.CanonicalDefinition);
            _ = JsonNode.Parse(storedWorkflowCanonical)
                ?? throw new InvalidOperationException(
                    $"Workflow revision canonical JSON is invalid: {workflowId:D}#{workflowRevision}");

            var skillRows = (await conn.QueryAsync<SkillRunRow>(new CommandDefinition(
                "SELECT ars.position AS Position, s.id AS SkillId, s.name AS Name,"
                + " s.description AS Description, s.enabled AS Enabled,"
                + " sr.revision AS Revision, sr.kind AS Kind, sr.definition AS Definition,"
                + " sr.definition_sha256 AS DefinitionSha256, sr.package AS Package,"
                + " sr.package_sha256 AS PackageSha256"
                + " FROM agent_revision_skill ars"
                + " JOIN skill s ON s.id=ars.skill_id"
                + " JOIN skill_revision sr ON sr.skill_id=ars.skill_id AND sr.revision=ars.skill_revision"
                + " WHERE ars.agent_id=@agentId AND ars.agent_revision=@agentRevision AND ars.enabled"
                + " ORDER BY s.id"
                + " FOR SHARE OF s, sr",
                new { agentId, agentRevision }, tx, cancellationToken: ct))).AsList();
            if (skillRows.Any(s => !s.Enabled))
            {
                await tx.RollbackAsync(ct);
                return InvalidState("Agent 綁定的 Skill 已停用");
            }

            foreach (var skill in skillRows)
            {
                if (!string.Equals(
                        SkillHash.Sha256(skill.Definition),
                        skill.DefinitionSha256,
                        StringComparison.Ordinal)
                    || skill.PackageSha256 is not null
                    && !string.Equals(
                        SkillHash.Sha256(skill.Package ?? Array.Empty<byte>()),
                        skill.PackageSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Skill revision hash mismatch：{skill.Name}#{skill.Revision}");
                }
            }

            var agentSource = new PublishedAgentSnapshotSource(
                agentId,
                source.Name,
                agentRevision,
                definition,
                source.DefinitionSha256,
                workflowId,
                workflowRevision,
                skillRows.OrderBy(s => s.Position)
                    .Select(s => new AgentRevisionSkillInfo(
                        s.Name, s.Revision, s.Position, true))
                    .ToList(),
                source.PromptManifestRevision,
                source.PromptManifestSha256);
            var workflowSource = new WorkflowSnapshotSource(
                workflowId,
                workflowRevision,
                workflow.SchemaVersion,
                storedWorkflowCanonical,
                SkillHash.Sha256(storedWorkflowCanonical),
                workflow.CompilerContractVersion);
            var skills = skillRows.OrderBy(s => s.Position).Select(s => new SkillSnapshotSource(
                s.SkillId,
                s.Name,
                AgentRunSnapshotBuilder.SkillDescriptionOf(s.Definition),
                s.Revision,
                s.Kind,
                s.Definition,
                s.DefinitionSha256,
                s.PackageSha256 ?? (s.Package is null ? null : SkillHash.Sha256(s.Package)))).ToList();
            var snapshotErrors = AgentRunSnapshotBuilder.ValidateExecutionContract(
                agentSource, workflowSource, skills, tenantId, userId, role, groups);
            if (snapshotErrors.Count > 0)
            {
                await tx.RollbackAsync(ct);
                return InvalidState(
                    $"Agent execution snapshot contract 無效：{snapshotErrors[0].Message}");
            }

            var runId = Guid.NewGuid();
            var snapshot = AgentRunSnapshotBuilder.Build(
                runId,
                tenantId,
                userId,
                role,
                groups,
                capabilityClaims,
                agentSource,
                workflowSource,
                skills);
            if (snapshot.CanonicalByteLength
                > AgentExecutionContract.MaxSnapshotCanonicalBytes)
            {
                await tx.RollbackAsync(ct);
                return InvalidState(
                    $"Agent execution snapshot exceeds {AgentExecutionContract.MaxSnapshotCanonicalBytes} canonical UTF-8 bytes");
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO agent_run"
                + " (id,tenant_id,root_run_id,run_kind,user_id,caller_role,"
                + " agent_id,agent_revision,"
                + " workflow_id,workflow_revision,execution_snapshot,"
                + " execution_snapshot_canonical,snapshot_sha256,status,state_version,deadline_at)"
                + " VALUES (@runId,@tenantId,@runId,'direct-agent',@userId,@role,"
                + " @agentId,@agentRevision,"
                + " @workflowId,@workflowRevision,CAST(@snapshot AS jsonb),"
                + " @snapshotCanonical,@snapshotHash,'queued',1,"
                + " clock_timestamp()+make_interval(secs=>@effectiveTimeoutSeconds))",
                new
                {
                    runId,
                    tenantId,
                    userId,
                    role,
                    agentId,
                    agentRevision,
                    workflowId,
                    workflowRevision,
                    snapshot = snapshot.StoredSnapshot,
                    snapshotCanonical = snapshot.CanonicalBytes,
                    snapshotHash = snapshot.SnapshotHash,
                    snapshot.EffectiveTimeoutSeconds,
                },
                tx,
                cancellationToken: ct));

            foreach (var skill in skills)
            {
                var position = skillRows.Single(s => s.SkillId == skill.SkillId).Position;
                await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO agent_run_skill"
                    + " (run_id,position,skill_id,skill_revision,skill_name,kind,definition_sha256,package_sha256)"
                    + " VALUES (@runId,@position,@SkillId,@Revision,@Name,@Kind,@DefinitionSha256,@PackageSha256)",
                    new
                    {
                        runId,
                        position,
                        skill.SkillId,
                        skill.Revision,
                        skill.Name,
                        skill.Kind,
                        skill.DefinitionSha256,
                        skill.PackageSha256,
                    },
                    tx,
                    cancellationToken: ct));
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO agent_run_event"
                + " (run_id,sequence,event_id,event_type,snapshot_sha256,payload)"
                + " VALUES (@runId,1,@eventId,'run_created',@snapshotHash,'{}'::jsonb);"
                + " UPDATE agent_run SET latest_event_sequence=1 WHERE id=@runId",
                new
                {
                    runId,
                    eventId = Guid.NewGuid(),
                    snapshotHash = snapshot.SnapshotHash,
                },
                tx,
                cancellationToken: ct));

            // Mint the claim only after the potentially lengthy snapshot transaction work. Use
            // clock_timestamp(), not PostgreSQL now(), because now() is fixed at transaction start.
            var commandId = Guid.NewGuid();
            var claimToken = NewToken();
            var commandInputHash = CanonicalCommandInputSha256(
                commandInput,
                "start",
                0,
                0,
                null);
            var claimExpiresAt = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
                "INSERT INTO agent_run_command"
                + " (id,tenant_id,user_id,run_id,command_type,idempotency_key_sha256,"
                + " request_sha256,command_input,command_input_sha256,dispatch_claim_owner,"
                + " dispatch_claim_token_sha256,dispatch_claim_expires_at,"
                + " dispatch_attempts,last_dispatch_at)"
                + " VALUES (@commandId,@tenantId,@userId,@runId,'start',@keyHash,@requestHash,"
                + " CAST(@commandInput AS jsonb),@commandInputHash,'platform',@claimTokenHash,"
                + " clock_timestamp()+interval '30 seconds',1,clock_timestamp())"
                + " RETURNING dispatch_claim_expires_at",
                new
                {
                    runId,
                    tenantId,
                    userId,
                    keyHash,
                    requestHash,
                    commandId,
                    commandInput,
                    commandInputHash,
                    claimTokenHash = SkillHash.Sha256(claimToken),
                },
                tx,
                cancellationToken: ct));
            var dispatch = new AgentRunCommandDispatch(
                commandId,
                claimToken,
                claimExpiresAt,
                1);
            await tx.CommitAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Success,
                (await GetAsync(tenantId, userId, runId, ct))!,
                Dispatch: dispatch);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return await FindCommandAsync(
                       tenantId, userId, "start", keyHash, requestHash, ct)
                   ?? new AgentRunWriteResult(
                       AgentRunWriteStatus.Conflict,
                       Message: "Idempotency-Key 競爭衝突");
        }
    }

    public async Task<AgentRunResponse?> GetAsync(
        string tenantId, string userId, Guid runId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM agent_run"
            + " WHERE tenant_id=@tenantId AND user_id=@userId AND id=@runId",
            new { tenantId, userId, runId }, cancellationToken: ct));
        return row is null ? null : ToResponse(row);
    }

    public async Task<string?> GetExecutionArtifactAsync(
        string tenantId, string userId, Guid runId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ArtifactRow>(new CommandDefinition(
            "SELECT execution_snapshot_canonical AS SnapshotCanonical,"
            + " snapshot_sha256 AS SnapshotHash,caller_role AS CallerRole"
            + " FROM agent_run WHERE tenant_id=@tenantId AND user_id=@userId AND id=@runId",
            new { tenantId, userId, runId }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        return AgentRunSnapshotBuilder.CreateExecutionArtifact(
            row.SnapshotCanonical,
            row.SnapshotHash);
    }

    public async Task<AgentRunEventsResponse?> GetEventsAsync(
        string tenantId,
        string userId,
        Guid runId,
        long afterSequence,
        int limit,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_run"
            + " WHERE tenant_id=@tenantId AND user_id=@userId AND id=@runId)",
            new { tenantId, userId, runId }, cancellationToken: ct));
        if (!exists)
        {
            return null;
        }

        var rows = (await conn.QueryAsync<EventRow>(new CommandDefinition(
            "SELECT sequence AS Sequence,event_id AS EventId,event_type AS EventType,"
            + " node_id AS NodeId,lease_generation AS LeaseGeneration,"
            + " event_cursor AS EventCursor,"
            + " snapshot_sha256 AS SnapshotHash,payload::text AS Payload,"
            + " created_at AS CreatedAt FROM agent_run_event"
            + " WHERE run_id=@runId AND sequence>@afterSequence"
            + " ORDER BY sequence LIMIT @limit",
            new { runId, afterSequence, limit }, cancellationToken: ct))).AsList();
        var events = rows.Select(ToEventResponse).ToList();
        return new AgentRunEventsResponse(
            runId, events, events.Count == 0 ? afterSequence : events[^1].Sequence);
    }

    public Task<AgentRunWriteResult> ResumeAsync(
        string tenantId,
        string userId,
        Guid runId,
        string message,
        long expectedCheckpointVersion,
        string idempotencyKey,
        CancellationToken ct)
        => CommandAsync(
            tenantId,
            userId,
            runId,
            "resume",
            idempotencyKey,
            SkillHash.Sha256($"{runId:D}\0{expectedCheckpointVersion}\0{message}"),
            row => JsonSerializer.Serialize(new
            {
                message,
                expected_checkpoint_version = expectedCheckpointVersion,
                expected_checkpoint_ref = row.CheckpointRef,
            }),
            async (conn, tx, row) =>
            {
                if (row.CancelRequested
                    || row.Status != AgentRunStatuses.WaitingInput
                    || row.CheckpointVersion != expectedCheckpointVersion
                    || !AgentRunCheckpointRef.IsValidPromotion(
                        row.CheckpointRef,
                        row.CheckpointGeneration))
                {
                    return InvalidState("run 不在可 resume 的 waiting_input checkpoint");
                }
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE agent_run SET status='queued',pending_input=NULL,"
                    + " state_version=state_version+1,updated_at=now() WHERE id=@runId",
                    new { runId }, tx, cancellationToken: ct));
                await AppendSystemEventAsync(conn, tx, row, "input_resumed", ct);
                return null;
            },
            ct);

    public Task<AgentRunWriteResult> CancelAsync(
        string tenantId,
        string userId,
        Guid runId,
        string? reason,
        string idempotencyKey,
        CancellationToken ct)
        => CommandAsync(
            tenantId,
            userId,
            runId,
            "cancel",
            idempotencyKey,
            SkillHash.Sha256($"{runId:D}\0{reason ?? string.Empty}"),
            _ => JsonSerializer.Serialize(new { reason }),
            async (conn, tx, row) =>
            {
                var deadlineCleanupExists = await conn.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM agent_run_command"
                        + " WHERE run_id=@runId AND command_type='deadline_cleanup')",
                        new { runId },
                        tx,
                        cancellationToken: ct));
                if (deadlineCleanupExists
                    && !AgentRunStatuses.Terminal.Contains(row.Status))
                {
                    return new AgentRunWriteResult(
                        AgentRunWriteStatus.Conflict,
                        ToResponse(row),
                        "deadline cleanup is already in progress");
                }
                if (AgentRunStatuses.Terminal.Contains(row.Status))
                {
                    return row.Status == AgentRunStatuses.Cancelled
                        ? new AgentRunWriteResult(
                            AgentRunWriteStatus.Replay,
                            ToResponse(row),
                            Replayed: true)
                        : InvalidState("terminal run 不可取消");
                }
                if (row.CancelRequested)
                {
                    return new AgentRunWriteResult(
                        AgentRunWriteStatus.Replay,
                        ToResponse(row),
                        Replayed: true);
                }
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE agent_run SET cancel_requested_at=clock_timestamp(),"
                    + " cancel_requested_by=@userId,state_version=state_version+1,"
                    + " updated_at=clock_timestamp() WHERE id=@runId",
                    new { runId, userId }, tx, cancellationToken: ct));
                await AppendSystemEventAsync(conn, tx, row, "cancel_requested", ct);
                return null;
            },
            ct);

    public async Task<AgentRunWriteResult> TransitionAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunTransitionRequest request,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await LockRunAsync(conn, tx, tenantId, userId, runId, ct);
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return NotFound("找不到 Agent run");
        }
        var databaseNow = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT clock_timestamp()", transaction: tx, cancellationToken: ct));
        var deadlineElapsed = row.DeadlineAt <= databaseNow;
        var deadlineCleanupTarget = deadlineElapsed
            ? await DeadlineCleanupTargetAsync(conn, tx, row, ct)
            : null;
        var deadlineCleanupLease = deadlineCleanupTarget is
            AgentRunStatuses.Failed or AgentRunStatuses.Cancelled;
        if (row.StateVersion != request.ExpectedVersion)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict, ToResponse(row), "run state version 衝突");
        }
        var deadlineCleanupTransition =
            deadlineCleanupLease
            && request.ToStatus == deadlineCleanupTarget;
        var checkpointOnlyPromotion =
            row.Status == AgentRunStatuses.Running
            && request.ToStatus == AgentRunStatuses.Running;
        if (row.CancelRequested
            && request.ToStatus != AgentRunStatuses.Cancelled
            && !deadlineCleanupTransition)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("已要求取消的 run 只能轉為 cancelled");
        }
        if (request.ToStatus is null
            || !AgentRunStatuses.All.Contains(request.ToStatus)
            || !AgentRunStatuses.CanTransition(row.Status, request.ToStatus)
            && !deadlineCleanupTransition
            && !checkpointOnlyPromotion)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("不合法的 run 狀態轉移");
        }
        var terminal = AgentRunStatuses.Terminal.Contains(request.ToStatus!);
        var stateVersionCeiling =
            terminal ? long.MaxValue : long.MaxValue - 1;
        if (row.StateVersion < 0
            || row.StateVersion >= stateVersionCeiling)
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "run state version has insufficient lifecycle headroom");
        }
        if (!LeaseMatches(
                row,
                request.LeaseToken,
                request.LeaseGeneration,
                databaseNow))
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict, ToResponse(row), "run lease 無效或已過期");
        }
        if (request.ExpectedEventAckCursor is not long expectedEventAckCursor
            || expectedEventAckCursor != row.EventAckCursor)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict,
                ToResponse(row),
                "event acknowledgement cursor changed");
        }
        if (deadlineCleanupTransition)
        {
            var deadlineAuditPersisted = await conn.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM agent_run_event"
                    + " WHERE run_id=@runId AND lease_generation=@leaseGeneration"
                    + " AND event_type='deadline_exceeded')",
                    new { runId, request.LeaseGeneration },
                    tx,
                    cancellationToken: ct));
            var expectedErrorCode =
                request.ToStatus == AgentRunStatuses.Failed
                    ? "deadline_exceeded"
                    : null;
            if (!deadlineAuditPersisted
                || HasJsonValue(request.PendingInput)
                || HasJsonValue(request.Result)
                || !string.Equals(
                    request.ErrorCode,
                    expectedErrorCode,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.ErrorMessage,
                    "Agent runtime exceeded its authoritative deadline.",
                    StringComparison.Ordinal))
            {
                await tx.RollbackAsync(ct);
                return InvalidState(
                    "deadline cleanup requires one persisted sanitized audit and fixed terminal metadata");
            }
        }

        var checkpointRef = request.CheckpointRef;
        var hasCheckpointRef = checkpointRef is not null;
        var hasCheckpointVersion = request.CheckpointVersion is not null;
        if (hasCheckpointRef != hasCheckpointVersion)
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "checkpoint_ref and checkpoint_version must be promoted together");
        }
        var promotionRequired = request.ToStatus is
            AgentRunStatuses.WaitingInput
            or AgentRunStatuses.Completed
            or AgentRunStatuses.Failed
            or AgentRunStatuses.Cancelled;
        var promotesCheckpoint = hasCheckpointRef && hasCheckpointVersion;
        var generationOnlyRunningPromotion = promotesCheckpoint
            && request.ToStatus == AgentRunStatuses.Running
            && request.LeaseGeneration > row.CheckpointGeneration
            && request.CheckpointVersion == row.CheckpointVersion;
        if (promotionRequired && !promotesCheckpoint)
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "waiting or terminal transitions require a checkpoint promotion");
        }
        if (promotesCheckpoint
            && (!AgentRunCheckpointRef.IsValidPromotion(
                    checkpointRef,
                    request.LeaseGeneration)
                || request.CheckpointVersion is not long promotedCheckpointVersion
                || (promotedCheckpointVersion <= row.CheckpointVersion
                    && !generationOnlyRunningPromotion)))
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "checkpoint promotion must advance with a canonical v2 reference for the active lease generation");
        }
        if (promotesCheckpoint
            && !terminal
            && request.CheckpointVersion == long.MaxValue)
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "nonterminal checkpoint promotion must preserve one terminal version");
        }
        if (checkpointOnlyPromotion
            && (!promotesCheckpoint
                || HasJsonValue(request.PendingInput)
                || HasJsonValue(request.Result)
                || request.ErrorCode is not null
                || request.ErrorMessage is not null))
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "running checkpoint promotion cannot mutate status, payload, result, or error metadata");
        }
        if (deadlineElapsed && !deadlineCleanupTransition)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("run deadline has elapsed");
        }
        if (request.ToStatus == AgentRunStatuses.WaitingInput
            && (checkpointRef is null
                || request.CheckpointVersion is not long waitingCheckpointVersion
                || waitingCheckpointVersion <= row.CheckpointVersion))
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "waiting_input requires a nonblank checkpoint_ref and an advanced checkpoint_version");
        }
        if (request.CheckpointVersion is long checkpointVersion
            && checkpointVersion < row.CheckpointVersion)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("checkpoint_version 不可倒退");
        }
        if (!WithinJsonLimit(request.PendingInput, 64 * 1024)
            || !WithinJsonLimit(request.Result, 1024 * 1024))
        {
            await tx.RollbackAsync(ct);
            return InvalidState("pending_input 或 result 超過上限");
        }

        if (checkpointOnlyPromotion)
        {
            var checkpointChanged = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_run SET state_version=state_version+1,"
                + " checkpoint_generation=@leaseGeneration,"
                + " checkpoint_ref=@checkpointRef,"
                + " checkpoint_version=@checkpointVersion,"
                + " updated_at=clock_timestamp() WHERE id=@runId"
                + " AND status='running'"
                + " AND state_version=@expectedVersion"
                + " AND lease_generation=@leaseGeneration"
                + " AND lease_token_sha256=@leaseTokenHash"
                + " AND lease_expires_at>clock_timestamp()"
                + " AND event_ack_cursor=@expectedEventAckCursor"
                + " AND deadline_at>clock_timestamp()",
                new
                {
                    runId,
                    request.ExpectedVersion,
                    request.LeaseGeneration,
                    leaseTokenHash = SkillHash.Sha256(request.LeaseToken!),
                    expectedEventAckCursor,
                    checkpointRef,
                    checkpointVersion = request.CheckpointVersion!.Value,
                },
                tx,
                cancellationToken: ct));
            if (checkpointChanged != 1)
            {
                await tx.RollbackAsync(ct);
                return new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict,
                    ToResponse(row),
                    "run fencing state changed");
            }
            await tx.CommitAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Success,
                (await GetAsync(tenantId, userId, runId, ct))!);
        }

        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run SET status=@toStatus,state_version=state_version+1,"
            + " checkpoint_generation=CASE WHEN @promotesCheckpoint"
            + " THEN @leaseGeneration ELSE checkpoint_generation END,"
            + " checkpoint_ref=CASE WHEN @promotesCheckpoint"
            + " THEN @checkpointRef ELSE checkpoint_ref END,"
            + " checkpoint_version=COALESCE(@checkpointVersion,checkpoint_version),"
            + " pending_input=CAST(@pendingInput AS jsonb),result=CAST(@result AS jsonb),"
            + " error_code=@errorCode,error_message=@errorMessage,"
            + " started_at=CASE WHEN @toStatus='running'"
            + " THEN COALESCE(started_at,clock_timestamp()) ELSE started_at END,"
            + " completed_at=CASE WHEN @terminal THEN clock_timestamp() ELSE completed_at END,"
             + " lease_owner=CASE WHEN @releaseLease THEN NULL ELSE lease_owner END,"
             + " lease_token_sha256=CASE WHEN @releaseLease THEN NULL ELSE lease_token_sha256 END,"
             + " lease_expires_at=CASE WHEN @releaseLease THEN NULL ELSE lease_expires_at END,"
             + " lease_command_id=CASE WHEN @releaseLease THEN NULL ELSE lease_command_id END,"
            + " updated_at=clock_timestamp() WHERE id=@runId"
            + " AND state_version=@expectedVersion"
            + " AND lease_generation=@leaseGeneration"
            + " AND lease_token_sha256=@leaseTokenHash"
            + " AND lease_expires_at>clock_timestamp()"
            + " AND event_ack_cursor=@expectedEventAckCursor"
             + " AND (deadline_at>clock_timestamp() OR @deadlineCleanupTransition)",
            new
            {
                runId,
                toStatus = request.ToStatus,
                request.ExpectedVersion,
                request.LeaseGeneration,
                leaseTokenHash = SkillHash.Sha256(request.LeaseToken!),
                expectedEventAckCursor,
                checkpointRef,
                request.CheckpointVersion,
                promotesCheckpoint,
                pendingInput = JsonText(request.PendingInput),
                result = JsonText(request.Result),
                errorCode = Normalize(request.ErrorCode, 100),
                errorMessage = Normalize(request.ErrorMessage, 500),
                terminal,
                releaseLease = terminal
                    || request.ToStatus == AgentRunStatuses.WaitingInput,
                deadlineCleanupTransition,
            },
            tx,
            cancellationToken: ct));
        if (changed != 1)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict,
                ToResponse(row),
                "run fencing state changed");
        }
        if (terminal)
        {
            await SynchronizeOrchestratorChildTerminalAsync(
                conn,
                tx,
                row,
                request.ToStatus!,
                request.Result,
                request.ErrorCode,
                ct);
        }
        await tx.CommitAsync(ct);
        return new AgentRunWriteResult(
            AgentRunWriteStatus.Success,
            (await GetAsync(tenantId, userId, runId, ct))!);
    }

    public async Task<AgentRunWriteResult> AppendEventsAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunEventsAppendRequest request,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await LockRunAsync(conn, tx, tenantId, userId, runId, ct);
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return NotFound("找不到 Agent run");
        }
        var databaseNow = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT clock_timestamp()", transaction: tx, cancellationToken: ct));
        var deadlineElapsed = row.DeadlineAt <= databaseNow;
        var deadlineCleanupTarget = deadlineElapsed
            ? await DeadlineCleanupTargetAsync(conn, tx, row, ct)
            : null;
        var deadlineCleanupLease = deadlineCleanupTarget is
            AgentRunStatuses.Failed or AgentRunStatuses.Cancelled;
        if (deadlineElapsed && !deadlineCleanupLease)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("run deadline has elapsed");
        }
        if (request.ExpectedVersion != row.StateVersion)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict, ToResponse(row), "run state version 衝突");
        }
        if (AgentRunStatuses.Terminal.Contains(row.Status))
        {
            await tx.RollbackAsync(ct);
            return InvalidState("terminal run 不接受 events");
        }
        if (!LeaseMatches(
                row,
                request.LeaseToken,
                request.LeaseGeneration,
                databaseNow))
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict, ToResponse(row), "run lease 無效或已過期");
        }
        if (request.Events is null)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("events 不可為 null");
        }
        if (request.Events.Count is < 1 or > 100)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("單次最多追加 100 個 events");
        }
        if (deadlineElapsed
            && request.Events.Any(item =>
                !DeadlineCleanupAuditEventTypes.Contains(
                    item.EventType?.Trim() ?? string.Empty)))
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "deadline cleanup accepts only sanitized deadline audit events");
        }
        long eventCursorEnd;
        try
        {
            eventCursorEnd = checked(request.EventCursorStart + request.Events.Count);
        }
        catch (OverflowException)
        {
            await tx.RollbackAsync(ct);
            return InvalidState("event cursor is out of range");
        }
        if (request.EventCursorStart < 0
            || request.EventCursorStart > row.EventAckCursor
            || request.EventCursorStart < row.EventAckCursor
            && eventCursorEnd > row.EventAckCursor)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict,
                ToResponse(row),
                "event cursor has a gap or partial overlap");
        }
        if (row.CancelRequested
            && !deadlineCleanupLease
            && request.Events.Any(item =>
                !CancelAuditEventTypes.Contains(item.EventType?.Trim() ?? string.Empty)))
        {
            await tx.RollbackAsync(ct);
            return InvalidState(
                "cancel-requested run 只接受受限的 runtime audit events");
        }
        foreach (var item in request.Events)
        {
            if (item.EventId == Guid.Empty
                || string.IsNullOrWhiteSpace(item.EventType)
                || item.EventType.Trim().Length > 100
                || !string.Equals(item.SnapshotHash, row.SnapshotHash, StringComparison.Ordinal)
                || !IsSafePayload(item.Payload))
            {
                await tx.RollbackAsync(ct);
                return InvalidState("event id/type/snapshot_hash/payload 無效");
            }
        }

        if (request.EventCursorStart < row.EventAckCursor)
        {
            for (var index = 0; index < request.Events.Count; index++)
            {
                var item = request.Events[index];
                var prior = await conn.QuerySingleOrDefaultAsync<EventIdentityRow>(
                    new CommandDefinition(
                        "SELECT event_id AS EventId,event_cursor AS EventCursor,"
                        + " event_type AS EventType,node_id AS NodeId,"
                        + " snapshot_sha256 AS SnapshotHash,payload::text AS Payload"
                        + " FROM agent_run_event"
                        + " WHERE run_id=@runId AND event_cursor=@eventCursor",
                        new
                        {
                            runId,
                            eventCursor = request.EventCursorStart + index,
                        },
                        tx,
                        cancellationToken: ct));
                if (prior is null
                    || prior.EventId != item.EventId
                    || !EventReplayMatches(prior, item, row.SnapshotHash))
                {
                    await tx.RollbackAsync(ct);
                    return new AgentRunWriteResult(
                        AgentRunWriteStatus.Conflict,
                        ToResponse(row),
                        "event cursor replay does not match persisted events");
                }
            }
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Replay,
                ToResponse(row),
                Replayed: true);
        }

        var sequence = row.LatestEventSequence;
        var nextEventCursor = request.EventCursorStart;
        foreach (var item in request.Events)
        {
            var eventCursor = nextEventCursor++;
            var prior = await conn.QuerySingleOrDefaultAsync<EventIdentityRow>(new CommandDefinition(
                "SELECT event_id AS EventId,event_cursor AS EventCursor,"
                + " event_type AS EventType,node_id AS NodeId,"
                + " snapshot_sha256 AS SnapshotHash,payload::text AS Payload"
                + " FROM agent_run_event WHERE run_id=@runId AND event_id=@eventId",
                new { runId, item.EventId }, tx, cancellationToken: ct));
            if (prior is not null)
            {
                if (prior.EventCursor != eventCursor
                    || !EventReplayMatches(prior, item, row.SnapshotHash))
                {
                    await tx.RollbackAsync(ct);
                    return new AgentRunWriteResult(
                        AgentRunWriteStatus.Conflict,
                        ToResponse(row),
                        "event_id 已用於不同內容");
                }
                continue;
            }
            sequence++;
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO agent_run_event"
                + " (run_id,sequence,event_id,event_type,node_id,lease_generation,"
                + " event_cursor,snapshot_sha256,payload)"
                + " VALUES (@runId,@sequence,@eventId,@eventType,@nodeId,@leaseGeneration,"
                + " @eventCursor,@snapshotHash,CAST(@payload AS jsonb))",
                new
                {
                    runId,
                    sequence,
                    item.EventId,
                    eventType = item.EventType!.Trim(),
                    nodeId = Normalize(item.NodeId, 200),
                    request.LeaseGeneration,
                    eventCursor,
                    snapshotHash = row.SnapshotHash,
                    payload = JsonText(item.Payload) ?? "{}",
                },
                tx,
                cancellationToken: ct));
        }
        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run SET latest_event_sequence=@sequence,"
            + " event_ack_cursor=@eventCursorEnd,updated_at=clock_timestamp()"
            + " WHERE id=@runId"
            + " AND state_version=@expectedVersion"
            + " AND lease_generation=@leaseGeneration"
            + " AND lease_token_sha256=@leaseTokenHash"
            + " AND lease_expires_at>clock_timestamp()"
            + " AND (deadline_at>clock_timestamp() OR @deadlineCleanupLease)"
            + " AND event_ack_cursor=@eventCursorStart",
            new
            {
                runId,
                sequence,
                eventCursorEnd,
                request.ExpectedVersion,
                request.LeaseGeneration,
                leaseTokenHash = SkillHash.Sha256(request.LeaseToken!),
                request.EventCursorStart,
                deadlineCleanupLease,
            },
            tx,
            cancellationToken: ct));
        if (changed != 1)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict,
                ToResponse(row),
                "run fencing or event cursor state changed");
        }
        await tx.CommitAsync(ct);
        return new AgentRunWriteResult(
            AgentRunWriteStatus.Success,
            (await GetAsync(tenantId, userId, runId, ct))!);
    }

    public async Task<AgentRunLeaseResult> ClaimLeaseAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunLeaseRequest request,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await LockRunAsync(conn, tx, tenantId, userId, runId, ct);
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunLeaseResult(AgentRunWriteStatus.NotFound);
        }
        var now = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT clock_timestamp()", transaction: tx, cancellationToken: ct));
        if (row.StateVersion != request.ExpectedVersion)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunLeaseResult(
                AgentRunWriteStatus.Conflict, Message: "run state version 衝突");
        }
        if (AgentRunStatuses.Terminal.Contains(row.Status)
            || row.DeadlineAt <= now
            || row.StateVersion < 0
            || row.StateVersion >= long.MaxValue - 1
            || string.IsNullOrWhiteSpace(request.Owner)
            || request.DurationSeconds is < 5 or > 900
            || row.LeaseExpiresAt is DateTime expiry && expiry > now
            && !string.Equals(row.LeaseOwner, request.Owner.Trim(), StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new AgentRunLeaseResult(
                AgentRunWriteStatus.InvalidState, Message: "run 無法取得 lease");
        }

        var activeSameOwner = row.LeaseExpiresAt is DateTime activeExpiry
                              && activeExpiry > now
                              && string.Equals(
                                  row.LeaseOwner,
                                  request.Owner.Trim(),
                                  StringComparison.Ordinal);
        if (!activeSameOwner && row.LeaseGeneration == long.MaxValue)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunLeaseResult(
                AgentRunWriteStatus.InvalidState,
                Message: "run lease generation is exhausted");
        }
        var leaseGeneration = activeSameOwner
            ? row.LeaseGeneration
            : checked(row.LeaseGeneration + 1);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "UPDATE agent_run SET lease_owner=@owner,lease_token_sha256=@tokenHash,"
             + " lease_generation=@leaseGeneration,"
             + " lease_command_id=NULL,"
            + " lease_expires_at=clock_timestamp()+make_interval(secs=>@durationSeconds),"
            + " state_version=state_version+1,updated_at=clock_timestamp()"
            + " WHERE id=@runId AND state_version=@expectedVersion"
            + " AND deadline_at>clock_timestamp()"
            + " RETURNING lease_expires_at",
            new
            {
                runId,
                owner = request.Owner.Trim(),
                tokenHash = SkillHash.Sha256(token),
                leaseGeneration,
                request.DurationSeconds,
                request.ExpectedVersion,
            },
            tx,
            cancellationToken: ct));
        if (expiresAt is null)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunLeaseResult(
                AgentRunWriteStatus.Conflict,
                Message: "run fencing state changed");
        }
        await tx.CommitAsync(ct);
        var updated = (await GetAsync(tenantId, userId, runId, ct))!;
        return new AgentRunLeaseResult(
            AgentRunWriteStatus.Success,
            new AgentRunLeaseResponse(
                token,
                updated.LeaseGeneration,
                expiresAt.Value,
                updated.CheckpointGeneration,
                updated.CheckpointRef,
                updated.CheckpointVersion,
                updated.EventAckCursor,
                updated));
    }

    public async Task<AgentRunCommandClaimResult> ClaimCommandAsync(
        string tenantId,
        string userId,
        Guid runId,
        Guid commandId,
        AgentRunCommandClaimRequest request,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await LockRunAsync(conn, tx, tenantId, userId, runId, ct);
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(AgentRunWriteStatus.NotFound);
        }

        var command = await conn.QuerySingleOrDefaultAsync<CommandClaimRow>(
            new CommandDefinition(
                "SELECT id AS Id,command_type AS Type,command_input::text AS Input,"
                + " command_input_sha256 AS InputHash,"
                + " dispatch_claim_owner AS DispatchClaimOwner,"
                + " dispatch_claim_token_sha256 AS DispatchClaimTokenHash,"
                + " dispatch_claim_expires_at AS DispatchClaimExpiresAt,"
                + " dispatch_attempts AS DispatchAttempts,"
                + " dispatch_completed_at AS DispatchCompletedAt"
                + " FROM agent_run_command"
                + " WHERE id=@commandId AND run_id=@runId"
                + " AND tenant_id=@tenantId AND user_id=@userId FOR UPDATE",
                new { tenantId, userId, runId, commandId },
                tx,
                cancellationToken: ct));
        if (command is null)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(AgentRunWriteStatus.NotFound);
        }
        var latestCommandId = await conn.QuerySingleAsync<Guid>(new CommandDefinition(
            "SELECT id FROM agent_run_command WHERE run_id=@runId"
            + " ORDER BY (command_type='cancel' AND dispatch_completed_at IS NULL) DESC,"
            + " command_sequence DESC LIMIT 1",
            new { runId },
            tx,
            cancellationToken: ct));
        if (latestCommandId != commandId)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Conflict,
                Message: "command has been superseded");
        }
        if (!AgentRunCommandInput.TryParseAndValidate(
                command.Input,
                command.Type,
                row.CheckpointGeneration,
                row.CheckpointVersion,
                row.CheckpointRef,
                AgentRunCommandValidationMode.DirectClaim,
                out var validatedInput,
                out _)
            || !AgentRunCommandInput.MatchesCanonicalSha256(
                validatedInput,
                command.Type,
                command.InputHash))
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.InvalidState,
                Message: "command input is invalid");
        }

        var artifactRow = await conn.QuerySingleAsync<ArtifactRow>(new CommandDefinition(
            "SELECT execution_snapshot_canonical AS SnapshotCanonical,"
            + " snapshot_sha256 AS SnapshotHash,caller_role AS CallerRole"
            + " FROM agent_run WHERE id=@runId",
            new { runId },
            tx,
            cancellationToken: ct));
        var metadataOnly = command.Type == "cancel";
        var role = artifactRow.CallerRole;
        JsonElement snapshotEnvelope = default;
        if (metadataOnly)
        {
            if (!HasValidPinnedIdentity(tenantId, userId, role))
            {
                await tx.RollbackAsync(ct);
                return new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.InvalidState,
                    Message: "run caller identity is invalid");
            }
        }
        else
        {
            var canonicalSnapshot = AgentRunSnapshotBuilder.ReadAuthoritativeSnapshot(
                artifactRow.SnapshotCanonical,
                artifactRow.SnapshotHash);
            using var snapshotDocument = JsonDocument.Parse(canonicalSnapshot);
            var caller = snapshotDocument.RootElement.GetProperty("caller");
            var snapshotTenantId = caller.GetProperty("tenant_id").GetString();
            var snapshotUserId = caller.GetProperty("user_id").GetString();
            role = caller.GetProperty("role").GetString();
            if (!HasValidPinnedIdentity(snapshotTenantId, snapshotUserId, role)
                || !string.Equals(snapshotTenantId, tenantId, StringComparison.Ordinal)
                || !string.Equals(snapshotUserId, userId, StringComparison.Ordinal)
                || !string.Equals(
                    role,
                    artifactRow.CallerRole,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Run snapshot has invalid or mismatched caller identity");
            }
            snapshotEnvelope = JsonDocument.Parse(
                AgentRunSnapshotBuilder.CreateExecutionArtifact(
                    artifactRow.SnapshotCanonical,
                    artifactRow.SnapshotHash)).RootElement.Clone();
        }

        if (command.DispatchCompletedAt is not null)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Replay);
        }

        var now = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT clock_timestamp()", transaction: tx, cancellationToken: ct));
        var workerId = request.WorkerId!.Trim();
        if (AgentRunStatuses.Terminal.Contains(row.Status)
            || row.DeadlineAt <= now
            || row.CancelRequested && command.Type != "cancel")
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.InvalidState,
                Message: "run is not executable for this command");
        }
        if (command.DispatchClaimExpiresAt is DateTime commandClaimExpiry
            && commandClaimExpiry > now
            && !string.Equals(
                command.DispatchClaimOwner,
                workerId,
                StringComparison.Ordinal)
            && !string.Equals(
                command.DispatchClaimOwner,
                "platform",
                StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Conflict,
                Message: "command is claimed by another worker");
        }
        var forceTakeover = command.Type == "cancel";
        var activeSameOwner = !forceTakeover
                              && row.LeaseExpiresAt is DateTime leaseExpiry
                              && leaseExpiry > now
                              && string.Equals(
                                  row.LeaseOwner,
                                  workerId,
                                  StringComparison.Ordinal);
        var resumeFromReleasedWait = command.Type == "resume"
                                     && row.LeaseExpiresAt is null
                                     && row.CheckpointGeneration == row.LeaseGeneration
                                     && !string.IsNullOrWhiteSpace(row.CheckpointRef);
        var reuseGeneration = activeSameOwner || resumeFromReleasedWait;
        if (row.LeaseExpiresAt is DateTime activeLeaseExpiry
            && activeLeaseExpiry > now
            && !activeSameOwner
            && !forceTakeover)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Conflict,
                Message: "run lease is owned by another worker");
        }
        if (!reuseGeneration && row.LeaseGeneration == long.MaxValue)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.InvalidState,
                Message: "run lease generation is exhausted");
        }

        var leaseGeneration = reuseGeneration
            ? row.LeaseGeneration
            : row.LeaseGeneration + 1;
        var leaseToken = NewToken();
        var claimToken = NewToken();
        var leaseExpiresAt = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "UPDATE agent_run SET lease_owner=@workerId,"
             + " lease_token_sha256=@leaseTokenHash,"
             + " lease_generation=@leaseGeneration,"
             + " lease_command_id=@commandId,"
            + " lease_expires_at=clock_timestamp()+make_interval(secs=>@leaseSeconds),"
            + " state_version=state_version+1,updated_at=clock_timestamp()"
             + " WHERE id=@runId AND state_version=@expectedVersion"
             + " AND deadline_at>clock_timestamp()"
             + " AND (lease_expires_at IS NULL OR lease_expires_at<=clock_timestamp()"
             + " OR lease_owner=@workerId OR @forceTakeover)"
            + " RETURNING lease_expires_at",
            new
            {
                runId,
                workerId,
                leaseTokenHash = SkillHash.Sha256(leaseToken),
                leaseGeneration,
                leaseSeconds = request.LeaseSeconds,
                expectedVersion = row.StateVersion,
                forceTakeover,
                commandId,
            },
            tx,
            cancellationToken: ct));
        if (leaseExpiresAt is null)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Conflict,
                Message: "run fencing state changed");
        }
        var attempt = command.DispatchAttempts + 1;
        var commandChanged = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_command SET dispatch_claim_owner=@workerId,"
            + " dispatch_claim_token_sha256=@claimTokenHash,"
            + " dispatch_claim_expires_at=@leaseExpiresAt,"
            + " dispatch_attempts=@attempt,last_dispatch_at=clock_timestamp()"
            + " WHERE id=@commandId AND dispatch_completed_at IS NULL",
            new
            {
                workerId,
                claimTokenHash = SkillHash.Sha256(claimToken),
                leaseExpiresAt,
                attempt,
                commandId,
            },
            tx,
            cancellationToken: ct));
        if (commandChanged != 1)
        {
            await tx.RollbackAsync(ct);
            return new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Conflict,
                Message: "command claim changed");
        }

        await tx.CommitAsync(ct);
        return new AgentRunCommandClaimResult(
            AgentRunWriteStatus.Success,
            BuildCommandItem(
                command,
                row with
                {
                    StateVersion = row.StateVersion + 1,
                    LeaseGeneration = leaseGeneration,
                },
                tenantId,
                userId,
                role!,
                metadataOnly ? default : validatedInput,
                snapshotEnvelope,
                leaseToken,
                leaseExpiresAt.Value,
                claimToken,
                leaseExpiresAt.Value,
                attempt,
                metadataOnly ? AgentRunStatuses.Cancelled : null));
    }

    public async Task<AgentRunDispatchCompleteStatus> CompleteDispatchAsync(
        string tenantId,
        string userId,
        Guid runId,
        Guid commandId,
        string claimToken,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var lockedRunId = await conn.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM agent_run"
            + " WHERE id=@runId AND tenant_id=@tenantId AND user_id=@userId"
            + " FOR UPDATE",
            new { tenantId, userId, runId, commandId },
            tx,
            cancellationToken: ct));
        if (lockedRunId is null)
        {
            await tx.RollbackAsync(ct);
            return AgentRunDispatchCompleteStatus.NotFound;
        }

        var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM agent_run_command"
            + " WHERE id=@commandId AND run_id=@runId)",
            new { runId, commandId },
            tx,
            cancellationToken: ct));
        if (!exists)
        {
            await tx.RollbackAsync(ct);
            return AgentRunDispatchCompleteStatus.NotFound;
        }

        var changed = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_command SET dispatch_completed_at=now(),"
            + " dispatch_claim_owner=NULL,dispatch_claim_token_sha256=NULL,"
            + " dispatch_claim_expires_at=NULL"
            + " WHERE id=@commandId AND run_id=@runId"
            + " AND dispatch_completed_at IS NULL"
            + " AND dispatch_claim_token_sha256=@claimTokenHash",
            new
            {
                runId,
                commandId,
                claimTokenHash = SkillHash.Sha256(claimToken),
            },
            tx,
            cancellationToken: ct));
        await tx.CommitAsync(ct);
        return changed == 1
            ? AgentRunDispatchCompleteStatus.Success
            : AgentRunDispatchCompleteStatus.Conflict;
    }

    public async Task<AgentRunRecoveryClaimResponse> ClaimRecoveryAsync(
        AgentRunRecoveryClaimRequest request,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var databaseNow = await conn.ExecuteScalarAsync<DateTime>(new CommandDefinition(
            "SELECT clock_timestamp()", transaction: tx, cancellationToken: ct));
        var recoveryBefore = databaseNow.AddSeconds(-30);
        var rows = (await conn.QueryAsync<RecoveryCandidateRow>(new CommandDefinition(
            "SELECT c.id AS CommandId,r.id AS RunId,c.command_type AS CommandType,"
            + " c.command_input::text AS Input,"
            + " c.command_input_sha256 AS InputHash,"
            + " c.dispatch_attempts AS DispatchAttempts,"
            + " c.dispatch_claim_token_sha256 AS DispatchClaimTokenHash,"
            + " c.dispatch_claim_expires_at AS DispatchClaimExpiresAt,"
            + " c.dispatch_completed_at AS DispatchCompletedAt,"
            + " c.execution_recovery_lease_token_sha256"
            + " AS ExecutionRecoveryLeaseTokenHash,"
            + " r.tenant_id AS TenantId,r.user_id AS UserId,"
            + " r.caller_role AS CallerRole,"
            + " r.execution_snapshot_canonical AS SnapshotCanonical,"
            + " r.snapshot_sha256 AS SnapshotHash,r.status AS RunStatus,"
            + " r.state_version AS StateVersion,"
            + " r.lease_generation AS LeaseGeneration,"
            + " r.checkpoint_generation AS CheckpointGeneration,"
            + " r.checkpoint_ref AS CheckpointRef,"
            + " r.checkpoint_version AS CheckpointVersion,"
            + " r.event_ack_cursor AS EventAckCursor,"
            + " r.latest_event_sequence AS LatestEventSequence,"
            + " r.deadline_at AS DeadlineAt,"
            + " (r.cancel_requested_at IS NOT NULL) AS CancelRequested,"
            + " r.lease_token_sha256 AS RunLeaseTokenHash,"
            + " r.lease_expires_at AS RunLeaseExpiresAt"
            + " FROM agent_run r"
            + " JOIN LATERAL (SELECT cmd.* FROM agent_run_command cmd"
            + " WHERE cmd.run_id=r.id"
            + " AND cmd.command_type IN ('start','resume','cancel','deadline_cleanup')"
            + " ORDER BY (cmd.command_type='deadline_cleanup') DESC,"
            + " (cmd.command_type='cancel') DESC,"
            + " cmd.command_sequence DESC LIMIT 1) c ON true"
            + " WHERE r.status IN ('queued','running','waiting_input','waiting_approval') AND ("
            + " (r.deadline_at<=@databaseNow AND ("
            + " c.command_type<>'deadline_cleanup'"
            + " OR (c.command_type='deadline_cleanup' AND ("
            + " (c.dispatch_completed_at IS NULL"
            + " AND (c.dispatch_claim_expires_at IS NULL"
            + " OR c.dispatch_claim_expires_at<=@databaseNow))"
            + " OR (c.dispatch_completed_at IS NOT NULL"
            + " AND (r.lease_expires_at IS NULL OR r.lease_expires_at<=@databaseNow)"
            + " AND (r.lease_token_sha256"
            + " IS DISTINCT FROM c.execution_recovery_lease_token_sha256"
            + " OR c.dispatch_completed_at<=@recoveryBefore)))))"
            + " ))"
            + " OR (r.deadline_at>@databaseNow AND ("
            + " (r.cancel_requested_at IS NOT NULL"
            + " AND c.command_type='cancel'"
            + " AND ((c.dispatch_completed_at IS NULL"
            + " AND (c.dispatch_claim_expires_at IS NULL"
            + " OR c.dispatch_claim_expires_at<=@databaseNow))"
            + " OR (c.dispatch_completed_at IS NOT NULL"
            + " AND (r.lease_expires_at IS NULL OR r.lease_expires_at<=@databaseNow)"
            + " AND (r.lease_token_sha256"
            + " IS DISTINCT FROM c.execution_recovery_lease_token_sha256"
            + " OR c.dispatch_completed_at<=@recoveryBefore))))"
            + " OR (r.cancel_requested_at IS NULL"
            + " AND r.status IN ('queued','running')"
            + " AND c.command_type IN ('start','resume')"
            + " AND (r.lease_expires_at IS NULL OR r.lease_expires_at<=@databaseNow)"
            + " AND ((c.dispatch_completed_at IS NULL"
            + " AND (c.dispatch_claim_expires_at IS NULL"
            + " OR c.dispatch_claim_expires_at<=@databaseNow))"
            + " OR (c.dispatch_completed_at IS NOT NULL"
            + " AND (r.lease_token_sha256"
            + " IS DISTINCT FROM c.execution_recovery_lease_token_sha256"
            + " OR c.dispatch_completed_at<=@recoveryBefore))))"
            + " ))"
            + " ORDER BY r.updated_at,r.id LIMIT @take"
            + " FOR UPDATE OF r SKIP LOCKED",
            new
            {
                databaseNow,
                recoveryBefore,
                take = request.Limit + 1,
            },
            tx,
            cancellationToken: ct))).AsList();
        var items = new List<AgentRunRecoveryItem>(request.Limit);
        var hasMore = false;
        foreach (var candidate in rows)
        {
            if (items.Count >= request.Limit)
            {
                hasMore = true;
                break;
            }

            var row = candidate;
            var runCounterError = RecoveryRunCounterError(row);
            if (runCounterError is not null)
            {
                await QuarantineExhaustedRecoveryCandidateAsync(
                    conn,
                    tx,
                    row,
                    runCounterError,
                    ct);
                continue;
            }
            var deadlineCleanup = row.DeadlineAt <= databaseNow;
            if (deadlineCleanup && row.CommandType != "deadline_cleanup")
            {
                row = await EnsureDeadlineCleanupCommandAsync(conn, tx, row, ct);
            }
            // 候選查詢用 FOR UPDATE OF r SKIP LOCKED 只鎖 run 列,join 來的 command 列停在
            // 交易的 MVCC 快照:EvalPlanQual 只會用最新的 r 重驗條件,c 的欄位不重讀。若 ACK
            // (CompleteDispatchAsync,同樣先鎖 run 列)在快照之後、我們拿到 run 鎖之前 commit,
            // 候選帶的就是過期的 command 狀態。鎖住 command 列後必須重讀真值再重驗資格。
            var locked = await conn.QuerySingleAsync<RecoveryCommandStateRow>(
                new CommandDefinition(
                    RecoveryCommandStateColumns
                    + " FROM agent_run_command WHERE id=@commandId FOR UPDATE",
                    new { row.CommandId },
                    tx,
                    cancellationToken: ct));
            row = row with
            {
                Input = locked.Input,
                InputHash = locked.InputHash,
                DispatchAttempts = locked.DispatchAttempts,
                DispatchClaimTokenHash = locked.DispatchClaimTokenHash,
                DispatchClaimExpiresAt = locked.DispatchClaimExpiresAt,
                DispatchCompletedAt = locked.DispatchCompletedAt,
                ExecutionRecoveryLeaseTokenHash =
                    locked.ExecutionRecoveryLeaseTokenHash,
            };
            if (!IsRecoveryDispatchEligible(row, databaseNow, recoveryBefore))
            {
                // 輸掉與 ACK/其他 recovery 的競態:跳過,不是不變式破壞。
                continue;
            }

            if (row.DispatchAttempts is < 0 or >= int.MaxValue - 1)
            {
                await QuarantineExhaustedRecoveryCandidateAsync(
                    conn,
                    tx,
                    row,
                    "run_recovery_counter_exhausted",
                    ct);
                continue;
            }

            if (!HasValidCheckpointSeed(row))
            {
                await DeadLetterRecoveryCandidateAsync(
                    conn,
                    tx,
                    row,
                    "run_recovery_seed_invalid",
                    ct);
                continue;
            }
            if (!HasValidPinnedIdentity(
                    row.TenantId,
                    row.UserId,
                    row.CallerRole))
            {
                await DeadLetterRecoveryCandidateAsync(
                    conn,
                    tx,
                    row,
                    "run_recovery_identity_invalid",
                    ct);
                continue;
            }
            if (!AgentRunCommandInput.TryParseAndValidate(
                    row.Input,
                    row.CommandType,
                    row.CheckpointGeneration,
                    row.CheckpointVersion,
                    row.CheckpointRef,
                    AgentRunCommandValidationMode.Recovery,
                    out var commandInput,
                    out var parsedTargetTerminal)
                || !AgentRunCommandInput.MatchesCanonicalSha256(
                    commandInput,
                    row.CommandType,
                    row.InputHash))
            {
                await DeadLetterRecoveryCandidateAsync(
                    conn,
                    tx,
                    row,
                    "run_recovery_command_invalid",
                    ct);
                continue;
            }
            string? role = null;
            JsonElement snapshotEnvelope = default;
            string? targetTerminal = null;
            var metadataOnly =
                deadlineCleanup || row.CommandType == "cancel";
            if (deadlineCleanup)
            {
                targetTerminal = parsedTargetTerminal;
            }
            else if (row.CommandType == "cancel")
            {
                targetTerminal = AgentRunStatuses.Cancelled;
            }
            if (metadataOnly)
            {
                role = row.CallerRole;
            }
            else if (!TryBuildRecoverySnapshot(row, out role, out snapshotEnvelope))
            {
                await DeadLetterRecoveryCandidateAsync(
                    conn,
                    tx,
                    row,
                    "run_recovery_snapshot_invalid",
                    ct);
                continue;
            }

            var token = NewToken();
            var leaseToken = NewToken();
            var attempt = checked(row.DispatchAttempts + 1);
            var executionRecovery = row.DispatchCompletedAt is not null;
            var forceTakeover =
                deadlineCleanup
                || row.CommandType == "cancel" && row.DispatchCompletedAt is null;
            var leaseGeneration = checked(row.LeaseGeneration + 1);
            var expiresAt = await conn.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(
                    "UPDATE agent_run SET lease_owner=@workerId,"
                     + " lease_token_sha256=@leaseTokenHash,"
                     + " lease_generation=@leaseGeneration,"
                     + " lease_command_id=@commandId,"
                    + " lease_expires_at=clock_timestamp()+make_interval(secs=>@leaseSeconds),"
                    + " state_version=state_version+1,updated_at=clock_timestamp()"
                    + " WHERE id=@runId AND state_version=@expectedStateVersion"
                    + " AND ((@deadlineCleanup AND deadline_at<=clock_timestamp())"
                    + " OR (NOT @deadlineCleanup AND deadline_at>clock_timestamp()))"
                    + " AND (@forceTakeover OR lease_expires_at IS NULL"
                    + " OR lease_expires_at<=clock_timestamp())"
                    + " RETURNING lease_expires_at",
                    new
                    {
                        workerId = request.WorkerId!.Trim(),
                        leaseTokenHash = SkillHash.Sha256(leaseToken),
                        leaseGeneration,
                        leaseSeconds = request.LeaseSeconds,
                        forceTakeover,
                        deadlineCleanup,
                        row.RunId,
                        row.CommandId,
                        expectedStateVersion = row.StateVersion,
                    },
                    tx,
                    cancellationToken: ct));
            if (expiresAt is null)
            {
                continue;
            }
            var changed = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_run_command SET dispatch_claim_owner=@workerId,"
                + " dispatch_claim_token_sha256=@tokenHash,"
                + " dispatch_claim_expires_at=@expiresAt,"
                + " dispatch_attempts=@attempt,dispatch_completed_at=NULL,"
                + " execution_recovery_lease_token_sha256=CASE"
                + " WHEN @executionRecovery THEN @runLeaseTokenHash"
                + " ELSE execution_recovery_lease_token_sha256 END,"
                + " last_dispatch_at=clock_timestamp() WHERE id=@commandId"
                + " AND dispatch_completed_at"
                + " IS NOT DISTINCT FROM @expectedDispatchCompletedAt"
                + " AND dispatch_attempts=@expectedAttempts"
                + " AND dispatch_claim_token_sha256"
                + " IS NOT DISTINCT FROM @expectedClaimTokenHash"
                + " AND dispatch_claim_expires_at"
                + " IS NOT DISTINCT FROM @expectedClaimExpiresAt"
                + " AND execution_recovery_lease_token_sha256"
                + " IS NOT DISTINCT FROM @expectedExecutionRecoveryLeaseTokenHash",
                new
                {
                    workerId = request.WorkerId!.Trim(),
                    tokenHash = SkillHash.Sha256(token),
                    expiresAt = expiresAt.Value,
                    attempt,
                    row.CommandId,
                    expectedAttempts = row.DispatchAttempts,
                    expectedClaimTokenHash = row.DispatchClaimTokenHash,
                    expectedClaimExpiresAt = row.DispatchClaimExpiresAt,
                    expectedDispatchCompletedAt = row.DispatchCompletedAt,
                    expectedExecutionRecoveryLeaseTokenHash =
                        row.ExecutionRecoveryLeaseTokenHash,
                    executionRecovery,
                    runLeaseTokenHash = SkillHash.Sha256(leaseToken),
                },
                tx,
                cancellationToken: ct));
            if (changed != 1)
            {
                // 期望值皆來自上面鎖後重讀,且 command 列鎖持有到交易結束,理應不可達;
                // 保留為最後防線:真的觸發代表有人在鎖區間內另行改動了同一列。
                throw new InvalidOperationException(
                    "Recovery command changed while its row lock was held");
            }
            items.Add(new AgentRunRecoveryItem(
                row.CommandId,
                row.RunId,
                row.CommandType,
                metadataOnly
                    ? default
                    : commandInput,
                row.TenantId,
                row.UserId,
                metadataOnly ? row.CallerRole : role,
                row.SnapshotHash,
                snapshotEnvelope,
                row.RunStatus,
                row.StateVersion + 1,
                leaseGeneration,
                row.CheckpointGeneration,
                row.CheckpointRef,
                row.CheckpointVersion,
                row.EventAckCursor,
                row.DeadlineAt,
                leaseToken,
                expiresAt.Value,
                token,
                expiresAt.Value,
                attempt,
                targetTerminal));
        }
        await tx.CommitAsync(ct);
        return new AgentRunRecoveryClaimResponse(
            items,
            hasMore || rows.Count == request.Limit + 1);
    }

    private static async Task<RecoveryCandidateRow> EnsureDeadlineCleanupCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryCandidateRow row,
        CancellationToken ct)
    {
        var targetTerminal = row.CancelRequested
            ? AgentRunStatuses.Cancelled
            : AgentRunStatuses.Failed;
        var commandId = Guid.NewGuid();
        var idempotencyHash = SkillHash.Sha256(
            $"deadline_cleanup\0{row.RunId:D}");
        var requestHash = SkillHash.Sha256(
            $"{row.RunId:D}\0{targetTerminal}");
        var input = JsonSerializer.Serialize(new
        {
            target_terminal = targetTerminal,
        });
        var commandInputHash = CanonicalCommandInputSha256(
            input,
            "deadline_cleanup",
            row.CheckpointGeneration,
            row.CheckpointVersion,
            row.CheckpointRef);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO agent_run_command"
            + " (id,tenant_id,user_id,run_id,command_type,idempotency_key_sha256,"
            + " request_sha256,command_input,command_input_sha256,dispatch_attempts)"
            + " VALUES (@commandId,@tenantId,@userId,@runId,'deadline_cleanup',"
            + " @idempotencyHash,@requestHash,CAST(@input AS jsonb),@commandInputHash,0)"
            + " ON CONFLICT (run_id) WHERE command_type='deadline_cleanup'"
            + " DO NOTHING",
            new
            {
                commandId,
                row.TenantId,
                row.UserId,
                row.RunId,
                idempotencyHash,
                requestHash,
                input,
                commandInputHash,
            },
            transaction,
            cancellationToken: ct));
        // 只解析並鎖住 command id;其餘欄位由呼叫端統一的鎖後重讀取得(單一真值來源)。
        var resolvedCommandId = await connection.QuerySingleAsync<Guid>(
            new CommandDefinition(
                "SELECT id FROM agent_run_command"
                + " WHERE run_id=@runId AND command_type='deadline_cleanup'"
                + " FOR UPDATE",
                new { row.RunId },
                transaction,
                cancellationToken: ct));
        return row with
        {
            CommandId = resolvedCommandId,
            CommandType = "deadline_cleanup",
        };
    }

    private const string RecoveryCommandStateColumns =
        "SELECT id AS CommandId,command_input::text AS Input,"
        + " command_input_sha256 AS InputHash,"
        + " dispatch_attempts AS DispatchAttempts,"
        + " dispatch_claim_token_sha256 AS DispatchClaimTokenHash,"
        + " dispatch_claim_expires_at AS DispatchClaimExpiresAt,"
        + " dispatch_completed_at AS DispatchCompletedAt,"
        + " execution_recovery_lease_token_sha256"
        + " AS ExecutionRecoveryLeaseTokenHash";

    /// <summary>
    /// 候選查詢 WHERE 中屬於 command 側的資格條件,用鎖後重讀的真值複驗。run 側僅
    /// deadline/cancel/status 由 FOR UPDATE OF r 的 EvalPlanQual 以最新 r 重驗,租約活性
    /// 須自行複驗:未 ACK 分支只需 command 側(無有效 dispatch claim);已 ACK 分支除了
    /// command 側(執行租約已換代的 lease-generation marker,或超過 30 秒無租約寬限期)外,
    /// 也要求 run 側租約已非活躍(lease_expires_at 已過期或從未核發),否則會在寬限期內
    /// 搶開一個 worker 仍持有活躍租約的已 ACK command。RunLeaseExpiresAt 來自候選查詢的
    /// r.lease_expires_at:候選查詢以 FOR UPDATE OF r 鎖定 run 列,EvalPlanQual 保證取得的
    /// 是鎖後最新提交值,不需再對 run 列做第二次重讀。
    /// </summary>
    private static bool IsRecoveryDispatchEligible(
        RecoveryCandidateRow row,
        DateTime databaseNow,
        DateTime recoveryBefore)
        => row.DispatchCompletedAt is null
            ? row.DispatchClaimExpiresAt is null
              || row.DispatchClaimExpiresAt <= databaseNow
            : (row.RunLeaseExpiresAt is null || row.RunLeaseExpiresAt <= databaseNow)
              && (!string.Equals(
                      row.RunLeaseTokenHash,
                      row.ExecutionRecoveryLeaseTokenHash,
                      StringComparison.Ordinal)
                  || row.DispatchCompletedAt <= recoveryBefore);

    private static bool HasValidCheckpointSeed(RecoveryCandidateRow row)
        => row.CheckpointVersion == 0
            ? row.CheckpointGeneration == 0 && row.CheckpointRef is null
            : row.CheckpointVersion > 0
              && AgentRunCheckpointRef.IsValidPromotion(
                  row.CheckpointRef,
                  row.CheckpointGeneration);

    private static bool TryBuildRecoverySnapshot(
        RecoveryCandidateRow row,
        out string? role,
        out JsonElement snapshotEnvelope)
    {
        role = null;
        snapshotEnvelope = default;
        try
        {
            var canonicalSnapshot = AgentRunSnapshotBuilder.ReadAuthoritativeSnapshot(
                row.SnapshotCanonical,
                row.SnapshotHash);
            using var snapshotDocument = JsonDocument.Parse(canonicalSnapshot);
            var caller = snapshotDocument.RootElement.GetProperty("caller");
            var snapshotTenantId = caller.GetProperty("tenant_id").GetString();
            var snapshotUserId = caller.GetProperty("user_id").GetString();
            role = caller.GetProperty("role").GetString();
            if (!HasValidPinnedIdentity(snapshotTenantId, snapshotUserId, role)
                || !string.Equals(
                    snapshotTenantId,
                    row.TenantId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    snapshotUserId,
                    row.UserId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    role,
                    row.CallerRole,
                    StringComparison.Ordinal))
            {
                return false;
            }
            snapshotEnvelope = JsonDocument.Parse(
                AgentRunSnapshotBuilder.CreateExecutionArtifact(
                    row.SnapshotCanonical,
                    row.SnapshotHash)).RootElement.Clone();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
                                   or KeyNotFoundException)
        {
            role = null;
            snapshotEnvelope = default;
            return false;
        }
    }

    private static bool HasValidPinnedIdentity(
        string? tenantId,
        string? userId,
        string? role)
        => HasValidIdentityValue(
               tenantId,
               AgentExecutionContract.MaxCallerIdentityLength)
           && HasValidIdentityValue(
               userId,
               AgentExecutionContract.MaxCallerIdentityLength)
           && HasValidIdentityValue(
               role,
               AgentExecutionContract.MaxCallerRoleLength);

    private static bool HasValidIdentityValue(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static string? RecoveryRunCounterError(RecoveryCandidateRow row)
    {
        if (row.LeaseGeneration is < 0 or >= long.MaxValue - 1)
        {
            return "run_recovery_generation_exhausted";
        }

        return row.StateVersion is < 0 or >= long.MaxValue - 2
               || row.CheckpointVersion is < 0 or >= long.MaxValue - 1
               || row.EventAckCursor is < 0 or >= long.MaxValue - 1
               || row.LatestEventSequence is < 0 or >= long.MaxValue - 1
            ? "run_recovery_counter_exhausted"
            : null;
    }

    private async Task QuarantineExhaustedRecoveryCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryCandidateRow row,
        string errorCode,
        CancellationToken ct)
    {
        var terminalStatus = row.CancelRequested
            ? AgentRunStatuses.Cancelled
            : AgentRunStatuses.Failed;
        var canAppendAudit =
            row.LatestEventSequence >= 0
            && row.LatestEventSequence < long.MaxValue;
        var sequence = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "UPDATE agent_run SET status=@terminalStatus,"
                + " pending_input=NULL,result=NULL,error_code=@errorCode,"
                + " error_message='Run recovery counters were safely quarantined',"
                + " completed_at=clock_timestamp(),"
                + " lease_owner=NULL,lease_token_sha256=NULL,lease_expires_at=NULL,"
                + " lease_command_id=NULL,"
                + " latest_event_sequence=CASE WHEN @canAppendAudit"
                + " THEN latest_event_sequence+1 ELSE latest_event_sequence END,"
                + " updated_at=clock_timestamp()"
                + " WHERE id=@runId AND state_version=@expectedStateVersion"
                + " AND status IN ('queued','running','waiting_input','waiting_approval')"
                + " RETURNING latest_event_sequence",
                new
                {
                    terminalStatus,
                    errorCode,
                    row.RunId,
                    expectedStateVersion = row.StateVersion,
                    canAppendAudit,
                },
                transaction,
                cancellationToken: ct));
        if (sequence is null)
        {
            return;
        }

        if (canAppendAudit)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO agent_run_event"
                + " (run_id,sequence,event_id,event_type,snapshot_sha256,payload)"
                + " VALUES (@runId,@sequence,@eventId,'run_dead_lettered',"
                + " @snapshotHash,CAST(@payload AS jsonb))",
                new
                {
                    row.RunId,
                    sequence = sequence.Value,
                    eventId = Guid.NewGuid(),
                    row.SnapshotHash,
                    payload = JsonSerializer.Serialize(new { error_code = errorCode }),
                },
                transaction,
                cancellationToken: ct));
        }
        else
        {
            _logger?.LogError(
                "Agent run {RunId} was quarantined without an audit event because "
                + "latest_event_sequence is exhausted",
                row.RunId);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_run_command SET command_input='{}'::jsonb,"
            + " dispatch_completed_at=COALESCE(dispatch_completed_at,clock_timestamp()),"
            + " dispatch_claim_owner=NULL,dispatch_claim_token_sha256=NULL,"
            + " dispatch_claim_expires_at=NULL"
            + " WHERE run_id=@runId",
            new { row.RunId },
            transaction,
            cancellationToken: ct));
    }

    private static async Task DeadLetterRecoveryCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecoveryCandidateRow row,
        string errorCode,
        CancellationToken ct)
    {
        var terminalStatus = row.CancelRequested
            ? AgentRunStatuses.Cancelled
            : AgentRunStatuses.Failed;
        var sequence = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "UPDATE agent_run SET status=@terminalStatus,"
                + " state_version=state_version+1,pending_input=NULL,result=NULL,"
                + " error_code=@errorCode,"
                + " error_message='Run recovery was safely dead-lettered',"
                + " completed_at=clock_timestamp(),"
                + " lease_owner=NULL,lease_token_sha256=NULL,lease_expires_at=NULL,"
                + " lease_command_id=NULL,"
                + " latest_event_sequence=latest_event_sequence+1,"
                + " updated_at=clock_timestamp()"
                + " WHERE id=@runId AND state_version=@expectedStateVersion"
                + " AND status IN ('queued','running','waiting_input','waiting_approval')"
                + " RETURNING latest_event_sequence",
                new
                {
                    terminalStatus,
                    errorCode,
                    row.RunId,
                    expectedStateVersion = row.StateVersion,
                },
                transaction,
                cancellationToken: ct));
        if (sequence is null)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO agent_run_event"
            + " (run_id,sequence,event_id,event_type,snapshot_sha256,payload)"
            + " VALUES (@runId,@sequence,@eventId,'run_dead_lettered',"
            + " @snapshotHash,CAST(@payload AS jsonb));"
            + " UPDATE agent_run_command SET command_input='{}'::jsonb,"
            + " dispatch_completed_at=COALESCE(dispatch_completed_at,clock_timestamp()),"
            + " dispatch_claim_owner=NULL,dispatch_claim_token_sha256=NULL,"
            + " dispatch_claim_expires_at=NULL"
            + " WHERE run_id=@runId",
            new
            {
                row.RunId,
                sequence = sequence.Value,
                eventId = Guid.NewGuid(),
                row.SnapshotHash,
                payload = JsonSerializer.Serialize(new { error_code = errorCode }),
            },
            transaction,
            cancellationToken: ct));
    }

    private async Task<AgentRunWriteResult> CommandAsync(
        string tenantId,
        string userId,
        Guid runId,
        string type,
        string idempotencyKey,
        string requestHash,
        Func<RunRow, string> commandInputFactory,
        Func<NpgsqlConnection, NpgsqlTransaction, RunRow, Task<AgentRunWriteResult?>> mutate,
        CancellationToken ct)
    {
        var keyHash = SkillHash.Sha256(idempotencyKey);
        var prior = await FindCommandAsync(tenantId, userId, type, keyHash, requestHash, ct);
        if (prior is not null)
        {
            return prior;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var row = await LockRunAsync(conn, tx, tenantId, userId, runId, ct);
        if (row is null)
        {
            await tx.RollbackAsync(ct);
            return NotFound("找不到 Agent run");
        }
        var recheck = await FindCommandAsync(
            conn, tx, tenantId, userId, type, keyHash, requestHash, ct);
        if (recheck is not null)
        {
            await tx.RollbackAsync(ct);
            return recheck;
        }
        var dispatchRequired = !(type == "cancel" && row.Status == AgentRunStatuses.Cancelled);
        var error = await mutate(conn, tx, row);
        if (error is not null)
        {
            await tx.RollbackAsync(ct);
            return error;
        }

        var commandInput = commandInputFactory(row);
        var commandInputHash = CanonicalCommandInputSha256(
            commandInput,
            type,
            row.CheckpointGeneration,
            row.CheckpointVersion,
            row.CheckpointRef);
        var dispatch = dispatchRequired ? NewDispatchClaim() : null;
        var commandId = dispatch?.CommandId ?? Guid.NewGuid();
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO agent_run_command"
                + " (id,tenant_id,user_id,run_id,command_type,idempotency_key_sha256,"
                + " request_sha256,command_input,command_input_sha256,dispatch_claim_owner,"
                + " dispatch_claim_token_sha256,dispatch_claim_expires_at,"
                + " dispatch_attempts,dispatch_completed_at,last_dispatch_at)"
                + " VALUES (@commandId,@tenantId,@userId,@runId,@type,@keyHash,@requestHash,"
                + " CAST(@commandInput AS jsonb),@commandInputHash,@claimOwner,"
                + " @claimTokenHash,@claimExpiresAt,"
                + " @dispatchAttempts,@dispatchCompletedAt,@lastDispatchAt)",
                new
                {
                    commandId,
                    tenantId,
                    userId,
                    runId,
                    type,
                    keyHash,
                    requestHash,
                    commandInput,
                    commandInputHash,
                    claimOwner = dispatch is null ? null : "platform",
                    claimTokenHash = dispatch is null
                        ? null
                        : SkillHash.Sha256(dispatch.ClaimToken),
                    claimExpiresAt = dispatch?.ClaimExpiresAt,
                    dispatchAttempts = dispatch?.DispatchAttempt ?? 0,
                    dispatchCompletedAt = dispatch is null ? DateTime.UtcNow : (DateTime?)null,
                    lastDispatchAt = dispatch is null ? (DateTime?)null : DateTime.UtcNow,
                },
                tx,
                cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);
            return await FindCommandAsync(tenantId, userId, type, keyHash, requestHash, ct)
                   ?? new AgentRunWriteResult(
                       AgentRunWriteStatus.Conflict,
                       Message: "Idempotency-Key 競爭衝突");
        }
        return new AgentRunWriteResult(
            AgentRunWriteStatus.Success,
            (await GetAsync(tenantId, userId, runId, ct))!,
            Dispatch: dispatch);
    }

    private async Task<AgentRunWriteResult?> FindCommandAsync(
        string tenantId,
        string userId,
        string type,
        string keyHash,
        string requestHash,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await FindCommandAsync(
            conn, null, tenantId, userId, type, keyHash, requestHash, ct);
    }

    private static async Task<AgentRunWriteResult?> FindCommandAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        string tenantId,
        string userId,
        string type,
        string keyHash,
        string requestHash,
        CancellationToken ct)
    {
        var command = await conn.QuerySingleOrDefaultAsync<CommandRow>(new CommandDefinition(
            "SELECT run_id AS RunId,request_sha256 AS RequestHash"
            + " FROM agent_run_command"
            + " WHERE tenant_id=@tenantId AND user_id=@userId"
            + " AND command_type=@type AND idempotency_key_sha256=@keyHash",
            new { tenantId, userId, type, keyHash }, tx, cancellationToken: ct));
        if (command is null)
        {
            return null;
        }
        if (!string.Equals(command.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict,
                Message: "Idempotency-Key 已用於不同請求");
        }
        var run = await conn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM agent_run"
            + " WHERE tenant_id=@tenantId AND user_id=@userId AND id=@runId",
            new { tenantId, userId, runId = command.RunId }, tx, cancellationToken: ct));
        return new AgentRunWriteResult(
            AgentRunWriteStatus.Replay,
            run is null ? null : ToResponse(run),
            Replayed: true);
    }

    private static Task<RunRow?> LockRunAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string tenantId,
        string userId,
        Guid runId,
        CancellationToken ct)
        => conn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            $"SELECT {RunColumns} FROM agent_run"
            + " WHERE tenant_id=@tenantId AND user_id=@userId AND id=@runId FOR UPDATE",
            new { tenantId, userId, runId }, tx, cancellationToken: ct));

    private static async Task AppendSystemEventAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        RunRow row,
        string eventType,
        CancellationToken ct)
    {
        var sequence = row.LatestEventSequence + 1;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO agent_run_event"
            + " (run_id,sequence,event_id,event_type,snapshot_sha256,payload)"
            + " VALUES (@runId,@sequence,@eventId,@eventType,@snapshotHash,'{}'::jsonb);"
            + " UPDATE agent_run SET latest_event_sequence=@sequence WHERE id=@runId",
            new
            {
                runId = row.Id,
                sequence,
                eventId = Guid.NewGuid(),
                eventType,
                snapshotHash = row.SnapshotHash,
            },
            tx,
            cancellationToken: ct));
    }

    private static AgentRunRecoveryItem BuildCommandItem(
        CommandClaimRow command,
        RunRow row,
        string tenantId,
        string userId,
        string role,
        JsonElement input,
        JsonElement snapshotEnvelope,
        string leaseToken,
        DateTime leaseExpiresAt,
        string claimToken,
        DateTime claimExpiresAt,
        int dispatchAttempt,
        string? targetTerminal)
        => new(
            command.Id,
            row.Id,
            command.Type,
            input,
            tenantId,
            userId,
            role,
            row.SnapshotHash,
            snapshotEnvelope,
            row.Status,
            row.StateVersion,
            row.LeaseGeneration,
            row.CheckpointGeneration,
            row.CheckpointRef,
            row.CheckpointVersion,
            row.EventAckCursor,
            row.DeadlineAt,
            leaseToken,
            leaseExpiresAt,
            claimToken,
            claimExpiresAt,
            dispatchAttempt,
            targetTerminal);

    private static bool JsonArrayContains(JsonArray array, string expected)
        => array.Any(item => string.Equals(
               item?.GetValue<string>(), expected, StringComparison.Ordinal));

    private static bool LeaseMatches(
        RunRow row,
        string? token,
        long leaseGeneration,
        DateTime databaseNow)
    {
        return row.LeaseTokenHash is not null
               && leaseGeneration > 0
               && row.LeaseGeneration == leaseGeneration
               && row.LeaseExpiresAt > databaseNow
               && !string.IsNullOrWhiteSpace(token)
               && string.Equals(
                   row.LeaseTokenHash,
                   SkillHash.Sha256(token),
                   StringComparison.Ordinal);
    }

    private static async Task<string?> DeadlineCleanupTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RunRow row,
        CancellationToken ct)
    {
        if (row.LeaseCommandId is not Guid commandId)
        {
            return null;
        }

        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT command_input->>'target_terminal' FROM agent_run_command"
            + " WHERE id=@commandId AND run_id=@runId"
            + " AND command_type='deadline_cleanup'",
            new { commandId, runId = row.Id },
            transaction,
            cancellationToken: ct));
    }

    private static AgentRunCommandDispatch NewDispatchClaim()
    {
        var token = NewToken();
        return new AgentRunCommandDispatch(
            Guid.NewGuid(),
            token,
            DateTime.UtcNow.AddSeconds(30),
            1);
    }

    private static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string CanonicalCommandInputSha256(
        string json,
        string commandType,
        long checkpointGeneration,
        long checkpointVersion,
        string? checkpointRef)
    {
        if (!AgentRunCommandInput.TryParseAndValidate(
                json,
                commandType,
                checkpointGeneration,
                checkpointVersion,
                checkpointRef,
                AgentRunCommandValidationMode.DirectClaim,
                out var validatedInput,
                out _))
        {
            throw new InvalidOperationException(
                $"Generated agent-run command input for '{commandType}' is invalid.");
        }

        return AgentRunCommandInput.CanonicalSha256(
            validatedInput,
            commandType);
    }

    private static bool WithinJsonLimit(JsonElement? value, int max)
        => value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
           || value.Value.GetRawText().Length <= max;

    private static string? JsonText(JsonElement? value)
        => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } v
            ? v.GetRawText()
            : null;

    private static string? Normalize(string? value, int max)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, max)];
    }

    private static bool IsSafePayload(JsonElement? payload)
    {
        if (payload is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return true;
        }
        return payload.Value.GetRawText().Length <= 32_768 && SafeNode(payload.Value, 0);
    }

    private static bool SafeNode(JsonElement node, int depth)
    {
        if (depth > 8)
        {
            return false;
        }
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var key = property.Name.ToLowerInvariant();
                if (key is "prompt" or "message" or "content" or "args" or "arguments"
                    or "value" or "token" or "secret" or "resource"
                    || !SafeNode(property.Value, depth + 1))
                {
                    return false;
                }
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (!SafeNode(item, depth + 1))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool EventReplayMatches(
        EventIdentityRow prior,
        AgentRunEventAppend candidate,
        string snapshotHash)
    {
        var candidatePayload = JsonText(candidate.Payload) ?? "{}";
        return string.Equals(
                   prior.EventType,
                   candidate.EventType?.Trim(),
                   StringComparison.Ordinal)
               && string.Equals(
                   prior.NodeId,
                   Normalize(candidate.NodeId, 200),
                   StringComparison.Ordinal)
               && string.Equals(prior.SnapshotHash, snapshotHash, StringComparison.Ordinal)
               && JsonNode.DeepEquals(
                   JsonNode.Parse(prior.Payload),
                   JsonNode.Parse(candidatePayload));
    }

    /// <summary>
    /// A D5 child is still a normal D3 run.  Its terminal CAS is therefore performed by the
    /// D3 transition above, then mirrored into the root ledger in the same transaction.  Locking
    /// the root here serialises its event sequence with root terminal/cancel transitions.
    /// </summary>
    private static async Task SynchronizeOrchestratorChildTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RunRow run,
        string status,
        JsonElement? result,
        string? errorCode,
        CancellationToken ct)
    {
        var rootId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT orchestrator_root_run_id FROM agent_run WHERE id=@runId",
            new { runId = run.Id }, transaction, cancellationToken: ct));
        if (rootId is not Guid orchestratorRootRunId)
        {
            return;
        }

        var root = await connection.QuerySingleOrDefaultAsync<(string Hash, string Status)>(
            new CommandDefinition(
                "SELECT snapshot_sha256 Hash,status Status FROM orchestrator_run"
                + " WHERE id=@orchestratorRootRunId FOR UPDATE",
                new { orchestratorRootRunId }, transaction, cancellationToken: ct));
        if (string.IsNullOrEmpty(root.Hash))
        {
            return;
        }

        var child = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Task, int Attempt, string Kind, Guid AgentId, int AgentRevision, string SnapshotHash)>(
            new CommandDefinition(
                "UPDATE orchestrator_run_child SET status=@status,updated_at=clock_timestamp()"
                + " WHERE agent_run_id=@runId AND status IN ('queued','running')"
                + " RETURNING id,task_id Task,attempt Attempt,run_kind Kind,agent_id AgentId,agent_revision AgentRevision,agent_snapshot_hash SnapshotHash",
                new { runId = run.Id, status }, transaction, cancellationToken: ct));
        if (child.Id == Guid.Empty)
        {
            return;
        }

        // Root event payloads are deliberately redacted; the generator is shared with the
        // lite repository so the two authorities cannot drift.
        var payload = OrchestratorRuns.OrchestratorRunEvents.ChildTerminal(
            child.Id, run.Id, child.Task, child.Attempt, child.Kind,
            child.AgentId, child.AgentRevision, child.SnapshotHash, status, result, errorCode);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO orchestrator_run_event(run_id,sequence,event_type,snapshot_sha256,payload)"
            + " VALUES(@orchestratorRootRunId,"
            + " (SELECT COALESCE(MAX(sequence),0)+1 FROM orchestrator_run_event WHERE run_id=@orchestratorRootRunId),"
            + " 'child_terminal',@hash,@payload::jsonb)",
            new { orchestratorRootRunId, hash = root.Hash, payload },
            transaction,
            cancellationToken: ct));
    }

    private static AgentRunResponse ToResponse(RunRow row) => new(
        row.Id,
        row.RootRunId,
        row.ParentRunId,
        row.TaskId,
        row.RunKind,
        row.AgentId,
        row.AgentRevision,
        row.WorkflowId,
        row.WorkflowRevision,
        row.SnapshotHash,
        row.Status,
        row.StateVersion,
        row.LeaseGeneration,
        row.CheckpointGeneration,
        row.CheckpointRef,
        row.CheckpointVersion,
        row.EventAckCursor,
        row.CancelRequested,
        ParseOptional(row.PendingInput),
        ParseOptional(row.Result),
        row.ErrorCode,
        row.ErrorMessage,
        row.LatestEventSequence,
        row.StartedAt,
        row.CreatedAt,
        row.DeadlineAt,
        row.UpdatedAt,
        row.CompletedAt,
        JsonSerializer.Deserialize<List<AgentRunSkillPinResponse>>(row.SkillPins)
            ?? new List<AgentRunSkillPinResponse>(),
        JsonDocument.Parse(row.RuntimeLimits).RootElement.Clone());

    private static AgentRunEventResponse ToEventResponse(EventRow row) => new(
        row.Sequence,
        row.EventId,
        row.EventType,
        row.NodeId,
        row.LeaseGeneration,
        row.EventCursor,
        row.SnapshotHash,
        JsonDocument.Parse(row.Payload).RootElement.Clone(),
        row.CreatedAt);

    private static JsonElement? ParseOptional(string? json)
        => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static bool HasJsonValue(JsonElement? value)
        => value is
        { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    private static AgentRunWriteResult NotFound(string message)
        => new(AgentRunWriteStatus.NotFound, Message: message);

    private static AgentRunWriteResult InvalidState(string message)
        => new(AgentRunWriteStatus.InvalidState, Message: message);

    private sealed record AgentSourceRow(
        string Name,
        bool Enabled,
        int? PublishedRevision,
        byte[]? CanonicalDefinition,
        Guid? WorkflowId,
        int? WorkflowRevision,
        string DefinitionSha256,
        int? PromptManifestRevision,
        string? PromptManifestSha256);

    private sealed record WorkflowRow(
        int SchemaVersion,
        byte[]? CanonicalDefinition,
        string DefinitionSha256,
        string CompilerContractVersion);

    private sealed record SkillRunRow(
        int Position,
        Guid SkillId,
        string Name,
        string Description,
        bool Enabled,
        int Revision,
        string Kind,
        string Definition,
        string DefinitionSha256,
        byte[]? Package,
        string? PackageSha256);

    private sealed record RunRow(
        Guid Id,
        Guid RootRunId,
        Guid? ParentRunId,
        string? TaskId,
        string RunKind,
        Guid AgentId,
        int AgentRevision,
        Guid WorkflowId,
        int WorkflowRevision,
        string SnapshotHash,
        string Status,
        long StateVersion,
        long LeaseGeneration,
        long CheckpointGeneration,
        string? CheckpointRef,
        long CheckpointVersion,
        long EventAckCursor,
        bool CancelRequested,
        string? PendingInput,
        string? Result,
        string? ErrorCode,
        string? ErrorMessage,
        long LatestEventSequence,
        DateTime? StartedAt,
        DateTime CreatedAt,
        DateTime DeadlineAt,
        DateTime UpdatedAt,
        DateTime? CompletedAt,
        string RuntimeLimits,
        string SkillPins,
        string? LeaseOwner = null,
        string? LeaseTokenHash = null,
        DateTime? LeaseExpiresAt = null,
        Guid? LeaseCommandId = null);

    private sealed record EventRow(
        long Sequence,
        Guid EventId,
        string EventType,
        string? NodeId,
        long? LeaseGeneration,
        long? EventCursor,
        string SnapshotHash,
        string Payload,
        DateTime CreatedAt);

    private sealed record EventIdentityRow(
        Guid EventId,
        long? EventCursor,
        string EventType,
        string? NodeId,
        string SnapshotHash,
        string Payload);

    private sealed record CommandRow(Guid RunId, string RequestHash);

    private sealed record CommandClaimRow(
        Guid Id,
        string Type,
        string Input,
        string InputHash,
        string? DispatchClaimOwner,
        string? DispatchClaimTokenHash,
        DateTime? DispatchClaimExpiresAt,
        int DispatchAttempts,
        DateTime? DispatchCompletedAt);

    private sealed record RecoveryCandidateRow(
        Guid CommandId,
        Guid RunId,
        string CommandType,
        string Input,
        string InputHash,
        int DispatchAttempts,
        string? DispatchClaimTokenHash,
        DateTime? DispatchClaimExpiresAt,
        DateTime? DispatchCompletedAt,
        string? ExecutionRecoveryLeaseTokenHash,
        string TenantId,
        string UserId,
        string CallerRole,
        byte[]? SnapshotCanonical,
        string SnapshotHash,
        string RunStatus,
        long StateVersion,
        long LeaseGeneration,
        long CheckpointGeneration,
        string? CheckpointRef,
        long CheckpointVersion,
        long EventAckCursor,
        long LatestEventSequence,
        DateTime DeadlineAt,
        bool CancelRequested,
        string? RunLeaseTokenHash,
        DateTime? RunLeaseExpiresAt);

    private sealed record RecoveryCommandStateRow(
        Guid CommandId,
        string Input,
        string InputHash,
        int DispatchAttempts,
        string? DispatchClaimTokenHash,
        DateTime? DispatchClaimExpiresAt,
        DateTime? DispatchCompletedAt,
        string? ExecutionRecoveryLeaseTokenHash);

    private sealed record ArtifactRow(
        byte[]? SnapshotCanonical,
        string? SnapshotHash,
        string CallerRole);
}
