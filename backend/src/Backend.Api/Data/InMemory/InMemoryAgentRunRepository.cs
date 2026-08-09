using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.AgentRuns;
using Backend.Api.Agents;
using Backend.Api.RunDiscovery;
using Backend.Api.Skills;

namespace Backend.Api.Data.InMemory;

public sealed class InMemoryAgentRunRepository : IAgentRunRepository, IOrchestratorChildRunRepository, IAgentRunApprovalLeaseVerifier, IAgentRunApprovalDecisionTransition, IAgentRunCancellationFence, IRunDiscoverySource
{
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

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _runs = new();
    private readonly Dictionary<(string Tenant, string User, string Type, string KeyHash), CommandEntry> _commands = new();
    private readonly InMemoryAgentRepository _agents;
    private readonly InMemorySkillRepository _skills;
    private readonly TimeProvider _timeProvider;
    private long _latestCommandSequence;

    public InMemoryAgentRunRepository(IAgentRepository agents, ISkillRepository skills)
        : this(agents, skills, TimeProvider.System)
    {
    }

    internal InMemoryAgentRunRepository(
        IAgentRepository agents,
        ISkillRepository skills,
        TimeProvider timeProvider)
    {
        _agents = agents as InMemoryAgentRepository
            ?? throw new ArgumentException("InMemoryAgentRunRepository 需要 InMemoryAgentRepository", nameof(agents));
        _skills = skills as InMemorySkillRepository
            ?? throw new ArgumentException("InMemoryAgentRunRepository 需要 InMemorySkillRepository", nameof(skills));
        _timeProvider = timeProvider;
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

    public Task<AgentRunWriteResult> CreateDirectAsync(
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> capabilityClaims,
        Guid agentId,
        string message,
        string idempotencyKey,
        CancellationToken ct)
        => CreateDirectAsync(
            tenantId,
            userId,
            role,
            Array.Empty<string>(),
            capabilityClaims,
            agentId,
            message,
            idempotencyKey,
            ct);

    public Task<AgentRunWriteResult> CreateDirectAsync(
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
        ct.ThrowIfCancellationRequested();
        var key = CommandKey(tenantId, userId, "start", idempotencyKey);
        var requestHash = SkillHash.Sha256($"{agentId:D}\0{message}");

        lock (_agents.RunSnapshotSyncRoot)
        {
            lock (_gate)
            {
                if (Replay(key, requestHash, out var replay))
                {
                    return Task.FromResult(replay);
                }

                var agent = _agents.GetPublishedSnapshotUnsafe(tenantId, agentId);
                if (agent is null)
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        _agents.AgentExistsUnsafe(tenantId, agentId)
                            ? AgentRunWriteStatus.InvalidState
                            : AgentRunWriteStatus.NotFound,
                        Message: "找不到可執行的已發布 Agent"));
                }

                var definition = JsonNode.Parse(agent.Definition)!.AsObject();
                if (!HasValue(definition, "execution_roles", "worker")
                    || !AgentAudience.Matches(
                        (definition["audience"]?.AsArray() ?? new JsonArray())
                        .Select(item => item!.GetValue<string>()),
                        role,
                        groups,
                        allowLegacyPublishedRoles: true))
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState,
                        Message: "Agent 未授權 ADMIN direct test 或不具 worker execution role"));
                }

                if (agent.WorkflowId != Guid.Parse(AgentDefaults.RuntimeWorkflowId)
                    || agent.WorkflowRevision is not (AgentDefaults.PreviousRuntimeWorkflowRevision or AgentDefaults.RuntimeWorkflowRevision))
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState,
                        Message: "Agent-Runtime Workflow revision 無法執行"));
                }

                var skillSources = new List<SkillSnapshotSource>();
                foreach (var binding in agent.SkillBindings.OrderBy(b => b.Position))
                {
                    if (!_skills.TryGetEnabledRevisionUnsafe(
                            tenantId, binding.Skill, binding.SkillRevision, out var source))
                    {
                        return Task.FromResult(new AgentRunWriteResult(
                            AgentRunWriteStatus.InvalidState,
                            Message: $"Skill「{binding.Skill}」已停用或 pinned revision 不存在"));
                    }
                    skillSources.Add(source!);
                }

                var workflowDefinition = AgentRunSnapshotBuilder.CanonicalizeJson(
                    agent.WorkflowRevision == AgentDefaults.PreviousRuntimeWorkflowRevision
                        ? AgentDefaults.PreviousRuntimeWorkflowDefinition
                        : AgentDefaults.RuntimeWorkflowDefinition);
                var workflow = new WorkflowSnapshotSource(
                    agent.WorkflowId,
                    agent.WorkflowRevision,
                    1,
                    workflowDefinition,
                    SkillHash.Sha256(workflowDefinition),
                    "1");
                var snapshotErrors = AgentRunSnapshotBuilder.ValidateExecutionContract(
                    agent, workflow, skillSources, tenantId, userId, role, groups);
                if (snapshotErrors.Count > 0)
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState,
                        Message:
                            $"Agent execution snapshot contract 無效：{snapshotErrors[0].Message}"));
                }
                var runId = Guid.NewGuid();
                var snapshot = AgentRunSnapshotBuilder.Build(
                    runId,
                    tenantId,
                    userId,
                    role,
                    groups,
                    capabilityClaims,
                    agent,
                    workflow,
                    skillSources);
                if (snapshot.CanonicalByteLength
                    > AgentExecutionContract.MaxSnapshotCanonicalBytes)
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState,
                        Message:
                            $"Agent execution snapshot exceeds {AgentExecutionContract.MaxSnapshotCanonicalBytes} canonical UTF-8 bytes"));
                }
                var now = UtcNow();
                var entry = new Entry
                {
                    Id = runId,
                    RootRunId = runId,
                    TenantId = tenantId,
                    UserId = userId,
                    CallerRole = role,
                    AgentId = agent.AgentId,
                    AgentRevision = agent.Revision,
                    WorkflowId = agent.WorkflowId,
                    WorkflowRevision = agent.WorkflowRevision,
                    Snapshot = snapshot.StoredSnapshot,
                    SnapshotCanonical = snapshot.CanonicalBytes,
                    SnapshotHash = snapshot.SnapshotHash,
                    Status = AgentRunStatuses.Queued,
                    StateVersion = 1,
                    CreatedAt = now,
                    DeadlineAt = now.AddSeconds(snapshot.EffectiveTimeoutSeconds),
                    UpdatedAt = now,
                };
                foreach (var source in skillSources)
                {
                    entry.Skills.Add(source);
                }
                AddEventUnsafe(entry, Guid.NewGuid(), "run_created", null, EmptyPayload());
                _runs.Add(runId, entry);
                var (command, dispatch) = NewClaimedCommand(
                    runId,
                    "start",
                    requestHash,
                    JsonSerializer.SerializeToElement(new { message }));
                _commands.Add(key, command);
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Success,
                    ToResponse(entry),
                    Dispatch: dispatch));
            }
        }
    }

    Task<AgentRunWriteResult> IOrchestratorChildRunRepository.CreateOrchestratorChildAsync(
        string tenantId,
        string userId,
        string role,
        IReadOnlyCollection<string> groups,
        IReadOnlyCollection<string> capabilityClaims,
        PublishedAgentSnapshotSource agent,
        WorkflowSnapshotSource workflow,
        OrchestratorChildSnapshotProvenance provenance,
        string runKind,
        int tokenCap,
        JsonElement taskEnvelope,
        string idempotencyKey,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (runKind is not ("worker" or "verifier")
            || tokenCap is < 1 or > AgentExecutionContract.MaxOrchestratorTokenCap
            || provenance.RootRunId == Guid.Empty
            || string.IsNullOrWhiteSpace(provenance.TaskId)
            || provenance.Attempt < 1)
        {
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.InvalidState, Message: "Invalid immutable orchestrator child pin"));
        }

        if (taskEnvelope.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.InvalidState, Message: "Canonical task envelope is required"));
        }
        var envelope = OrchestratorRuns.OrchestratorTaskEnvelope.Validate(taskEnvelope);
        using var canonicalDocument = JsonDocument.Parse(envelope.Canonical);
        var canonicalEnvelope = canonicalDocument.RootElement.Clone();
        var key = CommandKey(tenantId, userId, "start", idempotencyKey);
        var requestHash = SkillHash.Sha256(
            $"{provenance.RootRunId:D}\0{provenance.TaskId}\0{provenance.Attempt}\0{runKind}\0{agent.AgentId:D}\0{agent.Revision}\0{tokenCap}\0{envelope.Canonical}");
        lock (_agents.RunSnapshotSyncRoot)
        {
            lock (_gate)
            {
                if (Replay(key, requestHash, out var replay))
                {
                    return Task.FromResult(replay);
                }
                var current = _agents.GetPublishedSnapshotUnsafe(tenantId, agent.AgentId);
                if (current is null
                    || current.Revision != agent.Revision
                    || current.WorkflowId != agent.WorkflowId
                    || current.WorkflowRevision != agent.WorkflowRevision
                    || !string.Equals(current.DefinitionSha256, agent.DefinitionSha256, StringComparison.Ordinal)
                    || !string.Equals(current.Definition, agent.Definition, StringComparison.Ordinal)
                    || workflow.WorkflowId != agent.WorkflowId
                    || workflow.Revision != agent.WorkflowRevision)
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState, Message: "Orchestrator child pin is no longer published"));
                }

                var definition = JsonNode.Parse(agent.Definition)!.AsObject();
                var requiredRole = runKind == "verifier" ? "verifier" : "worker";
                if (!HasValue(definition, "execution_roles", requiredRole)
                    || !AgentAudience.Matches(
                        (definition["audience"]?.AsArray() ?? new JsonArray())
                        .Select(item => item!.GetValue<string>()),
                        role,
                        groups,
                        allowLegacyPublishedRoles: true))
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState, Message: "Child Agent does not allow the pinned execution role"));
                }

                var skills = new List<SkillSnapshotSource>();
                foreach (var binding in agent.SkillBindings.Where(x => x.Enabled).OrderBy(x => x.Position))
                {
                    if (!_skills.TryGetEnabledRevisionUnsafe(
                            tenantId, binding.Skill, binding.SkillRevision, out var source))
                    {
                        return Task.FromResult(new AgentRunWriteResult(
                            AgentRunWriteStatus.InvalidState, Message: "Pinned child skill is unavailable"));
                    }
                    skills.Add(source!);
                }
                var errors = AgentRunSnapshotBuilder.ValidateExecutionContract(
                    agent, workflow, skills, tenantId, userId, role, groups);
                if (errors.Count > 0)
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState, Message: errors[0].Message));
                }

                var runId = Guid.NewGuid();
                var snapshot = AgentRunSnapshotBuilder.Build(
                    runId, tenantId, userId, role, groups, capabilityClaims,
                    agent, workflow, skills,
                    runKind == "verifier" ? "orchestrator-verifier" : "orchestrator-worker",
                    tokenCap, provenance);
                if (snapshot.CanonicalByteLength > AgentExecutionContract.MaxSnapshotCanonicalBytes)
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.InvalidState, Message: "Agent execution snapshot exceeds canonical byte limit"));
                }

                var now = UtcNow();
                var entry = new Entry
                {
                    Id = runId,
                    RootRunId = provenance.RootRunId,
                    ParentRunId = provenance.RootRunId,
                    TaskId = provenance.TaskId,
                    RunKind = runKind,
                    TenantId = tenantId,
                    UserId = userId,
                    CallerRole = role,
                    AgentId = agent.AgentId,
                    AgentRevision = agent.Revision,
                    WorkflowId = workflow.WorkflowId,
                    WorkflowRevision = workflow.Revision,
                    Snapshot = snapshot.StoredSnapshot,
                    SnapshotCanonical = snapshot.CanonicalBytes,
                    SnapshotHash = snapshot.SnapshotHash,
                    Status = AgentRunStatuses.Queued,
                    StateVersion = 1,
                    CreatedAt = now,
                    DeadlineAt = now.AddSeconds(snapshot.EffectiveTimeoutSeconds),
                    UpdatedAt = now,
                };
                entry.Skills.AddRange(skills);
                AddEventUnsafe(entry, Guid.NewGuid(), "run_created", null, EmptyPayload());
                _runs.Add(runId, entry);
                var (command, dispatch) = NewClaimedCommand(
                    runId, "start", requestHash, JsonSerializer.SerializeToElement(new
                    {
                        message = envelope.Objective,
                        task_envelope = canonicalEnvelope,
                    }));
                _commands.Add(key, command);
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Success, ToResponse(entry), Dispatch: dispatch));
            }
        }
    }

    public Task<AgentRunResponse?> GetAsync(
        string tenantId, string userId, Guid runId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Find(tenantId, userId, runId) is { } entry
                ? ToResponse(entry)
                : null);
        }
    }

    public Task<string?> GetExecutionArtifactAsync(
        string tenantId, string userId, Guid runId, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            return Task.FromResult(entry is null
                ? null
                : AgentRunSnapshotBuilder.CreateExecutionArtifact(
                    entry.SnapshotCanonical,
                    entry.SnapshotHash));
        }
    }

    public Task<AgentRunEventsResponse?> GetEventsAsync(
        string tenantId,
        string userId,
        Guid runId,
        long afterSequence,
        int limit,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null)
            {
                return Task.FromResult<AgentRunEventsResponse?>(null);
            }

            var rows = entry.Events
                .Where(e => e.Sequence > afterSequence)
                .OrderBy(e => e.Sequence)
                .Take(limit)
                .Select(ToEventResponse)
                .ToList();
            var next = rows.Count == 0 ? afterSequence : rows[^1].Sequence;
            return Task.FromResult<AgentRunEventsResponse?>(
                new AgentRunEventsResponse(runId, rows, next));
        }
    }

    public Task<AgentRunWriteResult> ResumeAsync(
        string tenantId,
        string userId,
        Guid runId,
        string message,
        long expectedCheckpointVersion,
        string idempotencyKey,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null)
            {
                return Task.FromResult(NotFound());
            }

            var key = CommandKey(tenantId, userId, "resume", idempotencyKey);
            var requestHash = SkillHash.Sha256($"{runId:D}\0{expectedCheckpointVersion}\0{message}");
            if (Replay(key, requestHash, out var replay))
            {
                return Task.FromResult(replay);
            }
            if (entry.CancelRequestedAt is not null
                || entry.Status != AgentRunStatuses.WaitingInput
                || entry.CheckpointVersion != expectedCheckpointVersion
                || !AgentRunCheckpointRef.IsValidPromotion(
                    entry.CheckpointRef,
                    entry.CheckpointGeneration))
            {
                return Task.FromResult(InvalidState("run 不在可 resume 的 waiting_input checkpoint"));
            }

            entry.Status = AgentRunStatuses.Queued;
            entry.PendingInput = null;
            entry.StateVersion++;
            entry.UpdatedAt = UtcNow();
            AddEventUnsafe(entry, Guid.NewGuid(), "input_resumed", null, EmptyPayload());
            var (command, dispatch) = NewClaimedCommand(
                runId,
                "resume",
                requestHash,
                JsonSerializer.SerializeToElement(new
                {
                    message,
                    expected_checkpoint_version = expectedCheckpointVersion,
                    expected_checkpoint_ref = entry.CheckpointRef,
                }));
            _commands.Add(key, command);
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.Success,
                ToResponse(entry),
                Dispatch: dispatch));
        }
    }

    public Task<AgentRunWriteResult> CancelAsync(
        string tenantId,
        string userId,
        Guid runId,
        string? reason,
        string idempotencyKey,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null)
            {
                return Task.FromResult(NotFound());
            }

            var key = CommandKey(tenantId, userId, "cancel", idempotencyKey);
            var requestHash = SkillHash.Sha256($"{runId:D}\0{reason ?? string.Empty}");
            if (Replay(key, requestHash, out var replay))
            {
                return Task.FromResult(replay);
            }
            if (!AgentRunStatuses.Terminal.Contains(entry.Status)
                && _commands.Values.Any(command =>
                    command.RunId == runId
                    && command.Type == "deadline_cleanup"))
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict,
                    ToResponse(entry),
                    "deadline cleanup is already in progress"));
            }
            if (AgentRunStatuses.Terminal.Contains(entry.Status))
            {
                if (entry.Status != AgentRunStatuses.Cancelled)
                {
                    return Task.FromResult(InvalidState("terminal run 不可取消"));
                }

                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Replay,
                    ToResponse(entry),
                    Replayed: true));
            }
            if (entry.CancelRequestedAt is not null)
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Replay,
                    ToResponse(entry),
                    Replayed: true));
            }

            var now = UtcNow();
            entry.CancelRequestedAt = now;
            entry.StateVersion++;
            entry.UpdatedAt = now;
            AddEventUnsafe(entry, Guid.NewGuid(), "cancel_requested", null, EmptyPayload());
            var (command, dispatch) = NewClaimedCommand(
                runId,
                "cancel",
                requestHash,
                JsonSerializer.SerializeToElement(new { reason }));
            _commands.Add(key, command);
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.Success,
                ToResponse(entry),
                Dispatch: dispatch));
        }
    }

    public Task<AgentRunWriteResult> TransitionAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunTransitionRequest request,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null)
            {
                return Task.FromResult(NotFound());
            }
            if (entry.StateVersion != request.ExpectedVersion)
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict, ToResponse(entry), "run state version 衝突"));
            }
            var now = UtcNow();
            var deadlineElapsed = entry.DeadlineAt <= now;
            var deadlineCleanupTarget = deadlineElapsed
                ? DeadlineCleanupTarget(entry)
                : null;
            var deadlineCleanupTransition =
                deadlineCleanupTarget is AgentRunStatuses.Failed or AgentRunStatuses.Cancelled
                && request.ToStatus == deadlineCleanupTarget;
            var checkpointOnlyPromotion =
                entry.Status == AgentRunStatuses.Running
                && request.ToStatus == AgentRunStatuses.Running;
            if (entry.CancelRequestedAt is not null
                && request.ToStatus != AgentRunStatuses.Cancelled
                && !deadlineCleanupTransition)
            {
                return Task.FromResult(InvalidState(
                    "已要求取消的 run 只能轉為 cancelled"));
            }
            if (request.ToStatus is null
                || !AgentRunStatuses.All.Contains(request.ToStatus)
                || !AgentRunStatuses.CanTransition(entry.Status, request.ToStatus)
                && !deadlineCleanupTransition
                && !checkpointOnlyPromotion)
            {
                return Task.FromResult(InvalidState("不合法的 run 狀態轉移"));
            }
            var terminal = AgentRunStatuses.Terminal.Contains(request.ToStatus!);
            var stateVersionCeiling =
                terminal ? long.MaxValue : long.MaxValue - 1;
            if (entry.StateVersion < 0
                || entry.StateVersion >= stateVersionCeiling)
            {
                return Task.FromResult(InvalidState(
                    "run state version has insufficient lifecycle headroom"));
            }
            if (!AgentRunLeasePolicy.Matches(
                    entry.LeaseTokenHash,
                    entry.LeaseGeneration,
                    entry.LeaseExpiresAt,
                    request.LeaseToken,
                    request.LeaseGeneration,
                    now))
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict, ToResponse(entry), "run lease 無效或已過期"));
            }
            if (request.ExpectedEventAckCursor is not long expectedEventAckCursor
                || expectedEventAckCursor != entry.EventAckCursor)
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict,
                    ToResponse(entry),
                    "event acknowledgement cursor changed"));
            }
            if (deadlineCleanupTransition)
            {
                var deadlineAuditPersisted = entry.Events.Any(item =>
                    item.LeaseGeneration == request.LeaseGeneration
                    && item.EventType == "deadline_exceeded");
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
                    return Task.FromResult(InvalidState(
                        "deadline cleanup requires one persisted sanitized audit and fixed terminal metadata"));
                }
            }
            var checkpointRef = request.CheckpointRef;
            var hasCheckpointRef = checkpointRef is not null;
            var hasCheckpointVersion = request.CheckpointVersion is not null;
            if (hasCheckpointRef != hasCheckpointVersion)
            {
                return Task.FromResult(InvalidState(
                    "checkpoint_ref and checkpoint_version must be promoted together"));
            }
            var promotionRequired = request.ToStatus is
                AgentRunStatuses.WaitingInput
                or AgentRunStatuses.WaitingApproval
                or AgentRunStatuses.Completed
                or AgentRunStatuses.Failed
                or AgentRunStatuses.Cancelled;
            var promotesCheckpoint = hasCheckpointRef && hasCheckpointVersion;
            var generationOnlyRunningPromotion = promotesCheckpoint
                && request.ToStatus == AgentRunStatuses.Running
                && request.LeaseGeneration > entry.CheckpointGeneration
                && request.CheckpointVersion == entry.CheckpointVersion;
            if (promotionRequired && !promotesCheckpoint)
            {
                return Task.FromResult(InvalidState(
                    "waiting or terminal transitions require a checkpoint promotion"));
            }
            if (promotesCheckpoint
                && (!AgentRunCheckpointRef.IsValidPromotion(
                        checkpointRef,
                        request.LeaseGeneration)
                    || request.CheckpointVersion is not long promotedCheckpointVersion
                    || (promotedCheckpointVersion <= entry.CheckpointVersion
                        && !generationOnlyRunningPromotion)))
            {
                return Task.FromResult(InvalidState(
                    "checkpoint promotion must advance with a canonical v2 reference for the active lease generation"));
            }
            if (promotesCheckpoint
                && !terminal
                && request.CheckpointVersion == long.MaxValue)
            {
                return Task.FromResult(InvalidState(
                    "nonterminal checkpoint promotion must preserve one terminal version"));
            }
            if (checkpointOnlyPromotion
                && (!promotesCheckpoint
                    || HasJsonValue(request.PendingInput)
                    || HasJsonValue(request.Result)
                    || request.ErrorCode is not null
                    || request.ErrorMessage is not null))
            {
                return Task.FromResult(InvalidState(
                    "running checkpoint promotion cannot mutate status, payload, result, or error metadata"));
            }
            if (deadlineElapsed && !deadlineCleanupTransition)
            {
                return Task.FromResult(InvalidState("run deadline has elapsed"));
            }
            if (request.ToStatus is AgentRunStatuses.WaitingInput or AgentRunStatuses.WaitingApproval
                && (checkpointRef is null
                    || request.CheckpointVersion is not long waitingCheckpointVersion
                    || waitingCheckpointVersion <= entry.CheckpointVersion))
            {
                return Task.FromResult(InvalidState(
                    "waiting state requires a nonblank checkpoint_ref and an advanced checkpoint_version"));
            }
            if (request.CheckpointVersion is long checkpointVersion
                && checkpointVersion < entry.CheckpointVersion)
            {
                return Task.FromResult(InvalidState("checkpoint_version 不可倒退"));
            }
            if (!AgentRunEventPolicy.WithinJsonLimit(request.PendingInput, 64 * 1024)
                || !AgentRunEventPolicy.WithinJsonLimit(request.Result, 1024 * 1024))
            {
                return Task.FromResult(InvalidState("pending_input 或 result 超過上限"));
            }

            if (checkpointOnlyPromotion)
            {
                entry.CheckpointGeneration = request.LeaseGeneration;
                entry.CheckpointRef = checkpointRef;
                entry.CheckpointVersion = request.CheckpointVersion!.Value;
                entry.StateVersion++;
                entry.UpdatedAt = now;
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Success,
                    ToResponse(entry)));
            }

            entry.Status = request.ToStatus;
            if (promotesCheckpoint)
            {
                entry.CheckpointGeneration = request.LeaseGeneration;
                entry.CheckpointRef = checkpointRef;
            }
            entry.CheckpointVersion = request.CheckpointVersion ?? entry.CheckpointVersion;
            entry.PendingInput = Clone(request.PendingInput);
            entry.Result = Clone(request.Result);
            entry.ErrorCode = AgentRunEventPolicy.Normalize(request.ErrorCode, 100);
            entry.ErrorMessage = AgentRunEventPolicy.Normalize(request.ErrorMessage, 500);
            entry.StateVersion++;
            entry.UpdatedAt = now;
            entry.StartedAt ??= request.ToStatus == AgentRunStatuses.Running ? now : null;
            if (AgentRunStatuses.Terminal.Contains(request.ToStatus))
            {
                entry.CompletedAt = now;
            }
            if (AgentRunStatuses.Terminal.Contains(request.ToStatus)
                || request.ToStatus is AgentRunStatuses.WaitingInput or AgentRunStatuses.WaitingApproval)
            {
                entry.LeaseOwner = null;
                entry.LeaseTokenHash = null;
                entry.LeaseExpiresAt = null;
                entry.LeaseCommandId = null;
            }
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.Success, ToResponse(entry)));
        }
    }

    public Task<AgentRunWriteResult> AppendEventsAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunEventsAppendRequest request,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null)
            {
                return Task.FromResult(NotFound());
            }
            var now = UtcNow();
            var deadlineElapsed = entry.DeadlineAt <= now;
            var deadlineCleanupTarget = deadlineElapsed
                ? DeadlineCleanupTarget(entry)
                : null;
            var deadlineCleanupLease = deadlineCleanupTarget is
                AgentRunStatuses.Failed or AgentRunStatuses.Cancelled;
            if (deadlineElapsed && !deadlineCleanupLease)
            {
                return Task.FromResult(InvalidState("run deadline has elapsed"));
            }
            if (request.ExpectedVersion != entry.StateVersion)
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict, ToResponse(entry), "run state version 衝突"));
            }
            if (AgentRunStatuses.Terminal.Contains(entry.Status))
            {
                return Task.FromResult(InvalidState("terminal run 不接受 events"));
            }
            if (!AgentRunLeasePolicy.Matches(
                    entry.LeaseTokenHash,
                    entry.LeaseGeneration,
                    entry.LeaseExpiresAt,
                    request.LeaseToken,
                    request.LeaseGeneration,
                    now))
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict, ToResponse(entry), "run lease 無效或已過期"));
            }
            if (request.Events is null)
            {
                return Task.FromResult(InvalidState("events 不可為 null"));
            }
            if (request.Events.Count is < 1 or > 100)
            {
                return Task.FromResult(InvalidState("單次最多追加 100 個 events"));
            }
            if (deadlineElapsed
                && request.Events.Any(item =>
                    !DeadlineCleanupAuditEventTypes.Contains(
                        item.EventType?.Trim() ?? string.Empty)))
            {
                return Task.FromResult(InvalidState(
                    "deadline cleanup accepts only sanitized deadline audit events"));
            }
            long eventCursorEnd;
            try
            {
                eventCursorEnd = checked(request.EventCursorStart + request.Events.Count);
            }
            catch (OverflowException)
            {
                return Task.FromResult(InvalidState("event cursor is out of range"));
            }
            if (request.EventCursorStart < 0
                || request.EventCursorStart > entry.EventAckCursor
                || request.EventCursorStart < entry.EventAckCursor
                && eventCursorEnd > entry.EventAckCursor)
            {
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Conflict,
                    ToResponse(entry),
                    "event cursor has a gap or partial overlap"));
            }
            if (entry.CancelRequestedAt is not null
                && !deadlineCleanupLease
                && request.Events.Any(item =>
                    !CancelAuditEventTypes.Contains(item.EventType?.Trim() ?? string.Empty)))
            {
                return Task.FromResult(InvalidState(
                    "cancel-requested run 只接受受限的 runtime audit events"));
            }

            for (var index = 0; index < request.Events.Count; index++)
            {
                var item = request.Events[index];
                var eventCursor = request.EventCursorStart + index;
                // 訊息與 Dapper(生產權威)逐字對齊:同一組條件在那邊是單一訊息,
                // 拆成兩句會讓 lite/測試看到的錯誤字串與生產不同。
                if (item.EventId == Guid.Empty
                    || string.IsNullOrWhiteSpace(item.EventType)
                    || item.EventType.Trim().Length > 100
                    || !string.Equals(item.SnapshotHash, entry.SnapshotHash, StringComparison.Ordinal)
                    || !AgentRunEventPolicy.IsSafePayload(item.Payload))
                {
                    return Task.FromResult(InvalidState("event id/type/snapshot_hash/payload 無效"));
                }
                var prior = entry.Events.FirstOrDefault(e => e.EventId == item.EventId);
                if (prior is not null
                    && (prior.EventCursor != eventCursor
                        || !AgentRunEventPolicy.ReplayMatches(
                            prior.EventType, prior.NodeId, prior.SnapshotHash,
                            prior.Payload.GetRawText(), item, entry.SnapshotHash)))
                {
                    return Task.FromResult(new AgentRunWriteResult(
                        AgentRunWriteStatus.Conflict,
                        ToResponse(entry),
                        "event_id 已用於不同內容"));
                }
            }

            if (request.EventCursorStart < entry.EventAckCursor)
            {
                for (var index = 0; index < request.Events.Count; index++)
                {
                    var item = request.Events[index];
                    var prior = entry.Events.FirstOrDefault(
                        candidate => candidate.EventCursor == request.EventCursorStart + index);
                    if (prior is null
                        || prior.EventId != item.EventId
                        || !AgentRunEventPolicy.ReplayMatches(
                            prior.EventType, prior.NodeId, prior.SnapshotHash,
                            prior.Payload.GetRawText(), item, entry.SnapshotHash))
                    {
                        return Task.FromResult(new AgentRunWriteResult(
                            AgentRunWriteStatus.Conflict,
                            ToResponse(entry),
                            "event cursor replay does not match persisted events"));
                    }
                }
                return Task.FromResult(new AgentRunWriteResult(
                    AgentRunWriteStatus.Replay,
                    ToResponse(entry),
                    Replayed: true));
            }

            var nextEventCursor = request.EventCursorStart;
            foreach (var item in request.Events)
            {
                var eventCursor = nextEventCursor++;
                if (entry.Events.Any(e => e.EventId == item.EventId))
                {
                    continue;
                }
                AddEventUnsafe(
                    entry,
                    item.EventId,
                    item.EventType!.Trim(),
                    AgentRunEventPolicy.Normalize(item.NodeId, 200),
                    Clone(item.Payload) ?? EmptyPayload(),
                    request.LeaseGeneration,
                    eventCursor);
            }
            entry.EventAckCursor = eventCursorEnd;
            return Task.FromResult(new AgentRunWriteResult(
                AgentRunWriteStatus.Success, ToResponse(entry)));
        }
    }

    public Task<AgentRunLeaseResult> ClaimLeaseAsync(
        string tenantId,
        string userId,
        Guid runId,
        AgentRunLeaseRequest request,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null)
            {
                return Task.FromResult(new AgentRunLeaseResult(AgentRunWriteStatus.NotFound));
            }
            var now = UtcNow();
            if (entry.StateVersion != request.ExpectedVersion)
            {
                return Task.FromResult(new AgentRunLeaseResult(
                    AgentRunWriteStatus.Conflict, Message: "run state version 衝突"));
            }
            if (AgentRunStatuses.Terminal.Contains(entry.Status)
                || entry.DeadlineAt <= now
                || entry.StateVersion < 0
                || entry.StateVersion >= long.MaxValue - 1
                || string.IsNullOrWhiteSpace(request.Owner)
                || request.DurationSeconds is < 5 or > 900
                || entry.LeaseExpiresAt is DateTime expiry && expiry > now
                && !string.Equals(entry.LeaseOwner, request.Owner.Trim(), StringComparison.Ordinal))
            {
                return Task.FromResult(new AgentRunLeaseResult(
                    AgentRunWriteStatus.InvalidState, Message: "run 無法取得 lease"));
            }

            var activeSameOwner = entry.LeaseExpiresAt is DateTime activeExpiry
                                  && activeExpiry > now
                                  && string.Equals(
                                      entry.LeaseOwner,
                                      request.Owner.Trim(),
                                      StringComparison.Ordinal);
            if (!activeSameOwner && entry.LeaseGeneration == long.MaxValue)
            {
                return Task.FromResult(new AgentRunLeaseResult(
                    AgentRunWriteStatus.InvalidState,
                    Message: "run lease generation is exhausted"));
            }
            var token = AgentRunLeasePolicy.NewToken();
            entry.LeaseGeneration = activeSameOwner
                ? entry.LeaseGeneration
                : checked(entry.LeaseGeneration + 1);
            entry.LeaseTokenHash = SkillHash.Sha256(token);
            entry.LeaseOwner = request.Owner.Trim();
            entry.LeaseExpiresAt = now.AddSeconds(request.DurationSeconds);
            entry.LeaseCommandId = null;
            entry.StateVersion = checked(entry.StateVersion + 1);
            entry.UpdatedAt = now;
            return Task.FromResult(new AgentRunLeaseResult(
                AgentRunWriteStatus.Success,
                new AgentRunLeaseResponse(
                    token,
                    entry.LeaseGeneration,
                    entry.LeaseExpiresAt.Value,
                    entry.CheckpointGeneration,
                    entry.CheckpointRef,
                    entry.CheckpointVersion,
                    entry.EventAckCursor,
                    ToResponse(entry))));
        }
    }

    public Task<AgentRunCommandClaimResult> ClaimCommandAsync(
        string tenantId,
        string userId,
        Guid runId,
        Guid commandId,
        AgentRunCommandClaimRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            var command = _commands.Values.FirstOrDefault(
                item => item.Id == commandId && item.RunId == runId);
            if (entry is null || command is null)
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.NotFound));
            }
            var latest = _commands.Values
                .Where(item => item.RunId == runId)
                .OrderByDescending(item =>
                    item.Type == "cancel" && item.DispatchCompletedAt is null)
                .ThenByDescending(item => item.Sequence)
                .First();
            if (latest.Id != commandId)
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.Conflict,
                    Message: "command has been superseded"));
            }
            if (!AgentRunCommandInput.TryValidate(
                    command.Input,
                    command.Type,
                    entry.CheckpointGeneration,
                    entry.CheckpointVersion,
                    entry.CheckpointRef,
                    AgentRunCommandValidationMode.DirectClaim,
                    out var validatedInput,
                    out _)
                || !AgentRunCommandInput.MatchesCanonicalSha256(
                    validatedInput,
                    command.Type,
                    command.InputHash))
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.InvalidState,
                    Message: "command input is invalid"));
            }

            var metadataOnly = command.Type == "cancel";
            JsonElement snapshotEnvelope = default;
            var role = entry.CallerRole;
            if (metadataOnly)
            {
                if (!AgentRunRecoveryPolicy.HasValidPinnedIdentity(
                        entry.TenantId, entry.UserId, entry.CallerRole))
                {
                    return Task.FromResult(new AgentRunCommandClaimResult(
                        AgentRunWriteStatus.InvalidState,
                        Message: "run caller identity is invalid"));
                }
            }
            else
            {
                var authoritativeSnapshot = AuthoritativeSnapshotOf(entry);
                snapshotEnvelope = authoritativeSnapshot.Envelope;
                role = authoritativeSnapshot.Role;
            }
            if (command.DispatchCompletedAt is not null)
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.Replay));
            }

            var now = UtcNow();
            var workerId = request.WorkerId!.Trim();
            if (AgentRunStatuses.Terminal.Contains(entry.Status)
                || entry.DeadlineAt <= now
                || entry.CancelRequestedAt is not null && command.Type != "cancel")
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.InvalidState,
                    Message: "run is not executable for this command"));
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
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.Conflict,
                    Message: "command is claimed by another worker"));
            }
            var forceTakeover = command.Type == "cancel";
            var activeSameOwner = !forceTakeover
                                  && entry.LeaseExpiresAt is DateTime leaseExpiry
                                  && leaseExpiry > now
                                  && string.Equals(
                                      entry.LeaseOwner,
                                      workerId,
                                      StringComparison.Ordinal);
            var resumeFromReleasedWait = command.Type == "resume"
                                         && entry.LeaseExpiresAt is null
                                         && entry.CheckpointGeneration
                                         == entry.LeaseGeneration
                                         && !string.IsNullOrWhiteSpace(
                                             entry.CheckpointRef);
            var reuseGeneration = activeSameOwner || resumeFromReleasedWait;
            if (entry.LeaseExpiresAt is DateTime activeLeaseExpiry
                && activeLeaseExpiry > now
                && !activeSameOwner
                && !forceTakeover)
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.Conflict,
                    Message: "run lease is owned by another worker"));
            }
            if (!reuseGeneration && entry.LeaseGeneration == long.MaxValue)
            {
                return Task.FromResult(new AgentRunCommandClaimResult(
                    AgentRunWriteStatus.InvalidState,
                    Message: "run lease generation is exhausted"));
            }

            var leaseToken = AgentRunLeasePolicy.NewToken();
            var claimToken = AgentRunLeasePolicy.NewToken();
            var expiresAt = now.AddSeconds(request.LeaseSeconds);
            entry.LeaseGeneration = reuseGeneration
                ? entry.LeaseGeneration
                : entry.LeaseGeneration + 1;
            entry.LeaseOwner = workerId;
            entry.LeaseTokenHash = SkillHash.Sha256(leaseToken);
            entry.LeaseExpiresAt = expiresAt;
            entry.LeaseCommandId = command.Id;
            entry.StateVersion++;
            entry.UpdatedAt = now;
            command.DispatchClaimOwner = workerId;
            command.DispatchClaimTokenHash = SkillHash.Sha256(claimToken);
            command.DispatchClaimExpiresAt = expiresAt;
            command.DispatchAttempts++;
            command.LastDispatchAt = now;
            return Task.FromResult(new AgentRunCommandClaimResult(
                AgentRunWriteStatus.Success,
                BuildCommandItem(
                    command,
                    entry,
                    role,
                    metadataOnly ? default : validatedInput,
                    snapshotEnvelope,
                    leaseToken,
                    expiresAt,
                    claimToken,
                    expiresAt,
                    metadataOnly ? AgentRunStatuses.Cancelled : null)));
        }
    }

    public Task<AgentRunDispatchCompleteStatus> CompleteDispatchAsync(
        string tenantId,
        string userId,
        Guid runId,
        Guid commandId,
        string claimToken,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var command = _commands
                .Where(pair => pair.Key.Tenant == tenantId && pair.Key.User == userId)
                .Select(pair => pair.Value)
                .FirstOrDefault(item => item.Id == commandId && item.RunId == runId);
            if (command is null)
            {
                return Task.FromResult(AgentRunDispatchCompleteStatus.NotFound);
            }
            if (command.DispatchClaimTokenHash is null
                || string.IsNullOrWhiteSpace(claimToken)
                || !string.Equals(
                    command.DispatchClaimTokenHash,
                    SkillHash.Sha256(claimToken),
                    StringComparison.Ordinal))
            {
                return Task.FromResult(AgentRunDispatchCompleteStatus.Conflict);
            }

            command.DispatchCompletedAt = UtcNow();
            command.DispatchClaimOwner = null;
            command.DispatchClaimTokenHash = null;
            command.DispatchClaimExpiresAt = null;
            return Task.FromResult(AgentRunDispatchCompleteStatus.Success);
        }
    }

    public Task<AgentRunRecoveryClaimResponse> ClaimRecoveryAsync(
        AgentRunRecoveryClaimRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = UtcNow();
            var candidates = _runs.Values
                .Select(run => new
                {
                    Run = run,
                    Command = _commands.Values
                        .Where(command => command.RunId == run.Id
                                          && command.Type is
                                              ("start" or "resume" or "cancel"
                                              or "deadline_cleanup"))
                        .OrderByDescending(command =>
                            command.Type == "deadline_cleanup")
                        .ThenByDescending(command =>
                            command.Type == "cancel")
                        .ThenByDescending(command => command.Sequence)
                        .FirstOrDefault(),
                })
                .Where(item => item.Command is not null)
                .Where(item =>
                {
                    var command = item.Command!;
                    if (AgentRunStatuses.Terminal.Contains(item.Run.Status))
                    {
                        return false;
                    }
                    var dispatchClaimAvailable =
                        command.DispatchClaimExpiresAt is not DateTime claimExpiry
                        || claimExpiry <= now;
                    var executionLeaseInactive =
                        item.Run.LeaseExpiresAt is not DateTime leaseExpiry
                        || leaseExpiry <= now;
                    if (item.Run.DeadlineAt <= now)
                    {
                        return command.Type != "deadline_cleanup"
                               || command.DispatchCompletedAt is null
                               && dispatchClaimAvailable
                               || command.DispatchCompletedAt is DateTime cleanupCompletedAt
                               && executionLeaseInactive
                               && (!string.Equals(
                                       item.Run.LeaseTokenHash,
                                       command.ExecutionRecoveryLeaseTokenHash,
                                       StringComparison.Ordinal)
                                   || cleanupCompletedAt <= now.AddSeconds(-30));
                    }
                    var cancelRecovery =
                        item.Run.CancelRequestedAt is not null
                        && command.Type == "cancel"
                        && (command.DispatchCompletedAt is null
                            && dispatchClaimAvailable
                            || command.DispatchCompletedAt is DateTime cancelCompletedAt
                            && executionLeaseInactive
                            && (!string.Equals(
                                    item.Run.LeaseTokenHash,
                                    command.ExecutionRecoveryLeaseTokenHash,
                                    StringComparison.Ordinal)
                                || cancelCompletedAt <= now.AddSeconds(-30)));
                    var unacknowledgedDispatch =
                        item.Run.CancelRequestedAt is null
                        && command.DispatchCompletedAt is null
                        && executionLeaseInactive
                        && dispatchClaimAvailable;
                    var acknowledgedExecution =
                        item.Run.CancelRequestedAt is null
                        && command.DispatchCompletedAt is DateTime completedAt
                        && command.Type is ("start" or "resume")
                        && executionLeaseInactive
                        && (!string.Equals(
                                item.Run.LeaseTokenHash,
                                command.ExecutionRecoveryLeaseTokenHash,
                                StringComparison.Ordinal)
                            || completedAt <= now.AddSeconds(-30));
                    return cancelRecovery
                           || unacknowledgedDispatch
                           || acknowledgedExecution;
                })
                .OrderBy(item => item.Run.UpdatedAt)
                .ThenBy(item => item.Run.Id)
                .ToList();
            var items = new List<AgentRunRecoveryItem>(request.Limit);
            var hasMore = false;
            foreach (var candidate in candidates)
            {
                if (items.Count >= request.Limit)
                {
                    hasMore = true;
                    break;
                }

                var runCounterError = AgentRunRecoveryPolicy.CounterError(
                    candidate.Run.LeaseGeneration, candidate.Run.StateVersion,
                    candidate.Run.CheckpointVersion, candidate.Run.EventAckCursor,
                    candidate.Run.LatestEventSequence);
                if (runCounterError is not null)
                {
                    QuarantineExhaustedRecoveryCandidate(
                        candidate.Run,
                        runCounterError);
                    continue;
                }

                var deadlineCleanup = candidate.Run.DeadlineAt <= now;
                var command = deadlineCleanup
                    ? EnsureDeadlineCleanupCommand(candidate.Run)
                    : candidate.Command!;
                if (command.DispatchAttempts is < 0 or >= int.MaxValue - 1)
                {
                    QuarantineExhaustedRecoveryCandidate(
                        candidate.Run,
                        "run_recovery_counter_exhausted");
                    continue;
                }
                if (!AgentRunRecoveryPolicy.HasValidCheckpointSeed(
                        candidate.Run.CheckpointGeneration, candidate.Run.CheckpointRef,
                        candidate.Run.CheckpointVersion))
                {
                    DeadLetterRecoveryCandidate(
                        candidate.Run,
                        "run_recovery_seed_invalid");
                    continue;
                }
                if (!AgentRunRecoveryPolicy.HasValidPinnedIdentity(
                        candidate.Run.TenantId, candidate.Run.UserId,
                        candidate.Run.CallerRole))
                {
                    DeadLetterRecoveryCandidate(
                        candidate.Run,
                        "run_recovery_identity_invalid");
                    continue;
                }
                if (!AgentRunCommandInput.TryValidate(
                        command.Input,
                        command.Type,
                        candidate.Run.CheckpointGeneration,
                        candidate.Run.CheckpointVersion,
                        candidate.Run.CheckpointRef,
                        AgentRunCommandValidationMode.Recovery,
                        out var commandInput,
                        out var parsedTargetTerminal)
                    || !AgentRunCommandInput.MatchesCanonicalSha256(
                        commandInput,
                        command.Type,
                        command.InputHash))
                {
                    DeadLetterRecoveryCandidate(
                        candidate.Run,
                        "run_recovery_command_invalid");
                    continue;
                }
                (JsonElement Envelope, string Role) authoritativeSnapshot = default;
                string? targetTerminal = null;
                var metadataOnly =
                    deadlineCleanup || command.Type == "cancel";
                if (deadlineCleanup)
                {
                    targetTerminal = parsedTargetTerminal;
                    if (targetTerminal is not
                        (AgentRunStatuses.Failed or AgentRunStatuses.Cancelled))
                    {
                        DeadLetterRecoveryCandidate(
                            candidate.Run,
                            "run_recovery_seed_invalid");
                        continue;
                    }
                }
                else if (command.Type == "cancel")
                {
                    targetTerminal = AgentRunStatuses.Cancelled;
                }
                if (!metadataOnly)
                {
                    try
                    {
                        authoritativeSnapshot = AuthoritativeSnapshotOf(candidate.Run);
                    }
                    catch (Exception ex) when (ex is JsonException
                                               or InvalidOperationException
                                               or KeyNotFoundException)
                    {
                        DeadLetterRecoveryCandidate(
                            candidate.Run,
                            "run_recovery_snapshot_invalid");
                        continue;
                    }
                }

                var forceTakeover =
                    deadlineCleanup
                    || command.Type == "cancel" && command.DispatchCompletedAt is null;
                if (!forceTakeover
                    && candidate.Run.LeaseExpiresAt is DateTime activeLeaseExpiry
                    && activeLeaseExpiry > now)
                {
                    continue;
                }
                var token = AgentRunLeasePolicy.NewToken();
                var leaseToken = AgentRunLeasePolicy.NewToken();
                var expiresAt = now.AddSeconds(request.LeaseSeconds);
                candidate.Run.LeaseGeneration =
                    checked(candidate.Run.LeaseGeneration + 1);
                candidate.Run.LeaseOwner = request.WorkerId!.Trim();
                candidate.Run.LeaseTokenHash = SkillHash.Sha256(leaseToken);
                candidate.Run.LeaseExpiresAt = expiresAt;
                candidate.Run.LeaseCommandId = command.Id;
                candidate.Run.StateVersion =
                    checked(candidate.Run.StateVersion + 1);
                candidate.Run.UpdatedAt = now;
                command.DispatchClaimOwner = request.WorkerId!.Trim();
                command.DispatchClaimTokenHash = SkillHash.Sha256(token);
                command.DispatchClaimExpiresAt = expiresAt;
                command.DispatchAttempts =
                    checked(command.DispatchAttempts + 1);
                command.LastDispatchAt = now;
                if (command.DispatchCompletedAt is not null)
                {
                    command.ExecutionRecoveryLeaseTokenHash =
                        candidate.Run.LeaseTokenHash;
                }
                command.DispatchCompletedAt = null;
                items.Add(new AgentRunRecoveryItem(
                    command.Id,
                    candidate.Run.Id,
                    command.Type,
                    metadataOnly ? default : commandInput,
                    candidate.Run.TenantId,
                    candidate.Run.UserId,
                    metadataOnly
                        ? candidate.Run.CallerRole
                        : authoritativeSnapshot.Role,
                    candidate.Run.SnapshotHash,
                    metadataOnly ? default : authoritativeSnapshot.Envelope,
                    candidate.Run.Status,
                    candidate.Run.StateVersion,
                    candidate.Run.LeaseGeneration,
                    candidate.Run.CheckpointGeneration,
                    candidate.Run.CheckpointRef,
                    candidate.Run.CheckpointVersion,
                    candidate.Run.EventAckCursor,
                    candidate.Run.DeadlineAt,
                    leaseToken,
                    expiresAt,
                    token,
                    expiresAt,
                    command.DispatchAttempts,
                    targetTerminal));
            }

            return Task.FromResult(new AgentRunRecoveryClaimResponse(
                items,
                hasMore));
        }
    }

    private bool Replay(
        (string Tenant, string User, string Type, string KeyHash) key,
        string requestHash,
        out AgentRunWriteResult result)
    {
        if (!_commands.TryGetValue(key, out var command))
        {
            result = null!;
            return false;
        }
        if (!string.Equals(command.RequestHash, requestHash, StringComparison.Ordinal))
        {
            result = new AgentRunWriteResult(
                AgentRunWriteStatus.Conflict, Message: "Idempotency-Key 已用於不同請求");
            return true;
        }
        result = new AgentRunWriteResult(
            AgentRunWriteStatus.Replay,
            _runs.TryGetValue(command.RunId, out var run) ? ToResponse(run) : null,
            Replayed: true);
        return true;
    }

    private (CommandEntry Command, AgentRunCommandDispatch Dispatch) NewClaimedCommand(
        Guid runId,
        string type,
        string requestHash,
        JsonElement input)
    {
        var now = UtcNow();
        var token = AgentRunLeasePolicy.NewToken();
        var expiresAt = now.AddSeconds(30);
        var command = new CommandEntry
        {
            Id = Guid.NewGuid(),
            Sequence = ++_latestCommandSequence,
            RunId = runId,
            Type = type,
            RequestHash = requestHash,
            Input = input.Clone(),
            InputHash = AgentRunCommandInput.CanonicalSha256(input, type),
            DispatchClaimOwner = "platform",
            DispatchClaimTokenHash = SkillHash.Sha256(token),
            DispatchClaimExpiresAt = expiresAt,
            DispatchAttempts = 1,
            LastDispatchAt = now,
            CreatedAt = now,
        };
        return (
            command,
            new AgentRunCommandDispatch(command.Id, token, expiresAt, command.DispatchAttempts));
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static (JsonElement Envelope, string Role) AuthoritativeSnapshotOf(Entry entry)
    {
        var canonicalSnapshot = AgentRunSnapshotBuilder.ReadAuthoritativeSnapshot(
            entry.SnapshotCanonical,
            entry.SnapshotHash);
        using var document = JsonDocument.Parse(canonicalSnapshot);
        var caller = document.RootElement.GetProperty("caller");
        var tenantId = caller.GetProperty("tenant_id").GetString();
        var userId = caller.GetProperty("user_id").GetString();
        var role = caller.GetProperty("role").GetString();
        if (!AgentRunRecoveryPolicy.HasValidPinnedIdentity(tenantId, userId, role)
            || !string.Equals(tenantId, entry.TenantId, StringComparison.Ordinal)
            || !string.Equals(userId, entry.UserId, StringComparison.Ordinal)
            || !string.Equals(role, entry.CallerRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Run snapshot has invalid or mismatched caller identity");
        }
        var envelope = JsonDocument.Parse(
            AgentRunSnapshotBuilder.CreateExecutionArtifact(
                entry.SnapshotCanonical,
                entry.SnapshotHash)).RootElement.Clone();
        return (envelope, role!);
    }

    private string? DeadlineCleanupTarget(Entry entry)
    {
        if (entry.LeaseCommandId is not Guid commandId)
        {
            return null;
        }

        var command = _commands.Values.FirstOrDefault(
            item => item.Id == commandId
                    && item.RunId == entry.Id
                    && item.Type == "deadline_cleanup");
        return command is null ? null : DeadlineCleanupTarget(command);
    }

    private static string? DeadlineCleanupTarget(CommandEntry command)
    {
        if (!command.Input.TryGetProperty("target_terminal", out var target))
        {
            return null;
        }

        return target.GetString() is AgentRunStatuses.Failed or AgentRunStatuses.Cancelled
            ? target.GetString()
            : null;
    }

    private CommandEntry EnsureDeadlineCleanupCommand(Entry entry)
    {
        var existing = _commands.Values.FirstOrDefault(
            command => command.RunId == entry.Id
                       && command.Type == "deadline_cleanup");
        if (existing is not null)
        {
            return existing;
        }

        var targetTerminal = entry.CancelRequestedAt is null
            ? AgentRunStatuses.Failed
            : AgentRunStatuses.Cancelled;
        var key = CommandKey(
            entry.TenantId,
            entry.UserId,
            "deadline_cleanup",
            $"deadline_cleanup\0{entry.Id:D}");
        var now = UtcNow();
        var input = JsonSerializer.SerializeToElement(new
        {
            target_terminal = targetTerminal,
        });
        var command = new CommandEntry
        {
            Id = Guid.NewGuid(),
            Sequence = ++_latestCommandSequence,
            RunId = entry.Id,
            Type = "deadline_cleanup",
            RequestHash = SkillHash.Sha256(
                $"{entry.Id:D}\0{targetTerminal}"),
            Input = input,
            InputHash = AgentRunCommandInput.CanonicalSha256(
                input,
                "deadline_cleanup"),
            CreatedAt = now,
        };
        _commands.Add(key, command);
        return command;
    }

    private void QuarantineExhaustedRecoveryCandidate(
        Entry entry,
        string errorCode)
    {
        var now = UtcNow();
        entry.Status = entry.CancelRequestedAt is null
            ? AgentRunStatuses.Failed
            : AgentRunStatuses.Cancelled;
        entry.PendingInput = null;
        entry.Result = null;
        entry.ErrorCode = errorCode;
        entry.ErrorMessage = "Run recovery counters were safely quarantined";
        entry.CompletedAt = now;
        entry.LeaseOwner = null;
        entry.LeaseTokenHash = null;
        entry.LeaseExpiresAt = null;
        entry.LeaseCommandId = null;
        entry.UpdatedAt = now;
        if (entry.LatestEventSequence >= 0
            && entry.LatestEventSequence < long.MaxValue)
        {
            AddEventUnsafe(
                entry,
                Guid.NewGuid(),
                "run_dead_lettered",
                null,
                JsonSerializer.SerializeToElement(new { error_code = errorCode }));
        }
        else
        {
            System.Diagnostics.Trace.TraceError(
                "Agent run {0} was quarantined without an audit event because "
                + "latest_event_sequence is exhausted",
                entry.Id);
        }

        foreach (var command in _commands.Values.Where(
                     command => command.RunId == entry.Id))
        {
            command.Input = EmptyPayload();
            command.DispatchCompletedAt ??= now;
            command.DispatchClaimOwner = null;
            command.DispatchClaimTokenHash = null;
            command.DispatchClaimExpiresAt = null;
        }
    }

    private void DeadLetterRecoveryCandidate(Entry entry, string errorCode)
    {
        var now = UtcNow();
        entry.Status = entry.CancelRequestedAt is null
            ? AgentRunStatuses.Failed
            : AgentRunStatuses.Cancelled;
        entry.StateVersion++;
        entry.PendingInput = null;
        entry.Result = null;
        entry.ErrorCode = errorCode;
        entry.ErrorMessage = "Run recovery was safely dead-lettered";
        entry.CompletedAt = now;
        entry.LeaseOwner = null;
        entry.LeaseTokenHash = null;
        entry.LeaseExpiresAt = null;
        entry.LeaseCommandId = null;
        entry.UpdatedAt = now;
        AddEventUnsafe(
            entry,
            Guid.NewGuid(),
            "run_dead_lettered",
            null,
            JsonSerializer.SerializeToElement(new { error_code = errorCode }));
        foreach (var command in _commands.Values.Where(
                     command => command.RunId == entry.Id))
        {
            command.Input = EmptyPayload();
            command.DispatchCompletedAt ??= now;
            command.DispatchClaimOwner = null;
            command.DispatchClaimTokenHash = null;
            command.DispatchClaimExpiresAt = null;
        }
    }

    private Entry? Find(string tenantId, string userId, Guid id)
        => _runs.GetValueOrDefault(id) is { } entry
           && entry.TenantId == tenantId
           && entry.UserId == userId
            ? entry
            : null;

    /// <summary>
    /// D7 evidence 寫入(<see cref="InMemoryAgentRunApprovalRepository.WriteEvidenceAsync"/>)必須在
    /// 重新取得 <c>_gate</c> 之後原子性地重新檢查 CancelRequested,而不是只信任外層先前讀到的快照 ——
    /// 同 <see cref="InMemorySkillRepository.ReferenceSyncRoot"/> 先例,只供同組件內協調,不是公開
    /// repository 契約。
    /// </summary>
    internal Lock ReferenceSyncRoot => _gate;

    /// <summary>呼叫端必須持有 <see cref="ReferenceSyncRoot"/>。找不到 run 時保守回傳「已取消」。</summary>
    internal bool IsCancelRequestedUnsafe(string tenantId, string userId, Guid runId)
        => Find(tenantId, userId, runId) is not { CancelRequestedAt: null };

    /// <summary>
    /// <see cref="IAgentRunCancellationFence"/> 轉接:讓 <see cref="InMemoryAgentRunApprovalRepository"/>
    /// 依薄介面取得同一把鎖與同一個原子重新檢查,不需要對這個具體型別做 downcast。
    /// </summary>
    Lock IAgentRunCancellationFence.SyncRoot => ReferenceSyncRoot;

    bool IAgentRunCancellationFence.IsCancelRequested(string tenantId, string userId, Guid runId)
        => IsCancelRequestedUnsafe(tenantId, userId, runId);

    public bool HasActiveApprovalLease(string tenantId, string userId, Guid runId, string leaseToken, long leaseGeneration)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            return entry is not null
                && entry.Status == AgentRunStatuses.Running
                && entry.CancelRequestedAt is null
                && AgentRunLeasePolicy.Matches(
                    entry.LeaseTokenHash, entry.LeaseGeneration, entry.LeaseExpiresAt,
                    leaseToken, leaseGeneration, UtcNow());
        }
    }

    public Task<AgentRunWriteResult> ResolveApprovalAsync(string tenantId, string userId, Guid runId, long expectedVersion, bool approve, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            if (entry is null) return Task.FromResult(NotFound());
            if (entry.Status != AgentRunStatuses.WaitingApproval || entry.StateVersion != expectedVersion)
                return Task.FromResult(InvalidState("approval decision state changed"));
            if (entry.CancelRequestedAt is not null)
                return Task.FromResult(InvalidState("approval decision is cancelled"));
            entry.Status = approve ? AgentRunStatuses.Queued : AgentRunStatuses.Failed;
            entry.StateVersion++;
            entry.UpdatedAt = UtcNow();
            if (!approve)
            {
                entry.CompletedAt = entry.UpdatedAt;
                entry.LeaseOwner = null;
                entry.LeaseTokenHash = null;
                entry.LeaseExpiresAt = null;
                entry.LeaseCommandId = null;
            }
            return Task.FromResult(new AgentRunWriteResult(AgentRunWriteStatus.Success, ToResponse(entry)));
        }
    }

    public Task FailApprovalRunAsync(string tenantId, string userId, Guid runId, AgentRunApprovalFailure failure, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = Find(tenantId, userId, runId);
            // Same WHERE clause as the PostgreSQL authority in each case: expiry terminalizes its
            // run even when cancellation was requested (whether the approval was still pending or
            // already approved but never executed), while a dead-lettered write leaves a
            // cancel-requested run to its cancel command.
            var eligible = entry is not null && failure switch
            {
                AgentRunApprovalFailure.ApprovalExpired => entry.Status == AgentRunStatuses.WaitingApproval,
                AgentRunApprovalFailure.ApprovalExpiredBeforeWrite => entry.Status is AgentRunStatuses.Queued or AgentRunStatuses.Running,
                _ => entry.Status is AgentRunStatuses.Queued or AgentRunStatuses.Running && entry.CancelRequestedAt is null,
            };
            if (!eligible)
            {
                return Task.CompletedTask;
            }

            var now = UtcNow();
            entry!.Status = AgentRunStatuses.Failed;
            entry.StateVersion++;
            (entry.ErrorCode, entry.ErrorMessage) = failure switch
            {
                AgentRunApprovalFailure.ApprovalExpired => ("approval_expired", "Approval expired before a decision."),
                AgentRunApprovalFailure.ApprovalExpiredBeforeWrite => ("approval_expired", "Approval expired before the write could execute."),
                _ => ("approved_write_unrecoverable", "Approved write could not be safely resumed."),
            };
            entry.CompletedAt = now;
            entry.UpdatedAt = now;
            entry.LeaseOwner = null;
            entry.LeaseTokenHash = null;
            entry.LeaseExpiresAt = null;
            entry.LeaseCommandId = null;
            return Task.CompletedTask;
        }
    }

    private static bool HasValue(JsonObject definition, string field, string value)
        => definition[field] is JsonArray values
           && values.Any(v => string.Equals(v?.GetValue<string>(), value, StringComparison.Ordinal));

    private static (string Tenant, string User, string Type, string KeyHash) CommandKey(
        string tenant, string user, string type, string key)
        => (tenant, user, type, SkillHash.Sha256(key));

    private static AgentRunWriteResult NotFound()
        => new(AgentRunWriteStatus.NotFound, Message: "找不到 Agent run");

    private static AgentRunWriteResult InvalidState(string message)
        => new(AgentRunWriteStatus.InvalidState, Message: message);

    private void AddEventUnsafe(
        Entry entry,
        Guid eventId,
        string eventType,
        string? nodeId,
        JsonElement payload,
        long? leaseGeneration = null,
        long? eventCursor = null)
    {
        entry.LatestEventSequence++;
        entry.Events.Add(new EventEntry(
            entry.LatestEventSequence,
            eventId,
            eventType,
            nodeId,
            leaseGeneration,
            eventCursor,
            entry.SnapshotHash,
            payload.Clone(),
            UtcNow()));
    }

    private static JsonElement EmptyPayload()
        => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement? Clone(JsonElement? value)
        => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } v
            ? v.Clone()
            : null;

    private static bool HasJsonValue(JsonElement? value)
        => value is
        { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    private static AgentRunResponse ToResponse(Entry e) => new(
        e.Id,
        e.RootRunId,
        e.ParentRunId,
        e.TaskId,
        e.RunKind,
        e.AgentId,
        e.AgentRevision,
        e.WorkflowId,
        e.WorkflowRevision,
        e.SnapshotHash,
        e.Status,
        e.StateVersion,
        e.LeaseGeneration,
        e.CheckpointGeneration,
        e.CheckpointRef,
        e.CheckpointVersion,
        e.EventAckCursor,
        e.CancelRequestedAt is not null,
        Clone(e.PendingInput),
        Clone(e.Result),
        e.ErrorCode,
        e.ErrorMessage,
        e.LatestEventSequence,
        e.StartedAt,
        e.CreatedAt,
        e.DeadlineAt,
        e.UpdatedAt,
        e.CompletedAt,
        e.Skills.Select(s => new AgentRunSkillPinResponse(
                s.Name, s.Revision, s.Kind, s.DefinitionSha256, s.PackageSha256))
            .ToList(),
        RuntimeLimitsOf(e.Snapshot));

    /// <summary>O2 unified list source (<see cref="IRunDiscoverySource"/>): every direct-agent/
    /// worker/verifier row this tenant/user owns. <c>OrchestratorRootRunId</c> reuses
    /// <see cref="Entry.ParentRunId"/> — the InMemory child-creation path already stores the
    /// owning root there (see <c>CreateOrchestratorChildAsync</c>), unlike the Dapper authority
    /// which uses a dedicated <c>orchestrator_root_run_id</c> column because its own
    /// <c>root_run_id</c>/<c>parent_run_id</c> stay self-referential/null for children.</summary>
    IReadOnlyList<RunSummaryItem> IRunDiscoverySource.ListRunSummaries(string tenantId, string userId, DateTime now)
    {
        lock (_gate)
        {
            return _runs.Values
                .Where(e => e.TenantId == tenantId && e.UserId == userId)
                .Select(e => ToRunSummaryItem(e, now))
                .ToArray();
        }
    }

    private static RunSummaryItem ToRunSummaryItem(Entry e, DateTime now)
    {
        var lastEvent = e.Events.Count > 0 ? e.Events[^1] : null;
        var start = e.StartedAt ?? e.CreatedAt;
        var end = e.CompletedAt ?? now;
        return new RunSummaryItem(
            e.Id,
            e.RunKind,
            e.Status,
            e.RunKind == "direct-agent" ? null : e.ParentRunId,
            e.TaskId,
            e.AgentId,
            e.AgentRevision,
            null,
            null,
            e.WorkflowId,
            e.WorkflowRevision,
            e.CancelRequestedAt is not null,
            RuntimeLimitsOf(e.Snapshot),
            lastEvent?.EventType,
            lastEvent?.CreatedAt,
            null,
            e.ErrorCode,
            e.Status == AgentRunStatuses.WaitingApproval,
            e.Status is AgentRunStatuses.Queued or AgentRunStatuses.Running
                && e.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt < now,
            e.StartedAt,
            e.CreatedAt,
            e.UpdatedAt,
            e.CompletedAt,
            Math.Max(0, (end - start).TotalSeconds));
    }

    private static AgentRunRecoveryItem BuildCommandItem(
        CommandEntry command,
        Entry entry,
        string role,
        JsonElement input,
        JsonElement snapshotEnvelope,
        string leaseToken,
        DateTime leaseExpiresAt,
        string claimToken,
        DateTime claimExpiresAt,
        string? targetTerminal)
        => new(
            command.Id,
            entry.Id,
            command.Type,
            input,
            entry.TenantId,
            entry.UserId,
            role,
            entry.SnapshotHash,
            snapshotEnvelope,
            entry.Status,
            entry.StateVersion,
            entry.LeaseGeneration,
            entry.CheckpointGeneration,
            entry.CheckpointRef,
            entry.CheckpointVersion,
            entry.EventAckCursor,
            entry.DeadlineAt,
            leaseToken,
            leaseExpiresAt,
            claimToken,
            claimExpiresAt,
            command.DispatchAttempts,
            targetTerminal);

    private static JsonElement RuntimeLimitsOf(string snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        return document.RootElement
            .GetProperty("agent")
            .GetProperty("runtime_limits")
            .Clone();
    }

    private static AgentRunEventResponse ToEventResponse(EventEntry e) => new(
        e.Sequence,
        e.EventId,
        e.EventType,
        e.NodeId,
        e.LeaseGeneration,
        e.EventCursor,
        e.SnapshotHash,
        e.Payload.Clone(),
        e.CreatedAt);

    private sealed class Entry
    {
        public Guid Id;
        public Guid RootRunId;
        public Guid? ParentRunId;
        public string? TaskId;
        public string RunKind = "direct-agent";
        public string TenantId = "";
        public string UserId = "";
        public string CallerRole = "";
        public Guid AgentId;
        public int AgentRevision;
        public Guid WorkflowId;
        public int WorkflowRevision;
        public string Snapshot = "{}";
        public byte[] SnapshotCanonical = "{}"u8.ToArray();
        public string SnapshotHash = "";
        public string Status = AgentRunStatuses.Queued;
        public long StateVersion;
        public long LeaseGeneration;
        public long CheckpointGeneration;
        public string? CheckpointRef;
        public long CheckpointVersion;
        public long EventAckCursor;
        public JsonElement? PendingInput;
        public JsonElement? Result;
        public string? ErrorCode;
        public string? ErrorMessage;
        public DateTime? CancelRequestedAt;
        public string? LeaseOwner;
        public string? LeaseTokenHash;
        public DateTime? LeaseExpiresAt;
        public Guid? LeaseCommandId;
        public long LatestEventSequence;
        public DateTime? StartedAt;
        public DateTime CreatedAt;
        public DateTime DeadlineAt;
        public DateTime UpdatedAt;
        public DateTime? CompletedAt;
        public List<SkillSnapshotSource> Skills { get; } = new();
        public List<EventEntry> Events { get; } = new();
    }

    private sealed record EventEntry(
        long Sequence,
        Guid EventId,
        string EventType,
        string? NodeId,
        long? LeaseGeneration,
        long? EventCursor,
        string SnapshotHash,
        JsonElement Payload,
        DateTime CreatedAt);

    private sealed class CommandEntry
    {
        public Guid Id;
        public long Sequence;
        public Guid RunId;
        public string Type = "";
        public string RequestHash = "";
        public JsonElement Input = EmptyPayload();
        public string InputHash = "";
        public string? DispatchClaimOwner;
        public string? DispatchClaimTokenHash;
        public DateTime? DispatchClaimExpiresAt;
        public int DispatchAttempts;
        public DateTime? DispatchCompletedAt;
        public string? ExecutionRecoveryLeaseTokenHash;
        public DateTime? LastDispatchAt;
        public DateTime CreatedAt;
    }
}
