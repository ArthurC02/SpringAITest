using System.Text.Json;
using Backend.Api.AgentRuns;
using Backend.Api.Orchestrators;
using Backend.Api.Workflows;

namespace Backend.Api.OrchestratorRuns;

/// <summary>Lite/test durable model parity.  D3 Agent runs remain a separate aggregate.</summary>
public sealed class InMemoryOrchestratorRunRepository(
    IOrchestratorRepository orchestrators, IWorkflowRepository workflows,
    Backend.Api.Agents.IAgentRepository agents,
    IAgentRunRepository? agentRuns = null) : IOrchestratorRunRepository
{
    private readonly object _gate = new();
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
        lock (_gate)
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
            return new(OrchestratorRunWriteStatus.Success, ToResponse(item), Dispatch: new(item.CommandId));
        }
    }

    public Task<OrchestratorRunResponse?> GetAsync(string tenant, string user, Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(_runs.TryGetValue(id, out var x) && x.Tenant == tenant && x.User == user ? ToResponse(x) : null); }
    public Task<OrchestratorRunActiveLookup> FindActiveAsync(string tenant, string user, string conversation, CancellationToken ct) { lock (_gate) { var values = _runs.Values.Where(x => x.Tenant == tenant && x.User == user && x.Conversation == conversation && x.Status is "queued" or "running" or "waiting_input").Take(2).ToArray(); return Task.FromResult(values.Length switch { 1 => new OrchestratorRunActiveLookup(ToResponse(values[0])), > 1 => new OrchestratorRunActiveLookup(null, null, true), _ => new OrchestratorRunActiveLookup(null) }); } }
    public Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenant, string user, string key, OrchestratorRunReplayRequest request, CancellationToken ct) { lock (_gate) { var operation = key.Split(':', 2)[0]; if (operation is not ("chat" or "resume" or "switch")) return Task.FromResult(new OrchestratorRunActiveLookup(null, IsMismatch: true)); var values = _runs.Values.Where(x => x.Tenant == tenant && x.User == user && ((_keys.ContainsKey((tenant, user, key)) && _keys[(tenant, user, key)].Run == x.Id) || _resumeKeys.ContainsKey((tenant, user, x.Id, key)))).Take(2).ToArray(); if (values.Length != 1) return Task.FromResult(values.Length > 1 ? new OrchestratorRunActiveLookup(null, null, true) : new OrchestratorRunActiveLookup(null)); var value = values[0]; if (value.Conversation != request.ConversationId || request.OrchestratorId is Guid selected && value.OrchestratorId != selected) return Task.FromResult(new OrchestratorRunActiveLookup(null, IsMismatch: true)); var messageHash = Skills.SkillHash.Sha256(request.Message!); if (operation == "chat" && _keys.TryGetValue((tenant, user, key), out var start) && start.Run == value.Id && start.Hash == Skills.SkillHash.Sha256($"{value.OrchestratorId:D}\0{value.Conversation}\0{request.Message}")) return Task.FromResult(new OrchestratorRunActiveLookup(ToResponse(value), value.CommandId)); if (operation == "resume" && _resumeKeys.TryGetValue((tenant, user, value.Id, key), out var resume) && resume.InputHash == messageHash) return Task.FromResult(new OrchestratorRunActiveLookup(ToResponse(value), resume.CommandId)); return Task.FromResult(new OrchestratorRunActiveLookup(null, IsMismatch: true)); } }
    public Task<OrchestratorRunEventsResponse?> EventsAsync(string tenant, string user, Guid id, long after, int limit, CancellationToken ct) { lock (_gate) { if (!_runs.TryGetValue(id, out var x) || x.Tenant != tenant || x.User != user) return Task.FromResult<OrchestratorRunEventsResponse?>(null); var events = x.Events.Where(e => e.Sequence > after).Take(limit).Select(e => new OrchestratorRunEventResponse(e.Sequence, e.Type, e.Hash, e.Payload.Clone(), e.At)).ToArray(); return Task.FromResult<OrchestratorRunEventsResponse?>(new(id, events, events.Length == 0 ? after : events[^1].Sequence)); } }
    public Task<string?> ExecutionArtifactAsync(string tenant, string user, Guid id, CancellationToken ct) { lock (_gate) return Task.FromResult(_runs.TryGetValue(id, out var x) && x.Tenant == tenant && x.User == user ? JsonSerializer.Serialize(new { snapshot_hash = x.Hash, snapshot_canonical_base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Snapshot)) }) : null); }
    public Task<OrchestratorRunWriteResult> ResumeAsync(string tenant, string user, Guid id, string input, string key, CancellationToken ct) { lock (_gate) { if (!_runs.TryGetValue(id, out var x) || x.Tenant != tenant || x.User != user) return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.NotFound)); var inputHash = Skills.SkillHash.Sha256(input); if (_resumeKeys.TryGetValue((tenant, user, id, key), out var replay)) return Task.FromResult(string.Equals(replay.InputHash, inputHash, StringComparison.Ordinal) ? new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Replay, ToResponse(x), Dispatch: new(replay.CommandId), Replayed: true) : new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Conflict, Message: "Idempotency-Key was already used with different resume input")); if (x.Status != "waiting_input" || string.IsNullOrWhiteSpace(x.CheckpointRef) || x.CheckpointVersion < 1) return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, Message: "Root run is not waiting for input")); x.Status = "queued"; x.Version++; x.Updated = DateTime.UtcNow; x.ResumeInput = input; x.CommandId = Guid.NewGuid(); x.CommandCompleted = false; _resumeKeys[(tenant, user, id, key)] = (x.CommandId, inputHash); x.Events.Add(new(x.Events.Count + 1, "root_resumed", x.Hash, JsonDocument.Parse(JsonSerializer.Serialize(new { input })).RootElement.Clone(), x.Updated)); return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Success, ToResponse(x), Dispatch: new(x.CommandId))); } }
    public Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct)
    { lock (_gate) { if (!_runs.TryGetValue(runId, out var x) || x.Tenant != tenant || x.User != user || x.CommandId != commandId || x.CommandCompleted || x.ClaimExpiresAt > DateTime.UtcNow || string.IsNullOrWhiteSpace(workerId) || leaseSeconds is < 1 or > 300) return Task.FromResult<OrchestratorRunCommandClaim?>(null); var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); x.ClaimTokenHash = Skills.SkillHash.Sha256(token); x.ClaimExpiresAt = DateTime.UtcNow.AddSeconds(leaseSeconds); x.LeaseGeneration++; x.Status = x.Status == "queued" ? "running" : x.Status; x.Version++; x.Updated = DateTime.UtcNow; var resume = x.ResumeInput is not null; return Task.FromResult<OrchestratorRunCommandClaim?>(new(commandId, runId, resume ? "resume" : "start", token, x.ClaimExpiresAt, x.LeaseGeneration, x.Hash, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Snapshot)), resume ? x.ResumeInput : null, resume ? x.CheckpointRef : null, resume ? x.CheckpointVersion : null)); } }
    public Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string tenant, string user, Guid runId, Guid commandId, string claimToken, long leaseGeneration, int leaseSeconds, CancellationToken ct)
    { lock (_gate) { if (string.IsNullOrWhiteSpace(claimToken) || !_runs.TryGetValue(runId, out var x) || x.Tenant != tenant || x.User != user || x.CommandId != commandId || x.CommandCompleted || leaseSeconds is < 1 or > 300 || x.LeaseGeneration != leaseGeneration || x.ClaimExpiresAt <= DateTime.UtcNow || !string.Equals(x.ClaimTokenHash, Skills.SkillHash.Sha256(claimToken), StringComparison.Ordinal)) return Task.FromResult<OrchestratorRunCommandClaim?>(null); x.ClaimExpiresAt = DateTime.UtcNow.AddSeconds(leaseSeconds); var resume = x.ResumeInput is not null; return Task.FromResult<OrchestratorRunCommandClaim?>(new(commandId, runId, resume ? "resume" : "start", claimToken, x.ClaimExpiresAt, leaseGeneration, x.Hash, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Snapshot)), resume ? x.ResumeInput : null, resume ? x.CheckpointRef : null, resume ? x.CheckpointVersion : null)); } }
    public Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string tenant, string user, Guid runId, Guid commandId, string claimToken, CancellationToken ct)
    { lock (_gate) { if (!_runs.TryGetValue(runId, out var x) || x.Tenant != tenant || x.User != user || x.CommandId != commandId) return Task.FromResult(OrchestratorRunDispatchCompleteStatus.NotFound); if (x.CommandCompleted || x.ClaimExpiresAt <= DateTime.UtcNow || !string.Equals(x.ClaimTokenHash, Skills.SkillHash.Sha256(claimToken ?? ""), StringComparison.Ordinal)) return Task.FromResult(OrchestratorRunDispatchCompleteStatus.Conflict); x.DispatchCompleted = true; return Task.FromResult(OrchestratorRunDispatchCompleteStatus.Success); } }
    public Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string workerId, int limit, int leaseSeconds, CancellationToken ct)
    { lock (_gate) { if (string.IsNullOrWhiteSpace(workerId) || limit is < 1 or > 100 || leaseSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(workerId)); var candidates = _runs.Values.Where(x => !x.CommandCompleted && x.ClaimExpiresAt <= DateTime.UtcNow && (x.Status is "queued" or "running")).OrderBy(x => x.Created).Take(limit + 1).ToArray(); var items = new List<OrchestratorRunRecoveryItem>(); foreach (var x in candidates.Take(limit)) { var claim = ClaimCommandAsync(x.Tenant, x.User, x.Id, x.CommandId, workerId, leaseSeconds, ct).GetAwaiter().GetResult(); if (claim is not null) items.Add(new(x.Tenant, x.User, "ADMIN", claim)); } return Task.FromResult(new OrchestratorRunRecoveryResponse(items, candidates.Length > limit)); } }
    public async Task<OrchestratorChildResponse?> CreateChildAsync(string tenant, string user, Guid rootRunId, OrchestratorChildCreateRequest request, CancellationToken ct)
    {
        // Same fail-closed boundary as the PostgreSQL authority: a malformed child throws so the
        // controller can answer 400, instead of a null that becomes an eternally retryable 409.
        var (taskId, runKind, envelope) = OrchestratorTaskEnvelope.ValidateChild(request);
        using var canonicalDocument = JsonDocument.Parse(envelope.Canonical);
        var canonicalEnvelope = canonicalDocument.RootElement.Clone();
        var source = agentRuns is null ? null : await ResolveChildSourceAsync(tenant, request.AgentId, request.AgentRevision, ct);
        lock (_gate)
        {
            if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user || root.Status is not ("queued" or "running")) return null;
            using var doc = JsonDocument.Parse(root.Snapshot); var pin = Pin(doc.RootElement, runKind, request.AgentId, request.AgentRevision);
            if (pin is null || request.TokenCap != pin.Value.TokenCap || (source is not null && (!string.Equals(source.Value.Agent.DefinitionSha256, pin.Value.Hash, StringComparison.Ordinal) || source.Value.Workflow.WorkflowId != pin.Value.Workflow || source.Value.Workflow.Revision != pin.Value.Revision))) return null;
            var limits = doc.RootElement.GetProperty("limits");
            if (root.Children.Count >= limits.GetProperty("max_child_runs").GetInt32() || root.Children.Count(x => x.Status is "queued" or "running") >= limits.GetProperty("max_concurrency").GetInt32() || root.Children.Any(x => x.Task == taskId && x.Attempt == request.Attempt)) return null;
            var agentRunId = Guid.NewGuid(); var commandId = Guid.NewGuid();
            if (agentRuns is not null)
            {
                if (agentRuns is not IOrchestratorChildRunRepository childRuns || source is null) return null;
                var caller = doc.RootElement.GetProperty("caller");
                var caps = Strings(caller.GetProperty("tool_grants")).Select(x => "tool.use:" + x).Concat(Strings(caller.GetProperty("knowledge_grants")).Select(x => "knowledge.read:" + x)).ToArray();
                var started = childRuns.CreateOrchestratorChildAsync(tenant, user, root.Role, Strings(caller.GetProperty("groups")), caps, source.Value.Agent, source.Value.Workflow, new OrchestratorChildSnapshotProvenance(rootRunId, taskId, request.Attempt), runKind, pin.Value.TokenCap, canonicalEnvelope, $"orchestrator-child:{rootRunId:D}:{taskId}:{request.Attempt}:{runKind}", ct).GetAwaiter().GetResult();
                if (started.Status is not (AgentRunWriteStatus.Success or AgentRunWriteStatus.Replay) || started.Run is null || started.Dispatch is null) return null;
                agentRunId = started.Run.Id; commandId = started.Dispatch.CommandId;
            }
            var commandInput = JsonSerializer.SerializeToElement(new { message = envelope.Objective, task_envelope = canonicalEnvelope });
            var artifact = JsonSerializer.SerializeToElement(new { orchestrator_root_run_id = rootRunId, task_id = taskId, attempt = request.Attempt, run_kind = runKind, root_snapshot_hash = root.Hash, agent_snapshot_hash = pin.Value.Hash, agent_run_id = agentRunId, command_id = commandId, command_input = commandInput });
            var child = new Child(Guid.NewGuid(), taskId, request.Attempt, runKind, request.AgentId, request.AgentRevision, pin.Value.Workflow, pin.Value.Revision, pin.Value.Hash, agentRunId, commandId, canonicalEnvelope, artifact); root.Children.Add(child); root.Events.Add(new(root.Events.Count + 1, "child_created", root.Hash, JsonDocument.Parse(OrchestratorRunEvents.ChildCreated(child.Id, agentRunId, child.Task, child.Attempt, child.Kind)).RootElement.Clone(), DateTime.UtcNow)); return new(child.Id, rootRunId, child.Task, child.Attempt, child.Kind, child.AgentId, child.AgentRevision, child.WorkflowId, child.WorkflowRevision, child.Hash, child.Status, child.AgentRunId, child.CommandId);
        }
    }
    public Task<OrchestratorChildStatusResponse?> GetChildAsync(string tenant, string user, Guid rootRunId, Guid childId, CancellationToken ct)
    { lock (_gate) { if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user) return Task.FromResult<OrchestratorChildStatusResponse?>(null); var child = root.Children.SingleOrDefault(x => x.Id == childId); if (child is null) return Task.FromResult<OrchestratorChildStatusResponse?>(null); AgentRunResponse? run = agentRuns?.GetAsync(tenant, user, child.AgentRunId, ct).GetAwaiter().GetResult(); if (run is not null) { var prior = child.Status; child.Status = run.Status; if (prior != child.Status && child.Status is "completed" or "failed" or "cancelled") root.Events.Add(new(root.Events.Count + 1, "child_terminal", root.Hash, JsonDocument.Parse(OrchestratorRunEvents.ChildTerminal(child.Id, child.AgentRunId, child.Task, child.Attempt, child.Kind, child.AgentId, child.AgentRevision, child.Hash, child.Status, run.Result, run.ErrorCode)).RootElement.Clone(), DateTime.UtcNow)); } var output = run?.Result ?? JsonDocument.Parse("{}").RootElement.Clone(); var citations = output.ValueKind == JsonValueKind.Object && output.TryGetProperty("citations", out var cits) && cits.ValueKind == JsonValueKind.Array ? cits.Clone() : JsonDocument.Parse("[]").RootElement.Clone(); return Task.FromResult<OrchestratorChildStatusResponse?>(new(child.Id, rootRunId, child.Task, child.Attempt, child.Kind, child.AgentId, child.AgentRevision, child.WorkflowId, child.WorkflowRevision, child.Hash, child.AgentRunId, child.Status, output, citations, run?.ErrorCode, run?.ErrorMessage)); } }
    public Task<OrchestratorRunWriteResult> TransitionAsync(string tenant, string user, Guid rootRunId, OrchestratorRootTransitionRequest request, CancellationToken ct)
    { lock (_gate) { if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user) return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.NotFound)); if (root.Status is "completed" or "failed" or "cancelled" || root.Version != request.ExpectedStateVersion || request.ToStatus is not ("waiting_input" or "completed" or "failed" or "cancelled") || (request.ToStatus == "waiting_input" && (string.IsNullOrWhiteSpace(request.CheckpointRef) || request.CheckpointVersion is null || request.CheckpointVersion < 1)) || root.LeaseGeneration != request.LeaseGeneration || !string.Equals(root.ClaimTokenHash, Skills.SkillHash.Sha256(request.ClaimToken ?? ""), StringComparison.Ordinal) || root.ClaimExpiresAt <= DateTime.UtcNow) return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Conflict, ToResponse(root), "Root command lease changed")); root.Status = request.ToStatus!; if (root.Status == "waiting_input") { root.CheckpointRef = request.CheckpointRef; root.CheckpointVersion = request.CheckpointVersion!.Value; } root.CommandCompleted = true; root.ClaimTokenHash = null; root.ClaimExpiresAt = DateTime.MinValue; root.Version++; root.Updated = DateTime.UtcNow; foreach (var e in request.Events ?? Array.Empty<OrchestratorRootEventAppend>()) root.Events.Add(new(root.Events.Count + 1, e.EventType ?? "root_event", root.Hash, e.Payload?.Clone() ?? JsonDocument.Parse("{}").RootElement.Clone(), root.Updated)); root.Events.Add(new(root.Events.Count + 1, root.Status == "waiting_input" ? "root_waiting_input" : "root_terminal", root.Hash, JsonDocument.Parse(OrchestratorRunEvents.RootTerminal(root.Status)).RootElement.Clone(), root.Updated)); return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Success, ToResponse(root))); } }
    public Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string tenant, string user, Guid rootRunId, OrchestratorContextAcquireRequest request, CancellationToken ct)
    { lock (_gate) { if (!_runs.TryGetValue(rootRunId, out var root) || root.Tenant != tenant || root.User != user || request.ContextRound < 1 || request.CurrentContext is not { ValueKind: JsonValueKind.Object }) return Task.FromResult<OrchestratorContextAcquireResponse?>(null); using var snapshot = JsonDocument.Parse(root.Snapshot); var authority = snapshot.RootElement.GetProperty("authority"); var tools = authority.GetProperty("context_tools").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal); var knowledge = authority.GetProperty("knowledge_sources").EnumerateArray().Select(x => x.GetString()!).OrderBy(x => x, StringComparer.Ordinal); if (!tools.SequenceEqual((request.AllowedTools ?? []).OrderBy(x => x, StringComparer.Ordinal)) || !knowledge.SequenceEqual((request.AllowedKnowledgeSources ?? []).OrderBy(x => x, StringComparer.Ordinal))) return Task.FromResult<OrchestratorContextAcquireResponse?>(null); if (request.CurrentContext.Value.TryGetProperty("user_input", out var input) && input.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(input.GetString())) return Task.FromResult<OrchestratorContextAcquireResponse?>(new(true, request.CurrentContext.Value.Clone(), [], [])); var missing = tools.Select(x => "context-tool:" + x).Concat(knowledge.Select(x => "knowledge-source:" + x)).DefaultIfEmpty("context-adapter-unavailable").ToArray(); return Task.FromResult<OrchestratorContextAcquireResponse?>(new(false, JsonDocument.Parse("{}").RootElement.Clone(), [], missing)); } }
    public Task<OrchestratorRunWriteResult> CancelAsync(string tenant, string user, Guid id, string? reason, string key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var x) || x.Tenant != tenant || x.User != user)
                return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.NotFound));

            // Expiry never creates a cancel idempotency record.  Do not let a late request
            // turn a deadline terminal state into a replay or append a cancellation event.
            if (x.Status == "timed_out")
                return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal"));

            if (_cancelKeys.TryGetValue((tenant, user, id, key), out var replay))
                return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Replay, replay, Replayed: true));

            if (x.Status is "completed" or "failed" or "cancelled" or "timed_out")
                return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, Message: "Run is terminal"));

            x.Status = "cancelled";
            foreach (var child in x.Children.Where(child => child.Status is "queued" or "running"))
            {
                if (agentRuns is not null)
                    agentRuns.CancelAsync(tenant, user, child.AgentRunId, reason, $"orchestrator-cancel:{id:D}:{child.AgentRunId:D}", ct).GetAwaiter().GetResult();
                child.Status = "cancelled";
            }
            x.CancelRequested = true;
            x.Version++;
            x.Updated = DateTime.UtcNow;
            x.Events.Add(new(x.Events.Count + 1, "run_cancelled", x.Hash, JsonDocument.Parse(JsonSerializer.Serialize(new { reason })).RootElement.Clone(), x.Updated));
            var response = ToResponse(x);
            _cancelKeys[(tenant, user, id, key)] = response;
            return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.Success, response));
        }
    }
    private static OrchestratorRunResponse ToResponse(Entry x) { using var d = JsonDocument.Parse(x.Snapshot); return new(x.Id, x.OrchestratorId, x.OrchestratorRevision, x.Conversation, x.WorkflowId, x.WorkflowRevision, x.Hash, x.Status, x.CancelRequested, x.Version, x.Deadline, Budgets(d.RootElement), x.Created, x.Updated, ErrorCode: x.ErrorCode, ErrorMessage: x.ErrorMessage); }
    private static JsonElement Budgets(JsonElement snapshot)
    { var limits = snapshot.GetProperty("limits"); var value = new System.Text.Json.Nodes.JsonObject { { "maxContextRounds", limits.GetProperty("max_context_rounds").GetInt32() }, { "maxTasks", limits.GetProperty("max_tasks").GetInt32() }, { "maxChildRuns", limits.GetProperty("max_child_runs").GetInt32() }, { "maxConcurrency", limits.GetProperty("max_concurrency").GetInt32() }, { "maxRepairRounds", limits.GetProperty("max_repair_rounds").GetInt32() }, { "timeoutSeconds", limits.GetProperty("timeout_seconds").GetDouble() }, { "tokenBudget", snapshot.GetProperty("token_budget").GetInt32() } }; return JsonDocument.Parse(value.ToJsonString()).RootElement.Clone(); }
    private static (Guid Workflow, int Revision, string Hash, int TokenCap)? Pin(JsonElement root, string kind, Guid agent, int revision) { IEnumerable<JsonElement> pins = kind == "verifier" ? [root.GetProperty("verifier")] : root.GetProperty("workers").EnumerateArray(); foreach (var pin in pins) if (Guid.TryParse(pin.GetProperty("agent_id").GetString(), out var id) && id == agent && pin.GetProperty("agent_revision").GetInt32() == revision && Guid.TryParse(pin.GetProperty("workflow_id").GetString(), out var workflow) && pin.TryGetProperty("token_cap", out var cap) && cap.TryGetInt32(out var tokenCap) && tokenCap > 0) return (workflow, pin.GetProperty("workflow_revision").GetInt32(), pin.GetProperty("snapshot_hash").GetString()!, tokenCap); return null; }
    private async Task<(PublishedAgentSnapshotSource Agent, WorkflowSnapshotSource Workflow)?> ResolveChildSourceAsync(string tenant, Guid agentId, int revision, CancellationToken ct)
    { var agent = await agents.GetAsync(tenant, agentId, ct); var definition = await agents.GetRevisionDefinitionAsync(tenant, agentId, revision, ct); var info = (await agents.ListRevisionsAsync(tenant, agentId, ct)).SingleOrDefault(x => x.Revision == revision); if (agent is null || definition is null || info is null || !agent.Enabled || agent.PublishedRevision != revision || info.RuntimeWorkflowId is not Guid workflowId || info.RuntimeWorkflowRevision is not int workflowRevision) return null; var workflow = await workflows.GetRevisionAsync(tenant, workflowId, workflowRevision, ct); if (workflow is null) return null; return (new(agentId, agent.Name, revision, definition, info.DefinitionSha256, workflowId, workflowRevision, info.SkillBindings), new(workflowId, workflowRevision, 1, workflow.Value.Definition, WorkflowCanonicalizer.Hash(workflow.Value.Definition), WorkflowCompilerContracts.Current)); }
    private static IReadOnlyCollection<string> Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
    Task<OrchestratorRunWriteResult> IOrchestratorRunRepository.ResumeAsync(string tenant, string user, Guid id, string input, string key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(id, out var run) && run.Tenant == tenant && run.User == user && run.Deadline <= DateTime.UtcNow)
            { Expire(run); return Task.FromResult(new OrchestratorRunWriteResult(OrchestratorRunWriteStatus.InvalidState, ToResponse(run), "Root run deadline has expired")); }
        }
        return ResumeAsync(tenant, user, id, input, key, ct);
    }
    async Task<OrchestratorRunCommandClaim?> IOrchestratorRunRepository.ClaimCommandAsync(string tenant, string user, Guid runId, Guid commandId, string workerId, int leaseSeconds, CancellationToken ct)
    {
        Entry? run;
        lock (_gate)
        {
            _runs.TryGetValue(runId, out run);
            if (run is not null && run.Tenant == tenant && run.User == user && run.Deadline <= DateTime.UtcNow)
            {
                Expire(run);
                return null;
            }
        }
        var claim = await ClaimCommandAsync(tenant, user, runId, commandId, workerId, leaseSeconds, ct);
        return claim is null || run is null ? claim : claim with { DeadlineAt = run.Deadline };
    }
    Task<OrchestratorRunRecoveryResponse> IOrchestratorRunRepository.ClaimRecoveryAsync(string workerId, int limit, int leaseSeconds, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values.Where(x => !x.CommandCompleted && x.Deadline <= DateTime.UtcNow && (x.Status is "queued" or "running" or "waiting_input")))
            {
                Expire(run);
            }
        }
        return ClaimRecoveryAsync(workerId, limit, leaseSeconds, ct);
    }
    private static void Expire(Entry run) { if (run.Status is "completed" or "failed" or "cancelled" or "timed_out") return; run.Status = "timed_out"; run.ErrorCode = "deadline_exceeded"; run.ErrorMessage = "Root run deadline expired"; run.CommandCompleted = true; run.ClaimTokenHash = null; run.ClaimExpiresAt = DateTime.MinValue; run.Version++; run.Updated = DateTime.UtcNow; foreach (var child in run.Children.Where(x => x.Status is "queued" or "running")) child.Status = "cancelled"; run.Events.Add(new(run.Events.Count + 1, "root_timed_out", run.Hash, JsonDocument.Parse("{}").RootElement.Clone(), run.Updated)); }
    private sealed class Entry(Guid id, Guid commandId, string tenant, string user, string role, Guid oid, int orev, string conversation, Guid wid, int wrev, string snapshot, string hash, DateTime deadline, DateTime now) { public Guid Id = id, CommandId = commandId; public string Tenant = tenant, User = user, Role = role, Conversation = conversation, Snapshot = snapshot, Hash = hash, Status = "queued"; public Guid OrchestratorId = oid, WorkflowId = wid; public int OrchestratorRevision = orev, WorkflowRevision = wrev; public long Version = 1, LeaseGeneration, CheckpointVersion; public bool CancelRequested, CommandCompleted, DispatchCompleted; public string? ClaimTokenHash, ResumeInput, CheckpointRef, ErrorCode, ErrorMessage; public DateTime ClaimExpiresAt = DateTime.MinValue; public DateTime Deadline = deadline, Created = now, Updated = now; public List<E> Events = []; public List<Child> Children = []; }
    private sealed class Child(Guid id, string task, int attempt, string kind, Guid agentId, int agentRevision, Guid workflowId, int workflowRevision, string hash, Guid agentRunId, Guid commandId, JsonElement taskEnvelope, JsonElement dispatchArtifact) { public Guid Id = id, AgentId = agentId, WorkflowId = workflowId, AgentRunId = agentRunId, CommandId = commandId; public string Task = task, Kind = kind, Hash = hash, Status = "queued"; public int Attempt = attempt, AgentRevision = agentRevision, WorkflowRevision = workflowRevision; public JsonElement TaskEnvelope = taskEnvelope.Clone(), DispatchArtifact = dispatchArtifact.Clone(); }
    private sealed record E(long Sequence, string Type, string Hash, JsonElement Payload, DateTime At);
}
