using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Backend.Api.AgentRuns;
using Backend.Api.Skills;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// Lite-mode parity implementation for D7's durable-approval state machine.
/// It remains process-local by definition, but its effect/outbox and execute
/// claim rules intentionally match the PostgreSQL authority.
/// </summary>
public sealed partial class InMemoryAgentRunApprovalRepository : IAgentRunApprovalRepository
{
    private readonly IAgentRunRepository _agentRuns;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AgentRunApprovalResponse> _items = new();
    private readonly HashSet<(Guid Approval, string Key)> _decisions = new();
    private readonly Dictionary<Guid, (string Tenant, string Owner, string Status)> _runs = new();
    private readonly Dictionary<(Guid Run, string Fingerprint), Effect> _effects = new();
    private readonly Dictionary<Guid, (string Tenant, string RecordId, string Value)> _outbox = new();
    private readonly Dictionary<Guid, Execute> _executions = new();

    private sealed record Effect(Guid Id, string Status, string? RecordId = null, string? Value = null);
    private sealed record Execute(string Status, string? ClaimTokenHash = null, DateTime? ClaimExpiresAt = null, int Attempts = 0);

    public InMemoryAgentRunApprovalRepository(IAgentRunRepository agentRuns, TimeProvider? timeProvider = null)
    {
        _agentRuns = agentRuns;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<AgentRunApprovalWriteResult> CreateAsync(string tenantId, string userId, Guid runId, AgentRunApprovalCreateRequest request, CancellationToken ct)
    {
        if (request.ExpectedVersion < 1 || request.LeaseGeneration < 1 || string.IsNullOrWhiteSpace(request.LeaseToken)
            || string.IsNullOrWhiteSpace(request.CheckpointRef) || request.CheckpointVersion < 1
            || !Role().IsMatch(request.RequiredRole ?? "") || !Fingerprint().IsMatch(request.ActionFingerprint ?? "")
            || request.ExpiresAt is not DateTime expiry || expiry <= UtcNow() || expiry > UtcNow().AddDays(1))
            return new(AgentRunApprovalWriteStatus.InvalidState, Message: "invalid approval request");

        var existing = await _agentRuns.GetAsync(tenantId, userId, runId, ct);
        if (existing is null || existing.CancelRequested || existing.Status != AgentRunStatuses.Running) return new(AgentRunApprovalWriteStatus.NotFound);
        var moved = await _agentRuns.TransitionAsync(tenantId, userId, runId,
            new AgentRunTransitionRequest(request.ExpectedVersion, AgentRunStatuses.WaitingApproval, request.LeaseToken, request.LeaseGeneration, existing.EventAckCursor, request.CheckpointRef, request.CheckpointVersion), ct);
        if (moved.Status != AgentRunWriteStatus.Success) return new(AgentRunApprovalWriteStatus.Conflict, Message: moved.Message);

        lock (_gate)
        {
            _runs[runId] = (tenantId, userId, AgentRunStatuses.WaitingApproval);
            // D7 never trusts a Workflow/browser request to relax separation of
            // duties; PostgreSQL has the same server-forced value.
            var value = new AgentRunApprovalResponse(Guid.NewGuid(), runId, "pending", request.RequiredRole!, request.ActionFingerprint!, expiry,
                true, userId, null, null, null, null, request.CheckpointRef!, request.CheckpointVersion);
            _items[value.Id] = value;
            return new(AgentRunApprovalWriteStatus.Success, value);
        }
    }

    public Task<IReadOnlyList<AgentRunApprovalResponse>?> ListAsync(string tenantId, string userId, string role, Guid runId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run) || run.Tenant != tenantId) return Task.FromResult<IReadOnlyList<AgentRunApprovalResponse>?>(null);
            return Task.FromResult<IReadOnlyList<AgentRunApprovalResponse>?>(_items.Values
                .Where(x => x.RunId == runId && (x.RequestedBy == userId || x.RequiredRole == role && (!x.SelfApprovalForbidden || x.RequestedBy != userId)))
                .OrderBy(x => x.ExpiresAt).ToArray());
        }
    }

    public async Task<AgentRunApprovalWriteResult> DecideAsync(string tenantId, string approverId, string approverRole, Guid runId, Guid approvalId, bool approve, string idempotencyKey, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128 || reason?.Length > 500)
            return new(AgentRunApprovalWriteStatus.InvalidState, Message: "invalid decision request");
        (string Tenant, string Owner, string Status) run;
        AgentRunApprovalResponse item;
        var expired = false;
        var key = (approvalId, SkillHash.Sha256(idempotencyKey));
        lock (_gate)
        {
            if (!_items.TryGetValue(approvalId, out item!) || item.RunId != runId || !_runs.TryGetValue(runId, out run) || run.Tenant != tenantId) return new(AgentRunApprovalWriteStatus.NotFound);
            if (_decisions.Contains(key) || item.Status != "pending") return new(AgentRunApprovalWriteStatus.Replay, item);
            if (item.ExpiresAt <= UtcNow()) expired = true;
            if (!string.Equals(item.RequiredRole, approverRole, StringComparison.Ordinal) || item.SelfApprovalForbidden && item.RequestedBy == approverId) return new(AgentRunApprovalWriteStatus.Forbidden, item);
        }
        var record = await _agentRuns.GetAsync(tenantId, run.Owner, runId, ct);
        if (record is null) return new(AgentRunApprovalWriteStatus.NotFound);
        if (record.CancelRequested)
        {
            lock (_gate)
            {
                if (_items.GetValueOrDefault(approvalId) is { Status: "pending" } pending)
                    _items[approvalId] = pending with { Status = "cancelled" };
            }
            return new(AgentRunApprovalWriteStatus.InvalidState, _items.GetValueOrDefault(approvalId), Message: "run cancellation was requested");
        }
        if (expired)
        {
            if (_agentRuns is IAgentRunApprovalDecisionTransition expiryTransition)
                await expiryTransition.ResolveApprovalAsync(tenantId, run.Owner, runId, record.StateVersion, false, ct);
            lock (_gate)
            {
                var current = _items.GetValueOrDefault(approvalId);
                if (current is not null && current.Status == "pending") _items[approvalId] = current with { Status = "expired" };
                _runs[runId] = (run.Tenant, run.Owner, AgentRunStatuses.Failed);
                return new(AgentRunApprovalWriteStatus.Expired, _items.GetValueOrDefault(approvalId));
            }
        }
        var moved = _agentRuns is IAgentRunApprovalDecisionTransition decisionTransition
            ? await decisionTransition.ResolveApprovalAsync(tenantId, run.Owner, runId, record.StateVersion, approve, ct)
            : new AgentRunWriteResult(AgentRunWriteStatus.Conflict, Message: "approval decision transition is unavailable");
        if (moved.Status != AgentRunWriteStatus.Success)
        {
            // The narrow state transition sees cancel_requested under the
            // AgentRun lock, closing the cancellation/decision race.
            var currentRun = await _agentRuns.GetAsync(tenantId, run.Owner, runId, ct);
            if (currentRun?.CancelRequested == true)
            {
                lock (_gate)
                {
                    if (_items.GetValueOrDefault(approvalId) is { Status: "pending" } pending)
                        _items[approvalId] = pending with { Status = "cancelled" };
                }
                return new(AgentRunApprovalWriteStatus.InvalidState, _items.GetValueOrDefault(approvalId), Message: "run cancellation was requested");
            }
            return new(AgentRunApprovalWriteStatus.Conflict);
        }
        lock (_gate)
        {
            if (!_items.TryGetValue(approvalId, out var current) || current.Status != "pending" || _decisions.Contains(key)) return new(AgentRunApprovalWriteStatus.Conflict);
            var decision = approve ? "approved" : "rejected";
            current = current with { Status = decision, Decision = decision, DecidedBy = approverId, DecidedAt = UtcNow(), Reason = reason };
            _items[approvalId] = current;
            _runs[runId] = (run.Tenant, run.Owner, approve ? AgentRunStatuses.Queued : AgentRunStatuses.Failed);
            _decisions.Add(key);
            if (approve) _executions[approvalId] = new Execute("queued");
            return new(AgentRunApprovalWriteStatus.Success, current);
        }
    }

    public Task<(AgentRunApprovalWriteStatus Status, AgentRunApprovalConsumeResponse? Response, string? Message)> ConsumeAsync(string tenantId, Guid runId, Guid approvalId, AgentRunApprovalConsumeRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Fingerprint().IsMatch(request.ActionFingerprint ?? "") || request.LeaseGeneration < 1 || string.IsNullOrWhiteSpace(request.LeaseToken))
                return Task.FromResult((AgentRunApprovalWriteStatus.InvalidState, (AgentRunApprovalConsumeResponse?)null, (string?)"invalid consume request"));
            if (!_items.TryGetValue(approvalId, out var item) || item.RunId != runId || !_runs.TryGetValue(runId, out var run) || run.Tenant != tenantId)
                return Task.FromResult((AgentRunApprovalWriteStatus.NotFound, (AgentRunApprovalConsumeResponse?)null, (string?)null));
            if (item.ExpiresAt <= UtcNow()) return Task.FromResult((AgentRunApprovalWriteStatus.Expired, (AgentRunApprovalConsumeResponse?)null, (string?)null));
            if (!string.Equals(item.ActionFingerprint, request.ActionFingerprint, StringComparison.Ordinal)) return Task.FromResult((AgentRunApprovalWriteStatus.Conflict, (AgentRunApprovalConsumeResponse?)null, (string?)"approval action changed"));
            // Failing closed is preferable to accepting a stale/revoked lease
            // in test/lite adapters that do not expose their protected lease
            // state.  The production in-memory runtime implements this seam.
            if (_agentRuns is not IAgentRunApprovalLeaseVerifier leases || !leases.HasActiveApprovalLease(tenantId, run.Owner, runId, request.LeaseToken!, request.LeaseGeneration))
                return Task.FromResult((AgentRunApprovalWriteStatus.Conflict, (AgentRunApprovalConsumeResponse?)null, (string?)"stale write lease"));
            var key = (runId, item.ActionFingerprint);
            if (_effects.TryGetValue(key, out var effect))
                return Task.FromResult((effect.Status == "completed" ? AgentRunApprovalWriteStatus.Replay : AgentRunApprovalWriteStatus.Success,
                    (AgentRunApprovalConsumeResponse?)new AgentRunApprovalConsumeResponse(effect.Id, effect.Status == "reserved" ? "granted" : effect.Status), (string?)null));
            if (item.Status != "approved") return Task.FromResult((AgentRunApprovalWriteStatus.Conflict, (AgentRunApprovalConsumeResponse?)null, (string?)"approval is not executable"));
            var id = Guid.NewGuid();
            _effects[key] = new Effect(id, "reserved");
            _items[approvalId] = item with { Status = "consumed" };
            return Task.FromResult((AgentRunApprovalWriteStatus.Success, (AgentRunApprovalConsumeResponse?)new AgentRunApprovalConsumeResponse(id, "granted"), (string?)null));
        }
    }

    public Task<AgentRunApprovalWriteStatus> CompleteEffectAsync(string tenantId, Guid runId, Guid effectId, bool succeeded, CancellationToken ct)
    {
        lock (_gate)
        {
            var entry = _effects.SingleOrDefault(x => x.Value.Id == effectId);
            if (entry.Key == default || entry.Key.Run != runId || !_runs.TryGetValue(runId, out var run) || run.Tenant != tenantId) return Task.FromResult(AgentRunApprovalWriteStatus.NotFound);
            var target = succeeded ? "completed" : "failed";
            if (entry.Value.Status == target) return Task.FromResult(AgentRunApprovalWriteStatus.Success);
            if (entry.Value.Status != "reserved") return Task.FromResult(AgentRunApprovalWriteStatus.Conflict);
            _effects[entry.Key] = entry.Value with { Status = target };
            return Task.FromResult(AgentRunApprovalWriteStatus.Success);
        }
    }

    public async Task<(AgentRunApprovalWriteStatus Status, AgentRunWriteEvidenceResponse? Response, string? Message)> WriteEvidenceAsync(string tenantId, Guid runId, Guid effectId, AgentRunWriteEvidenceRequest request, CancellationToken ct)
    {
        var recordId = request.RecordId?.Trim();
        if (string.IsNullOrWhiteSpace(recordId) || recordId.Length > 128 || request.Value is null || request.Value.Length > 4_000)
            return (AgentRunApprovalWriteStatus.InvalidState, null, "invalid write evidence");

        (string Tenant, string Owner, string Status) run;
        lock (_gate)
        {
            var preflight = _effects.SingleOrDefault(x => x.Value.Id == effectId);
            if (preflight.Key == default || preflight.Key.Run != runId || !_runs.TryGetValue(runId, out run) || run.Tenant != tenantId)
                return (AgentRunApprovalWriteStatus.NotFound, null, null);
        }
        // Read the authoritative live run rather than the approval cache.  In
        // lite mode this is the cancellation fence mirroring the PostgreSQL
        // `agent_run ... FOR UPDATE` check in WriteEvidenceAsync.
        var liveRun = await _agentRuns.GetAsync(tenantId, run.Owner, runId, ct);
        if (liveRun is null) return (AgentRunApprovalWriteStatus.NotFound, null, null);
        if (liveRun.CancelRequested)
            return (AgentRunApprovalWriteStatus.InvalidState, null, "run cancellation was requested");

        lock (_gate)
        {
            var entry = _effects.SingleOrDefault(x => x.Value.Id == effectId);
            if (entry.Key == default || entry.Key.Run != runId || !_runs.TryGetValue(runId, out var storedRun) || storedRun.Tenant != tenantId)
                return (AgentRunApprovalWriteStatus.NotFound, null, null);
            if (entry.Value.Status == "completed")
            {
                if (entry.Value.RecordId == recordId && entry.Value.Value == request.Value)
                    return (AgentRunApprovalWriteStatus.Replay, new AgentRunWriteEvidenceResponse(1, "replayed"), null);
                return (AgentRunApprovalWriteStatus.Conflict, null, "write effect payload changed");
            }
            if (entry.Value.Status != "reserved") return (AgentRunApprovalWriteStatus.Conflict, null, "write effect is not reservable");
            _effects[entry.Key] = entry.Value with { Status = "completed", RecordId = recordId, Value = request.Value };
            _outbox[effectId] = (tenantId, recordId, request.Value);
            return (AgentRunApprovalWriteStatus.Success, new AgentRunWriteEvidenceResponse(1, "written"), null);
        }
    }

    public Task<AgentRunApprovalExecutionIdentity?> GetExecutionIdentityAsync(string tenantId, string approverId, Guid runId, Guid approvalId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(approvalId, out var item) || item.RunId != runId || item.Status is not ("approved" or "consumed") || item.DecidedBy != approverId || !_runs.TryGetValue(runId, out var run) || run.Tenant != tenantId)
                return Task.FromResult<AgentRunApprovalExecutionIdentity?>(null);
            return Task.FromResult<AgentRunApprovalExecutionIdentity?>(new(run.Owner, "USER"));
        }
    }

    public async Task<AgentRunApprovalExecuteClaim?> ClaimExecuteAsync(string tenantId, Guid runId, Guid approvalId, CancellationToken ct)
    {
        (string Tenant, string Owner, string Status) run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out run) || run.Tenant != tenantId)
                return null;
        }
        var current = await _agentRuns.GetAsync(tenantId, run.Owner, runId, ct);
        lock (_gate)
            return ClaimExecuteLocked(tenantId, runId, approvalId, current?.CancelRequested ?? true);
    }

    public async Task<IReadOnlyList<AgentRunApprovalExecuteClaim>> ClaimExecuteRecoveryAsync(int limit, CancellationToken ct)
    {
        List<(Guid ApprovalId, Guid RunId, string Tenant, string Owner)> candidates;
        lock (_gate)
        {
            candidates = _executions.Keys
                .Where(approvalId =>
                {
                    var execution = _executions[approvalId];
                    return execution.Status == "queued" || execution.Status == "claimed" && execution.ClaimExpiresAt <= UtcNow();
                })
                .Select(approvalId => _items.TryGetValue(approvalId, out var item)
                    && _runs.TryGetValue(item.RunId, out var run)
                    ? (ApprovalId: approvalId, RunId: item.RunId, Tenant: run.Tenant, Owner: run.Owner)
                    : (ApprovalId: Guid.Empty, RunId: Guid.Empty, Tenant: "", Owner: ""))
                .Where(item => item.ApprovalId != Guid.Empty)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList();
        }

        var result = new List<AgentRunApprovalExecuteClaim>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var current = await _agentRuns.GetAsync(candidate.Tenant, candidate.Owner, candidate.RunId, ct);
            lock (_gate)
            {
                var claim = ClaimExecuteLocked(candidate.Tenant, candidate.RunId, candidate.ApprovalId, current?.CancelRequested ?? true);
                if (claim is not null) result.Add(claim);
            }
        }
        return result;
    }

    public async Task<AgentRunApprovalWriteStatus> CompleteExecuteAsync(Guid approvalId, string claimToken, bool deadLetter, CancellationToken ct)
    {
        (string Tenant, string Owner, string Status) run;
        Guid runId;
        lock (_gate)
        {
            if (!_executions.TryGetValue(approvalId, out var execution) || execution.Status != "claimed" || execution.ClaimExpiresAt <= UtcNow() || !string.Equals(execution.ClaimTokenHash, SkillHash.Sha256(claimToken), StringComparison.Ordinal))
                return AgentRunApprovalWriteStatus.Conflict;
            if (!_items.TryGetValue(approvalId, out var item) || !_runs.TryGetValue(item.RunId, out run)) return AgentRunApprovalWriteStatus.NotFound;
            runId = item.RunId;
            _executions[approvalId] = execution with { Status = deadLetter ? "dead_letter" : "completed" };
            if (!deadLetter) return AgentRunApprovalWriteStatus.Success;
        }
        var current = await _agentRuns.GetAsync(run.Tenant, run.Owner, runId, ct);
        if (current is not null && !current.CancelRequested
            && current.Status is AgentRunStatuses.Queued or AgentRunStatuses.Running)
        {
            await _agentRuns.TransitionAsync(run.Tenant, run.Owner, current.Id,
                new AgentRunTransitionRequest(current.StateVersion, AgentRunStatuses.Failed), ct);
        }
        return AgentRunApprovalWriteStatus.Success;
    }

    private AgentRunApprovalExecuteClaim? ClaimExecuteLocked(string tenantId, Guid runId, Guid approvalId, bool cancelRequested = false)
    {
        if (!_executions.TryGetValue(approvalId, out var execution) || !_items.TryGetValue(approvalId, out var item)
            || !_runs.TryGetValue(runId, out var run) || item.RunId != runId || run.Tenant != tenantId) return null;
        if (cancelRequested || item.ExpiresAt <= UtcNow() || run.Status is AgentRunStatuses.Completed or AgentRunStatuses.Failed or AgentRunStatuses.Cancelled)
        {
            _executions[approvalId] = execution with { Status = "dead_letter" };
            if (item.ExpiresAt <= UtcNow() && item.Status is ("approved" or "consumed")) _items[approvalId] = item with { Status = "expired" };
            return null;
        }
        if (execution.Status is "completed" or "dead_letter" || execution.Status == "claimed" && execution.ClaimExpiresAt > UtcNow()) return null;
        var token = RandomNumberGenerator.GetHexString(32);
        _executions[approvalId] = new Execute("claimed", SkillHash.Sha256(token), UtcNow().AddSeconds(60), execution.Attempts + 1);
        return new AgentRunApprovalExecuteClaim(approvalId, runId, tenantId, item.DecidedBy!, token);
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")] private static partial Regex Role();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Fingerprint();
}
