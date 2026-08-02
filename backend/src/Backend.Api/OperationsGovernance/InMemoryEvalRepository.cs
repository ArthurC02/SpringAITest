namespace Backend.Api.OperationsGovernance;

/// <summary>Lite-mode / test parity for the eval suite/result authority. Tenant-scoped, same
/// idempotency and immutability semantics as <see cref="EvalRepository"/>.</summary>
public sealed class InMemoryEvalRepository : IEvalRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string SuiteId), SuiteState> _suites = new();
    private readonly Dictionary<(string Tenant, Guid RunId), RunState> _runs = new();
    private readonly Dictionary<(string Tenant, string KeyHash), Guid> _byIdempotencyKey = new();

    public Task<IReadOnlyList<EvalSuiteSummary>> ListSuitesAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<EvalSuiteSummary> result = _suites
                .Where(kv => kv.Key.Tenant == tenantId)
                .OrderBy(kv => kv.Key.SuiteId, StringComparer.Ordinal)
                .Select(kv => Summary(kv.Key.SuiteId, kv.Value))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<EvalSuiteSummary?> GetSuiteAsync(string tenantId, string suiteId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_suites.TryGetValue((tenantId, suiteId), out var s) ? Summary(suiteId, s) : null);
    }

    public Task<IReadOnlyList<EvalSuiteRevisionSummary>> ListSuiteRevisionsAsync(
        string tenantId, string suiteId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<EvalSuiteRevisionSummary> result = _suites.TryGetValue((tenantId, suiteId), out var s)
                ? s.Revisions.OrderByDescending(r => r.Revision)
                    .Select(r => new EvalSuiteRevisionSummary(r.Revision, r.CasesSha256, r.CaseCount, r.CreatedBy, r.CreatedAt))
                    .ToList()
                : Array.Empty<EvalSuiteRevisionSummary>();
            return Task.FromResult(result);
        }
    }

    public Task<EvalSuiteRevisionRecord?> GetSuiteRevisionAsync(
        string tenantId, string suiteId, int revision, CancellationToken ct)
    {
        lock (_gate)
        {
            var record = _suites.TryGetValue((tenantId, suiteId), out var s)
                ? s.Revisions.FirstOrDefault(r => r.Revision == revision)
                : null;
            return Task.FromResult(record is null ? null : ToRecord(suiteId, record));
        }
    }

    public Task<EvalSuiteRevisionRecord> PublishSuiteRevisionAsync(
        string tenantId, string suiteId, string casesCanonical, string createdBy, CancellationToken ct)
    {
        lock (_gate)
        {
            var sha = Skills.SkillHash.Sha256(casesCanonical);
            if (!_suites.TryGetValue((tenantId, suiteId), out var state))
            {
                state = new SuiteState();
                _suites[(tenantId, suiteId)] = state;
            }

            var latest = state.Revisions.Count == 0 ? null : state.Revisions[^1];
            if (latest is not null && string.Equals(latest.CasesSha256, sha, StringComparison.Ordinal))
            {
                return Task.FromResult(ToRecord(suiteId, latest));
            }

            var revision = new RevisionState(
                (latest?.Revision ?? 0) + 1, casesCanonical, sha,
                EvalSuiteCodec.CaseCount(EvalSuiteCodec.Parse(casesCanonical).Cases), createdBy, DateTime.UtcNow);
            state.Revisions.Add(revision);
            state.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult(ToRecord(suiteId, revision));
        }
    }

    public Task<EvalRunWriteResult> CreateRunAsync(
        string tenantId, string idempotencyKeySha256, EvalRunWrite run, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_byIdempotencyKey.TryGetValue((tenantId, idempotencyKeySha256), out var existingId))
            {
                var existing = ToDetail(_runs[(tenantId, existingId)]);
                var matches = string.Equals(existing.Run.SuiteId, run.SuiteId, StringComparison.Ordinal)
                    && existing.Run.SuiteRevision == run.SuiteRevision
                    && string.Equals(existing.Run.CandidateIdentitySha256, run.CandidateIdentitySha256, StringComparison.Ordinal);
                return Task.FromResult(new EvalRunWriteResult(
                    matches ? EvalRunWriteStatus.Replay : EvalRunWriteStatus.Conflict, existing));
            }

            var state = new RunState(run);
            _runs[(tenantId, run.Id)] = state;
            _byIdempotencyKey[(tenantId, idempotencyKeySha256)] = run.Id;
            return Task.FromResult(new EvalRunWriteResult(EvalRunWriteStatus.Created, ToDetail(state)));
        }
    }

    public Task<EvalRunDetail?> GetRunAsync(string tenantId, Guid runId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_runs.TryGetValue((tenantId, runId), out var s) ? ToDetail(s) : null);
    }

    public Task<EvalRunDetail?> GetRunByIdempotencyKeyAsync(string tenantId, string idempotencyKeySha256, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_byIdempotencyKey.TryGetValue((tenantId, idempotencyKeySha256), out var id)
                ? ToDetail(_runs[(tenantId, id)]) : null);
    }

    public Task<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<EvalRunSummary> result = _runs
                .Where(kv => kv.Key.Tenant == tenantId)
                .Select(kv => ToDetail(kv.Value).Run)
                .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<EvalGateEvaluation?> EvaluateGateAsync(
        string tenantId, Guid runId, string expectedSuiteId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue((tenantId, runId), out var state))
            {
                return Task.FromResult<EvalGateEvaluation?>(null);
            }

            var passing = state.Write.Cases.Where(c => c.Verdict == "PASS").Select(c => c.CaseId)
                .ToHashSet(StringComparer.Ordinal);
            var failOrErrorCount = state.Write.Cases.Count(c => c.Verdict is "FAIL" or "ERROR");
            var suiteMatches = string.Equals(state.Write.SuiteId, expectedSuiteId, StringComparison.Ordinal);
            var missing = state.Write.RequiredCaseIds.Where(id => !passing.Contains(id)).ToList();
            var fresh = state.Write.FreshnessSeconds is not long limit
                || (DateTime.UtcNow - state.Write.CompletedAt).TotalSeconds <= limit;

            // Same fail-closed rules as EvalRepository: empty required_case_ids never passes, and
            // any FAIL/ERROR verdict anywhere in the run blocks the gate even outside the required set.
            var passed = suiteMatches && fresh && state.Write.RequiredCaseIds.Count > 0
                && missing.Count == 0 && failOrErrorCount == 0;
            return Task.FromResult<EvalGateEvaluation?>(new EvalGateEvaluation(passed, suiteMatches, fresh, missing));
        }
    }

    private static EvalSuiteSummary Summary(string suiteId, SuiteState s)
        => new(suiteId, s.Revisions.Count == 0 ? 0 : s.Revisions[^1].Revision, s.CreatedAt, s.UpdatedAt);

    private static EvalSuiteRevisionRecord ToRecord(string suiteId, RevisionState r)
    {
        var content = EvalSuiteCodec.Parse(r.CasesCanonical);
        return new EvalSuiteRevisionRecord(
            suiteId, r.Revision, r.CasesCanonical, r.CasesSha256, r.CaseCount,
            content.Policy?.RequiredCaseIds ?? Array.Empty<string>(), content.Policy?.FreshnessSeconds,
            r.CreatedBy, r.CreatedAt);
    }

    private static EvalRunDetail ToDetail(RunState s)
    {
        var w = s.Write;
        var pass = w.Cases.Count(c => c.Verdict == "PASS");
        var fail = w.Cases.Count(c => c.Verdict == "FAIL");
        var error = w.Cases.Count(c => c.Verdict == "ERROR");
        var summary = new EvalRunSummary(
            w.Id, w.SuiteId, w.SuiteRevision, w.CandidateKind, w.CandidateRefJson, w.CandidatePinsJson,
            w.CandidateIdentitySha256, w.RunnerVersion, w.StartedAt, w.CompletedAt, pass, fail, error);
        // Matches EvalRepository's "ORDER BY case_id" so both implementations return cases in the
        // same deterministic order.
        return new EvalRunDetail(summary, w.Cases.OrderBy(c => c.CaseId, StringComparer.Ordinal).ToList());
    }

    private sealed class SuiteState
    {
        public List<RevisionState> Revisions { get; } = [];
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    private sealed record RevisionState(
        int Revision, string CasesCanonical, string CasesSha256, int CaseCount, string CreatedBy, DateTime CreatedAt);

    private sealed class RunState
    {
        public RunState(EvalRunWrite write) => Write = write;
        public EvalRunWrite Write { get; }
    }
}
