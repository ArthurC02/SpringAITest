using System.Text.Json;
using Backend.Api.AgentRuns;
using Backend.Api.Common;
using Backend.Api.Orchestrators;
using Backend.Api.Workflows;
using Backend.Api.Contexts;

namespace Backend.Api.OrchestratorRuns;

/// <summary>Lite/test durable model parity.  D3 Agent runs remain a separate aggregate.</summary>
public sealed class InMemoryOrchestratorRunRepository(
    IOrchestratorRepository orchestrators, IWorkflowRepository workflows,
    Backend.Api.Agents.IAgentRepository agents,
    IAgentRunRepository? agentRuns = null, IContextRepository? contexts = null,
    ContextEnrichmentState? contextState = null) : IOrchestratorRunRepository
{
    // Several critical sections await other repositories, so a plain `lock` cannot be used.
    // SemaphoreSlim is not reentrant; helpers invoked while holding this gate must not acquire it again.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Entry> _runs = [];
    private readonly Dictionary<(string Tenant, string User, string Key), (string Hash, Guid Run)> _keys = [];
    private readonly Dictionary<(string Tenant, string User, Guid Run, string Key), OrchestratorRunResponse> _cancelKeys = [];
    private readonly Dictionary<(string Tenant, string User, Guid Run, string Key), (Guid CommandId, string InputHash)> _resumeKeys = [];

    public async Task<OrchestratorRunWriteResult> CreateAsync(string tenant, string user, string role,
        IReadOnlyCollection<string> groups, IReadOnlyCollection<string> capabilities, Guid orchestratorId,
        string conversation, string message, string key, CancellationToken ct)
    {
        var hash = Skills.SkillHash.Sha256($"{orchestratorId:D}\0{conversation}\0{message}");
        var source = await orchestrators.GetAsync(tenant, orchestratorId, ct);
        if (source is null) return new(OrchestratorRunWriteStatus.NotFound);
        if (!source.Enabled || source.PublishedRevision is not int revision)
            return new(OrchestratorRunWriteStatus.InvalidState, Message: "Orchestrator must be enabled and published");
        var definition = await orchestrators.RevisionAsync(tenant, orchestratorId, revision, ct);
        if (definition is null) return new(OrchestratorRunWriteStatus.InvalidState, Message: "Published Orchestrator revision is unavailable");
        using var doc = JsonDocument.Parse(definition);
        var reference = doc.RootElement.GetProperty("workflow");
        var workflowId = Guid.Parse(reference.GetProperty("id").GetString()!);
        var workflowRevision = reference.GetProperty("revision").GetInt32();
        var workflow = await workflows.GetRevisionAsync(tenant, workflowId, workflowRevision, ct);
        if (workflow is null) return new(OrchestratorRunWriteStatus.InvalidState, Message: "Pinned root Workflow revision is unavailable");
        var workerSources = new List<PublishedAgentSnapshotSource>();
        PublishedAgentSnapshotSource? verifierSource = null;
        foreach (var referenceItem in OrchestratorCanonicalizer.AgentRefs(definition))
        {
            var agent = await agents.GetAsync(tenant, referenceItem.AgentId, ct);
            var agentDefinition = await agents.GetRevisionDefinitionAsync(tenant, referenceItem.AgentId, referenceItem.Revision, ct);
            var revisionInfo = (await agents.ListRevisionsAsync(tenant, referenceItem.AgentId, ct))
                .SingleOrDefault(x => x.Revision == referenceItem.Revision);
            if (agent is null || agentDefinition is null || revisionInfo is null
                || !agent.Enabled || agent.PublishedRevision != referenceItem.Revision
                || revisionInfo.RuntimeWorkflowId is null || revisionInfo.RuntimeWorkflowRevision is null)
                return new(OrchestratorRunWriteStatus.InvalidState, Message: "Pinned Worker/Verifier revision is unavailable");
            var runtimeWorkflow = await workflows.GetRevisionAsync(
                tenant, revisionInfo.RuntimeWorkflowId.Value, revisionInfo.RuntimeWorkflowRevision.Value, ct);
            if (runtimeWorkflow is null)
                return new(OrchestratorRunWriteStatus.InvalidState, Message: "Pinned Worker/Verifier workflow revision is unavailable");
            var pinnedSource = new PublishedAgentSnapshotSource(referenceItem.AgentId, agent.Name,
                referenceItem.Revision, agentDefinition, revisionInfo.DefinitionSha256,
                revisionInfo.RuntimeWorkflowId.Value, revisionInfo.RuntimeWorkflowRevision.Value,
                revisionInfo.SkillBindings);
            if (referenceItem.Verifier) verifierSource = pinnedSource; else workerSources.Add(pinnedSource);
        }
        if (verifierSource is null || workerSources.Count == 0)
            return new(OrchestratorRunWriteStatus.InvalidState, Message: "Pinned Worker/Verifier revisions are unavailable");
        await _gate.WaitAsync(ct);
        try
        {
            if (_keys.TryGetValue((tenant, user, key), out var prior))
                return prior.Hash == hash ? new(OrchestratorRunWriteStatus.Replay, ToResponse(_runs[prior.Run]), Dispatch: new(_runs[prior.Run].CommandId), Replayed: true) : new(OrchestratorRunWriteStatus.Conflict, Message: "Idempotency-Key was used for a different request");
            if (_runs.Values.Any(x => x.Tenant == tenant && x.User == user && x.OrchestratorId == orchestratorId && x.Conversation == conversation && x.Status is "queued" or "running" or "waiting_input"))
                return new(OrchestratorRunWriteStatus.Conflict, Message: "An active root run already exists for this conversation and Orchestrator");
            var id = Guid.NewGuid();
            var rootWorkflow = new WorkflowSnapshotSource(workflowId, workflowRevision, 1,
                workflow.Value.Definition, WorkflowCanonicalizer.Hash(workflow.Value.Definition),
                WorkflowCompilerContracts.Current);
            var snapshot = AgentRunSnapshotBuilder.BuildOrchestratorRoot(id, tenant, user, role, groups, capabilities, orchestratorId, revision, definition, WorkflowCanonicalizer.Hash(definition), rootWorkflow, workerSources, verifierSource, message);
            var now = DateTime.UtcNow;
            var item = new Entry(id, Guid.NewGuid(), tenant, user, role, orchestratorId, revision, conversation, workflowId, workflowRevision, snapshot.StoredSnapshot, snapshot.SnapshotHash, now.AddSeconds(snapshot.EffectiveTimeoutSeconds), now);
            item.Events.Add(new(1, "run_created", snapshot.SnapshotHash, JsonDocument.Parse("{}").RootElement.Clone(), now));
            _runs.Add(id, item); _keys.Add((tenant, user, key), (hash, id));
            if (contexts is IContextAuthorityRegistry registry) registry.RegisterRoot(tenant, user, id, snapshot.StoredSnapshot);
            return new(OrchestratorRunWriteStatus.Success, ToResponse(item), Dispatch: new(item.CommandId));
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunResponse?> GetAsync(string tenant, string user, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _runs.TryGetValue(id, out var x) && x.Tenant == tenant && x.User == user ? ToResponse(x) : null; }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunActiveLookup> FindActiveAsync(string tenant, string user, string conversation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var values = _runs.Values.Where(x => x.Tenant == tenant && x.User == user && x.Conversation == conversation && x.Status is "queued" or "running" or "waiting_input").Take(2).ToArray();
            return values.Length switch { 1 => new OrchestratorRunActiveLookup(ToResponse(values[0])), > 1 => new OrchestratorRunActiveLookup(null, null, true), _ => new OrchestratorRunActiveLookup(null) };
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenant, string user, string key, OrchestratorRunReplayRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var operation = key.Split(':', 2)[0];
            if (operation is not ("chat" or "resume" or "switch")) return new OrchestratorRunActiveLookup(null, IsMismatch: true);
            var values = _runs.Values.Where(x => x.Tenant == tenant && x.User == user && ((_keys.ContainsKey((tenant, user, key)) && _keys[(tenant, user, key)].Run == x.Id) || _resumeKeys.ContainsKey((tenant, user, x.Id, key)))).Take(2).ToArray();
            if (values.Length != 1) return values.Length > 1 ? new OrchestratorRunActiveLookup(null, null, true) : new OrchestratorRunActiveLookup(null);
            var value = values[0];
            if (value.Conversation != request.ConversationId || request.OrchestratorId is Guid selected && value.OrchestratorId != selected) return new OrchestratorRunActiveLookup(null, IsMismatch: true);
            var messageHash = Skills.SkillHash.Sha256(request.Message!);
            if (operation == "chat" && _keys.TryGetValue((tenant, user, key), out var start) && start.Run == value.Id && start.Hash == Skills.SkillHash.Sha256($"{value.OrchestratorId:D}\0{value.Conversation}\0{request.Message}")) return new OrchestratorRunActiveLookup(ToResponse(value), value.CommandId);
            if (operation == "resume" && _resumeKeys.TryGetValue((tenant, user, value.Id, key), out var resume) && resume.InputHash == messageHash) return new OrchestratorRunActiveLookup(ToResponse(value), resume.CommandId);
            return new OrchestratorRunActiveLookup(null, IsMismatch: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunEventsResponse?> EventsAsync(string tenant, string user, Guid id, long after, int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(id, out var x) || x.Tenant != tenant || x.User != user) return null;
            var events = x.Events.Where(e => e.Sequence > after).Take(limit).Select(e => new OrchestratorRunEventResponse(e.Sequence, e.Type, e.Hash, e.Payload.Clone(), e.At)).ToArray();
            return new(id, events, events.Length == 0 ? after : events[^1].Sequence);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ExecutionArtifactAsync(string tenant, string user, Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _runs.TryGetValue(id, out var x) && x.Tenant == tenant && x.User == user
                ? JsonSerializer.Serialize(new { snapshot_hash = x.Hash, snapshot_canonical_base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Snapshot)) })
                : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunWriteResult> ResumeAsync(string tenant, string user, Guid id, string input, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(id, out var x) || x.Tenant != tenant || x.User != user) return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.NotFound);
            var inputHash = Skills.SkillHash.Sha256(input);
            if (_resumeKeys.TryGetValue((tenant, user, id, key), out var replay))
                return string.Equals(replay.InputHash, inputHash, StringComparison.Ordinal)
                    ? new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Replay, ToResponse(x), Dispatch: new(replay.CommandId), Replayed: true)
                    : new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Conflict, Message: "Idempotency-Key was already used with different resume input");
            if (x.Status != "waiting_input" || string.IsNullOrWhiteSpace(x.CheckpointRef) || x.CheckpointVersion < 1)
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, Message: "Root run is not waiting for input");
            x.Status = "queued"; x.Version++; x.Updated = DateTime.UtcNow; x.ResumeInput = input; x.CommandId = Guid.NewGuid(); x.CommandCompleted = false;
            _resumeKeys[(tenant, user, id, key)] = (x.CommandId, inputHash);
            x.Events.Add(new(x.Events.Count + 1, "root_resumed", x.Hash, JsonDocument.Parse(JsonSerializer.Serialize(new { input })).RootElement.Clone(), x.Updated));
            return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Success, ToResponse(x), Dispatch: new(x.CommandId));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Claims while <see cref="_gate"/> is held, so recovery does not re-enter its non-reentrant semaphore.</summary>
    private OrchestratorRunCommandClaim? ClaimCommandLocked(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds)
    {
        if (!_runs.TryGetValue(runId, out var x) || x.Tenant != tenant || x.User != user || x.CommandId != commandId || x.CommandCompleted || x.ClaimExpiresAt > DateTime.UtcNow || string.IsNullOrWhiteSpace(workerId) || leaseSeconds is < 1 or > 300) return null;
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        x.ClaimTokenHash = Skills.SkillHash.Sha256(token);
        x.ClaimExpiresAt = DateTime.UtcNow.AddSeconds(leaseSeconds);
        x.LeaseGeneration++;
        x.Status = x.Status == "queued" ? "running" : x.Status;
        x.Version++;
        x.Updated = DateTime.UtcNow;
        var resume = x.ResumeInput is not null;
        return new(commandId, runId, resume ? "resume" : "start", token, x.ClaimExpiresAt, x.LeaseGeneration, x.Hash, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Snapshot)), resume ? x.ResumeInput : null, resume ? x.CheckpointRef : null, resume ? x.CheckpointVersion : null);
    }

    public async Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ClaimCommandLocked(tenant, user, runId, commandId, workerId, leaseSeconds); }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string tenant, string user, Guid runId, Guid commandId, string claimToken, long leaseGeneration, int leaseSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(claimToken) || !_runs.TryGetValue(runId, out var x) || x.Tenant != tenant || x.User != user || x.CommandId != commandId || x.CommandCompleted || leaseSeconds is < 1 or > 300 || x.LeaseGeneration != leaseGeneration || x.ClaimExpiresAt <= DateTime.UtcNow || !string.Equals(x.ClaimTokenHash, Skills.SkillHash.Sha256(claimToken), StringComparison.Ordinal)) return null;
            x.ClaimExpiresAt = DateTime.UtcNow.AddSeconds(leaseSeconds);
            var resume = x.ResumeInput is not null;
            return new(commandId, runId, resume ? "resume" : "start", claimToken, x.ClaimExpiresAt, leaseGeneration, x.Hash, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Snapshot)), resume ? x.ResumeInput : null, resume ? x.CheckpointRef : null, resume ? x.CheckpointVersion : null);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string tenant, string user, Guid runId, Guid commandId, string claimToken, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(runId, out var x) || x.Tenant != tenant || x.User != user || x.CommandId != commandId) return OrchestratorRunDispatchCompleteStatus.NotFound;
            if (x.CommandCompleted || x.ClaimExpiresAt <= DateTime.UtcNow || !string.Equals(x.ClaimTokenHash, Skills.SkillHash.Sha256(claimToken ?? ""), StringComparison.Ordinal)) return OrchestratorRunDispatchCompleteStatus.Conflict;
            x.DispatchCompleted = true;
            return OrchestratorRunDispatchCompleteStatus.Success;
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string workerId, int limit, int leaseSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(workerId) || limit is < 1 or > 100 || leaseSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(workerId));
            var candidates = _runs.Values.Where(x => !x.CommandCompleted && x.ClaimExpiresAt <= DateTime.UtcNow && (x.Status is "queued" or "running")).OrderBy(x => x.Created).Take(limit + 1).ToArray();
            var items = new List<OrchestratorRunRecoveryItem>();
            // ClaimCommandLocked, not ClaimCommandAsync: _gate is already held here.
            foreach (var x in candidates.Take(limit))
            {
                var claim = ClaimCommandLocked(x.Tenant, x.User, x.Id, x.CommandId, workerId, leaseSeconds);
                if (claim is not null) items.Add(new(x.Tenant, x.User, "ADMIN", claim));
            }
            return new OrchestratorRunRecoveryResponse(items, candidates.Length > limit);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorChildResponse?> CreateChildAsync(string tenant, string user, Guid rootRunId, OrchestratorChildCreateRequest request, CancellationToken ct)
    {
        if (contextState?.Enabled is true && contexts is not null)
        {
            var stored = await contexts.GetLatestReadyForRunAsync(tenant, user, rootRunId, ct);
            request = request with { TaskEnvelope = ContextTaskEnvelopeProjection.ApplyIfAvailable(request.TaskEnvelope, stored, request.RunKind?.Trim() ?? "") };
        }
        // Same fail-closed boundary as the PostgreSQL authority: a malformed child throws so the
        // controller can answer 400, instead of a null that becomes an eternally retryable 409.
        var (taskId, runKind, envelope) = OrchestratorTaskEnvelope.ValidateChild(request);
        using var canonicalDocument = JsonDocument.Parse(envelope.Canonical);
        var canonicalEnvelope = canonicalDocument.RootElement.Clone();
        var source = agentRuns is null ? null : await ResolveChildSourceAsync(tenant, request.AgentId, request.AgentRevision, ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user || root.Status is not ("queued" or "running")) return null;
            using var doc = JsonDocument.Parse(root.Snapshot); var pin = OrchestratorRunSnapshotProjection.FindPin(doc.RootElement, runKind, request.AgentId, request.AgentRevision);
            if (pin is null || request.TokenCap != pin.Value.TokenCap || (source is not null && (!string.Equals(source.Value.Agent.DefinitionSha256, pin.Value.Hash, StringComparison.Ordinal) || source.Value.Workflow.WorkflowId != pin.Value.WorkflowId || source.Value.Workflow.Revision != pin.Value.WorkflowRevision))) return null;
            var limits = doc.RootElement.GetProperty("limits");
            if (root.Children.Count >= limits.GetProperty("max_child_runs").GetInt32() || root.Children.Count(x => x.Status is "queued" or "running") >= limits.GetProperty("max_concurrency").GetInt32() || root.Children.Any(x => x.Task == taskId && x.Attempt == request.Attempt)) return null;
            var agentRunId = Guid.NewGuid(); var commandId = Guid.NewGuid();
            if (agentRuns is not null)
            {
                if (agentRuns is not IOrchestratorChildRunRepository childRuns || source is null) return null;
                var caller = doc.RootElement.GetProperty("caller");
                var caps = Strings(caller.GetProperty("tool_grants")).Select(x => "tool.use:" + x).Concat(Strings(caller.GetProperty("knowledge_grants")).Select(x => "knowledge.read:" + x)).ToArray();
                var started = await childRuns.CreateOrchestratorChildAsync(tenant, user, root.Role, Strings(caller.GetProperty("groups")), caps, source.Value.Agent, source.Value.Workflow, new OrchestratorChildSnapshotProvenance(rootRunId, taskId, request.Attempt), runKind, pin.Value.TokenCap, canonicalEnvelope, $"orchestrator-child:{rootRunId:D}:{taskId}:{request.Attempt}:{runKind}", ct);
                if (started.Status is not (AgentRunWriteStatus.Success or AgentRunWriteStatus.Replay) || started.Run is null || started.Dispatch is null) return null;
                agentRunId = started.Run.Id; commandId = started.Dispatch.CommandId;
            }
            var commandInput = JsonSerializer.SerializeToElement(new { message = envelope.Objective, task_envelope = canonicalEnvelope });
            var artifact = JsonSerializer.SerializeToElement(new { orchestrator_root_run_id = rootRunId, task_id = taskId, attempt = request.Attempt, run_kind = runKind, root_snapshot_hash = root.Hash, agent_snapshot_hash = pin.Value.Hash, agent_run_id = agentRunId, command_id = commandId, command_input = commandInput });
            var child = new Child(Guid.NewGuid(), taskId, request.Attempt, runKind, request.AgentId, request.AgentRevision, pin.Value.WorkflowId, pin.Value.WorkflowRevision, pin.Value.Hash, agentRunId, commandId, canonicalEnvelope, artifact);
            root.Children.Add(child);
            root.Events.Add(new(root.Events.Count + 1, "child_created", root.Hash, JsonDocument.Parse(OrchestratorRunEvents.ChildCreated(child.Id, agentRunId, child.Task, child.Attempt, child.Kind)).RootElement.Clone(), DateTime.UtcNow));
            return new(child.Id, rootRunId, child.Task, child.Attempt, child.Kind, child.AgentId, child.AgentRevision, child.WorkflowId, child.WorkflowRevision, child.Hash, child.Status, child.AgentRunId, child.CommandId);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorContextRequestResponse?> GetOrCreateContextRequestAsync(string tenant, string user, Guid rootRunId, Guid childId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (contextState?.Enabled is not true || !_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user) return null;
            var child = root.Children.SingleOrDefault(x => x.Id == childId);
            if (child is null) return null;
            var created = child.ContextRequest is null;
            child.ContextRequest ??= TaskContextRequest.Create(child);
            var request = child.ContextRequest;
            if (created && contexts is IContextTaskLocalRegistry taskContexts) taskContexts.RegisterTaskContext(tenant, rootRunId, request.ContextId);
            if (created) root.Events.Add(new(root.Events.Count + 1, "context.requested", root.Hash, JsonSerializer.SerializeToElement(new { context_request_id = request.Id, child_id = child.Id, task_id = child.Task, role = request.Role }), DateTime.UtcNow));
            return request.ToResponse(rootRunId, child);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorContextRequestResponse?> GetContextRequestAsync(string tenant, string user, Guid rootRunId, Guid childId, Guid requestId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (contextState?.Enabled is not true || !_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user) return null;
            var child = root.Children.SingleOrDefault(x => x.Id == childId);
            return child?.ContextRequest is { } request && request.Id == requestId ? request.ToResponse(rootRunId, child) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorContextDeltaResult> AppendContextDeltaAsync(string tenant, string user, Guid rootRunId, Guid childId, Guid requestId, long expectedVersion, OrchestratorContextDeltaRequest delta, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (contextState?.Enabled is not true || !_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user || root.Children.SingleOrDefault(x => x.Id == childId) is not { } child || child.ContextRequest is not { } request || request.Id != requestId)
                return new OrchestratorContextDeltaResult(OrchestratorContextDeltaStatus.NotFound);
            if (request.Version != expectedVersion) return new OrchestratorContextDeltaResult(OrchestratorContextDeltaStatus.Conflict);
            var views = delta.Views ?? Array.Empty<ContextViewInput>();
            if (views.Count != 1 || views[0].ViewType != request.Role) throw new ArgumentException("Context delta must contain exactly the task role view");
            var stored = contexts is null
                ? throw new ArgumentException("Context store is unavailable")
                : await contexts.CreateRevisionAsync(tenant, user, request.ContextId,
                    new ContextRevisionSubmitRequest(rootRunId, delta.Definition, delta.Evidence, views, delta.Measurements, delta.AsOf, delta.ExpiresAt), ct);
            request.Current = stored.Revision.ContextRef;
            request.Version++;
            request.Updated = DateTime.UtcNow;
            root.Events.Add(new(root.Events.Count + 1, "context.delta_applied", root.Hash, JsonSerializer.SerializeToElement(new { context_request_id = request.Id, child_id = child.Id, task_id = child.Task, revision = stored.Revision.Revision, status = stored.Revision.Status, readiness = stored.Revision.Readiness }), request.Updated));
            return new OrchestratorContextDeltaResult(OrchestratorContextDeltaStatus.Success, stored.Revision, request.Version);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorChildStatusResponse?> GetChildAsync(string tenant, string user, Guid rootRunId, Guid childId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user) return null;
            var child = root.Children.SingleOrDefault(x => x.Id == childId);
            if (child is null) return null;
            AgentRunResponse? run = agentRuns is null ? null : await agentRuns.GetAsync(tenant, user, child.AgentRunId, ct);
            if (run is not null)
            {
                var prior = child.Status;
                child.Status = run.Status;
                if (prior != child.Status && child.Status is "completed" or "failed" or "cancelled")
                    root.Events.Add(new(root.Events.Count + 1, "child_terminal", root.Hash, JsonDocument.Parse(OrchestratorRunEvents.ChildTerminal(child.Id, child.AgentRunId, child.Task, child.Attempt, child.Kind, child.AgentId, child.AgentRevision, child.Hash, child.Status, run.Result, run.ErrorCode)).RootElement.Clone(), DateTime.UtcNow));
            }
            var output = run?.Result ?? JsonDocument.Parse("{}").RootElement.Clone();
            var citations = output.ValueKind == JsonValueKind.Object && output.TryGetProperty("citations", out var cits) && cits.ValueKind == JsonValueKind.Array ? cits.Clone() : JsonDocument.Parse("[]").RootElement.Clone();
            return new(child.Id, rootRunId, child.Task, child.Attempt, child.Kind, child.AgentId, child.AgentRevision, child.WorkflowId, child.WorkflowRevision, child.Hash, child.AgentRunId, child.Status, output, citations, run?.ErrorCode, run?.ErrorMessage);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunWriteResult> TransitionAsync(string tenant, string user, Guid rootRunId, OrchestratorRootTransitionRequest request, CancellationToken ct)
    {
        if (!OrchestratorRootTransitionPolicy.IsValid(request))
            return new OrchestratorRunWriteResult(
                OrchestratorRunWriteStatus.InvalidState,
                Message: "Invalid root transition");

        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user)
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.NotFound);
            if (root.Status is "completed" or "failed" or "cancelled"
                || root.Version != request.ExpectedStateVersion
                || root.LeaseGeneration != request.LeaseGeneration
                || !string.Equals(root.ClaimTokenHash, Skills.SkillHash.Sha256(request.ClaimToken!), StringComparison.Ordinal)
                || root.ClaimExpiresAt <= DateTime.UtcNow)
                return new OrchestratorRunWriteResult(
                    OrchestratorRunWriteStatus.Conflict,
                    ToResponse(root),
                    "Root command lease changed");

            root.Status = request.ToStatus!;
            if (root.Status == "waiting_input")
            {
                root.CheckpointRef = request.CheckpointRef;
                root.CheckpointVersion = request.CheckpointVersion!.Value;
            }
            root.CommandCompleted = true;
            root.ClaimTokenHash = null;
            root.ClaimExpiresAt = DateTime.MinValue;
            root.Version++;
            root.Updated = DateTime.UtcNow;
            foreach (var eventItem in request.Events ?? Array.Empty<OrchestratorRootEventAppend>())
                root.Events.Add(new(root.Events.Count + 1, eventItem.EventType!, root.Hash,
                    eventItem.Payload?.Clone() ?? JsonDocument.Parse("{}").RootElement.Clone(), root.Updated));
            root.Events.Add(new(root.Events.Count + 1,
                root.Status == "waiting_input" ? "root_waiting_input" : "root_terminal",
                root.Hash,
                JsonDocument.Parse(OrchestratorRunEvents.RootTerminal(root.Status)).RootElement.Clone(),
                root.Updated));
            return new OrchestratorRunWriteResult(
                OrchestratorRunWriteStatus.Success,
                ToResponse(root));
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string tenant, string user, Guid rootRunId, OrchestratorContextAcquireRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user || request.ContextRound < 1
                || request.CurrentContext is not { ValueKind: JsonValueKind.Object } || JsonUtf8.ByteCount(request.CurrentContext.Value) > 65_536)
                return null;
            using var snapshot = JsonDocument.Parse(root.Snapshot); var authority = snapshot.RootElement.GetProperty("authority");
            var tools = authority.GetProperty("context_tools").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal);
            var knowledge = authority.GetProperty("knowledge_sources").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal);
            if (!tools.SequenceEqual((request.AllowedTools ?? []).OrderBy(x => x, StringComparer.Ordinal)) || !knowledge.SequenceEqual((request.AllowedKnowledgeSources ?? []).OrderBy(x => x, StringComparer.Ordinal)))
                return null;
            var missing = tools.Select(x => "context-tool:" + x).Concat(knowledge.Select(x => "knowledge-source:" + x)).DefaultIfEmpty("context-adapter-unavailable").ToArray();
            var clarified = request.CurrentContext.Value.TryGetProperty("user_input", out var input) && input.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(input.GetString());
            // Flag off is the pre-E1 contract verbatim, clarification short circuit included.
            if (contextState?.Enabled is not true)
                return clarified
                    ? new(true, request.CurrentContext.Value.Clone(), [], [])
                    : new(false, JsonDocument.Parse("{}").RootElement.Clone(), [], missing);
            if (clarified) return new(false, JsonDocument.Parse("{}").RootElement.Clone(), [], ["clarification-revision-required"]);
            var stored = contexts is null ? null : await contexts.GetLatestReadyForRunAsync(tenant, user, rootRunId, ct);
            if (stored is not null && ContextAcquireProjection.Build(stored) is { } projection) return projection;
            return new(false, JsonDocument.Parse("{}").RootElement.Clone(), [], missing);
        }
        finally { _gate.Release(); }
    }

    public async Task<OrchestratorRunWriteResult> CancelAsync(string tenant, string user, Guid id, string? reason, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_runs.TryGetValue(id, out var x) || x.Tenant != tenant || x.User != user)
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.NotFound);

            // Expiry never creates a cancel idempotency record.  Do not let a late request
            // turn a deadline terminal state into a replay or append a cancellation event.
            if (x.Status == "timed_out")
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal");

            if (_cancelKeys.TryGetValue((tenant, user, id, key), out var replay))
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Replay, replay, Replayed: true);

            if (x.Status is "completed" or "failed" or "cancelled" or "timed_out")
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal");

            // All-or-nothing, matching the Dapper authority's single cancel transaction: the child
            // cascade runs first and the root becomes terminal only when every child cancel
            // succeeded. A partial failure commits nothing (no terminal status, no run_cancelled
            // event, no cancel idempotency record) so the terminal/replay guards above cannot
            // permanently block a retry of the still-uncancelled children, and the failure is
            // surfaced the same way Dapper surfaces a rolled back transaction: as an exception to
            // the caller, plus a non-terminal event naming the children that failed.
            var failures = await CascadeChildCancelLocked(x, reason, "orchestrator-cancel", ct);
            if (failures is { Count: > 0 })
            {
                AppendCascadeIncomplete(x, "root_cancel_cascade_incomplete", failures);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0].Error).Throw();
            }

            x.Status = "cancelled";
            x.CancelRequested = true;
            x.Version++;
            x.Updated = DateTime.UtcNow;
            x.Events.Add(new(x.Events.Count + 1, "run_cancelled", x.Hash, JsonDocument.Parse(JsonSerializer.Serialize(new { reason })).RootElement.Clone(), x.Updated));
            var response = ToResponse(x);
            _cancelKeys[(tenant, user, id, key)] = response;
            return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Success, response);
        }
        finally { _gate.Release(); }
    }
    private static OrchestratorRunResponse ToResponse(Entry x) { using var d = JsonDocument.Parse(x.Snapshot); return new(x.Id, x.OrchestratorId, x.OrchestratorRevision, x.Conversation, x.WorkflowId, x.WorkflowRevision, x.Hash, x.Status, x.CancelRequested, x.Version, x.Deadline, OrchestratorRunSnapshotProjection.Budgets(d.RootElement), x.Created, x.Updated, ErrorCode: x.ErrorCode, ErrorMessage: x.ErrorMessage); }
    private async Task<(PublishedAgentSnapshotSource Agent, WorkflowSnapshotSource Workflow)?> ResolveChildSourceAsync(string tenant, Guid agentId, int revision, CancellationToken ct)
    { var agent = await agents.GetAsync(tenant, agentId, ct); var definition = await agents.GetRevisionDefinitionAsync(tenant, agentId, revision, ct); var info = (await agents.ListRevisionsAsync(tenant, agentId, ct)).SingleOrDefault(x => x.Revision == revision); if (agent is null || definition is null || info is null || !agent.Enabled || agent.PublishedRevision != revision || info.RuntimeWorkflowId is not Guid workflowId || info.RuntimeWorkflowRevision is not int workflowRevision) return null; var workflow = await workflows.GetRevisionAsync(tenant, workflowId, workflowRevision, ct); if (workflow is null) return null; return (new(agentId, agent.Name, revision, definition, info.DefinitionSha256, workflowId, workflowRevision, info.SkillBindings, info.PromptManifestRevision, info.PromptManifestSha256), new(workflowId, workflowRevision, 1, workflow.Value.Definition, WorkflowCanonicalizer.Hash(workflow.Value.Definition), WorkflowCompilerContracts.Current)); }
    private static IReadOnlyCollection<string> Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>();

    async Task<OrchestratorRunWriteResult> IOrchestratorRunRepository.ResumeAsync(string tenant, string user, Guid id, string input, string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_runs.TryGetValue(id, out var run) && run.Tenant == tenant && run.User == user && run.Deadline <= DateTime.UtcNow)
            {
                await ExpireLockedAsync(run, ct);
                return new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, ToResponse(run), "Root run deadline has expired");
            }
        }
        finally { _gate.Release(); }
        return await ResumeAsync(tenant, user, id, input, key, ct);
    }

    async Task<OrchestratorRunCommandClaim?> IOrchestratorRunRepository.ClaimCommandAsync(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct)
    {
        Entry? run;
        await _gate.WaitAsync(ct);
        try
        {
            _runs.TryGetValue(runId, out run);
            if (run is not null && run.Tenant == tenant && run.User == user && run.Deadline <= DateTime.UtcNow)
            {
                await ExpireLockedAsync(run, ct);
                return null;
            }
        }
        finally { _gate.Release(); }
        var claim = await ClaimCommandAsync(tenant, user, runId, commandId, workerId, leaseSeconds, ct);
        return claim is null || run is null ? claim : claim with { DeadlineAt = run.Deadline };
    }

    async Task<OrchestratorRunRecoveryResponse> IOrchestratorRunRepository.ClaimRecoveryAsync(string workerId, int limit, int leaseSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var run in _runs.Values.Where(x => !x.CommandCompleted && x.Deadline <= DateTime.UtcNow && (x.Status is "queued" or "running" or "waiting_input")))
            {
                await ExpireLockedAsync(run, ct);
            }
        }
        finally { _gate.Release(); }
        return await ClaimRecoveryAsync(workerId, limit, leaseSeconds, ct);
    }

    // Called with _gate held. Cascades every cancellable child before committing root timeout state.
    // All-or-nothing, deliberately matching the Dapper authority's single transaction
    // (OrchestratorRunRepository.ExpireDeadlineAsync): the root is marked "timed_out" (with its
    // root_timed_out event) only when every child cancel succeeds. If any child fails, the root is
    // left exactly as it was -- not terminal -- so the early-return guard above does NOT skip it on
    // a future scrub, and that same scrub will retry the still-uncancelled children. A partial
    // failure is not silent: it is recorded as a non-terminal "root_timeout_cascade_incomplete"
    // event carrying the failed child ids and error messages, since lite mode has no ILogger here
    // and the Dapper side would only ever surface this as a rolled-back transaction in server logs.
    // Accepted consequence (same one the Dapper transaction already has): a child that can never be
    // cancelled makes this root retry forever without becoming terminal.
    //
    // Do not call another gate-acquiring member here: SemaphoreSlim is not reentrant.
    private async Task ExpireLockedAsync(Entry run, CancellationToken ct)
    {
        if (run.Status is "completed" or "failed" or "cancelled" or "timed_out") return;
        var failures = await CascadeChildCancelLocked(run, "deadline", "orchestrator-deadline", ct);
        if (failures is { Count: > 0 })
        {
            AppendCascadeIncomplete(run, "root_timeout_cascade_incomplete", failures);
            return;
        }
        run.Status = "timed_out";
        run.ErrorCode = "deadline_exceeded";
        run.ErrorMessage = "Root run deadline expired";
        run.CommandCompleted = true;
        run.ClaimTokenHash = null;
        run.ClaimExpiresAt = DateTime.MinValue;
        run.Version++;
        run.Updated = DateTime.UtcNow;
        run.Events.Add(new(run.Events.Count + 1, "root_timed_out", run.Hash, JsonDocument.Parse("{}").RootElement.Clone(), run.Updated));
    }

    // Called with _gate held, by both the cancel and the deadline path: their cascade semantics are
    // one contract, so they share one implementation instead of drifting apart again.
    // Dapper cascades the agent_run cancel with a three-state WHERE (queued/running/waiting_input)
    // -- Child.Status only mirrors the real D3 status when GetChildAsync last synced it, so a child
    // parked in waiting_input must still be included or its underlying agent run is never told to
    // cancel. Caller cancellation is not a child failure: it propagates untouched so no durable
    // record (neither a terminal transition nor a cascade-incomplete event) is written for it.
    // Do not call another gate-acquiring member here: SemaphoreSlim is not reentrant.
    private async Task<List<(Guid ChildId, Exception Error)>?> CascadeChildCancelLocked(
        Entry run, string? reason, string keyPrefix, CancellationToken ct)
    {
        List<(Guid ChildId, Exception Error)>? failures = null;
        foreach (var child in run.Children.Where(x => x.Status is "queued" or "running" or "waiting_input"))
        {
            try
            {
                if (agentRuns is not null)
                    await agentRuns.CancelAsync(run.Tenant, run.User, child.AgentRunId, reason, $"{keyPrefix}:{run.Id:D}:{child.AgentRunId:D}", ct);
                child.Status = "cancelled";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add((child.Id, ex));
            }
        }
        return failures;
    }

    private static void AppendCascadeIncomplete(Entry run, string eventType, List<(Guid ChildId, Exception Error)> failures)
        => run.Events.Add(new(run.Events.Count + 1, eventType, run.Hash,
            JsonSerializer.SerializeToElement(new { failed_children = failures.Select(f => new { child_id = f.ChildId, error = f.Error.Message }) }),
            DateTime.UtcNow));
    private sealed class Entry(Guid id, Guid commandId, string tenant, string user, string role, Guid oid, int orev, string conversation, Guid wid, int wrev, string snapshot, string hash, DateTime deadline, DateTime now) { public Guid Id = id, CommandId = commandId; public string Tenant = tenant, User = user, Role = role, Conversation = conversation, Snapshot = snapshot, Hash = hash, Status = "queued"; public Guid OrchestratorId = oid, WorkflowId = wid; public int OrchestratorRevision = orev, WorkflowRevision = wrev; public long Version = 1, LeaseGeneration, CheckpointVersion; public bool CancelRequested, CommandCompleted, DispatchCompleted; public string? ClaimTokenHash, ResumeInput, CheckpointRef, ErrorCode, ErrorMessage; public DateTime ClaimExpiresAt = DateTime.MinValue; public DateTime Deadline = deadline, Created = now, Updated = now; public List<E> Events = []; public List<Child> Children = []; }
    private sealed class Child(Guid id, string task, int attempt, string kind, Guid agentId, int agentRevision, Guid workflowId, int workflowRevision, string hash, Guid agentRunId, Guid commandId, JsonElement taskEnvelope, JsonElement dispatchArtifact) { public Guid Id = id, AgentId = agentId, WorkflowId = workflowId, AgentRunId = agentRunId, CommandId = commandId; public string Task = task, Kind = kind, Hash = hash, Status = "queued"; public int Attempt = attempt, AgentRevision = agentRevision, WorkflowRevision = workflowRevision; public JsonElement TaskEnvelope = taskEnvelope.Clone(), DispatchArtifact = dispatchArtifact.Clone(); public TaskContextRequest? ContextRequest; }
    private sealed class TaskContextRequest(Guid id, Guid contextId, string role, ContextRef? baseContext, DateTime now)
    {
        public Guid Id = id, ContextId = contextId;
        public string Role = role;
        public ContextRef? Base = baseContext, Current;
        public long Version = 1;
        public DateTime Created = now, Updated = now;
        public static TaskContextRequest Create(Child child)
        {
            ContextRef? baseContext = null;
            if (child.TaskEnvelope.TryGetProperty("context_ref", out var raw) && raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("context_id", out var contextId) && Guid.TryParse(contextId.GetString(), out var id)
                && raw.TryGetProperty("revision", out var revision) && revision.TryGetInt32(out var number)
                && raw.TryGetProperty("view_id", out var viewId) && Guid.TryParse(viewId.GetString(), out var view)) baseContext = new(id, number, view);
            return new(Guid.NewGuid(), Guid.NewGuid(), child.Kind == "verifier" ? "verifier" : "worker", baseContext, DateTime.UtcNow);
        }
        public OrchestratorContextRequestResponse ToResponse(Guid rootRunId, Child child)
            => new(Id, rootRunId, child.Id, child.Task, Role, ContextId, Base, Current, Version, Created, Updated);
    }
    private sealed record E(long Sequence, string Type, string Hash, JsonElement Payload, DateTime At);
}
