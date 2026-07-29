using Dapper;
using Npgsql;

namespace Backend.Api.OperationsGovernance;

/// <summary>PostgreSQL implementation. No eval result is held only in process memory.</summary>
public sealed class EvalRepository(NpgsqlDataSource dataSource) : IEvalRepository
{
    public async Task<IReadOnlyList<EvalSuiteSummary>> ListSuitesAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EvalSuiteSummary>(new CommandDefinition(
            "SELECT suite_id AS SuiteId, current_revision AS CurrentRevision,"
            + " created_at AS CreatedAt, updated_at AS UpdatedAt"
            + " FROM eval_suite WHERE tenant_id=@tenantId ORDER BY suite_id",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<EvalSuiteSummary?> GetSuiteAsync(string tenantId, string suiteId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<EvalSuiteSummary>(new CommandDefinition(
            "SELECT suite_id AS SuiteId, current_revision AS CurrentRevision,"
            + " created_at AS CreatedAt, updated_at AS UpdatedAt"
            + " FROM eval_suite WHERE tenant_id=@tenantId AND suite_id=@suiteId",
            new { tenantId, suiteId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<EvalSuiteRevisionSummary>> ListSuiteRevisionsAsync(
        string tenantId, string suiteId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<EvalSuiteRevisionSummary>(new CommandDefinition(
            "SELECT r.revision AS Revision, r.cases_sha256 AS CasesSha256, r.case_count AS CaseCount,"
            + " r.created_by AS CreatedBy, r.created_at AS CreatedAt"
            + " FROM eval_suite_revision r JOIN eval_suite s ON s.id=r.suite_id"
            + " WHERE s.tenant_id=@tenantId AND s.suite_id=@suiteId ORDER BY r.revision DESC",
            new { tenantId, suiteId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<EvalSuiteRevisionRecord?> GetSuiteRevisionAsync(
        string tenantId, string suiteId, int revision, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RevisionRow>(new CommandDefinition(
            "SELECT r.cases_canonical AS CasesCanonical, r.cases_sha256 AS CasesSha256,"
            + " r.created_by AS CreatedBy, r.created_at AS CreatedAt"
            + " FROM eval_suite_revision r JOIN eval_suite s ON s.id=r.suite_id"
            + " WHERE s.tenant_id=@tenantId AND s.suite_id=@suiteId AND r.revision=@revision",
            new { tenantId, suiteId, revision }, cancellationToken: ct));
        return row is null ? null : ToRecord(suiteId, revision, row);
    }

    public async Task<EvalSuiteRevisionRecord> PublishSuiteRevisionAsync(
        string tenantId, string suiteId, string casesCanonical, string createdBy, CancellationToken ct)
    {
        var sha = SkillHashOf(casesCanonical);
        var caseCount = EvalSuiteCodec.CaseCount(EvalSuiteCodec.Parse(casesCanonical).Cases);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // ponytail: same first-publish race window as PromptArtifactRepository -- FOR UPDATE locks
        // an existing eval_suite row only, so two concurrent first publishes of the same (tenant,
        // suite_id) can both see "no suite yet" and race the INSERT below; uq_eval_suite_tenant_suite
        // backstops it (500 on the loser, no dirty/divergent data).
        var suite = await conn.QuerySingleOrDefaultAsync<SuiteRow>(new CommandDefinition(
            "SELECT id AS Id, current_revision AS CurrentRevision FROM eval_suite"
            + " WHERE tenant_id=@tenantId AND suite_id=@suiteId FOR UPDATE",
            new { tenantId, suiteId }, tx, cancellationToken: ct));

        if (suite is null)
        {
            var id = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "INSERT INTO eval_suite(tenant_id, suite_id, current_revision) VALUES(@tenantId, @suiteId, 1) RETURNING id",
                new { tenantId, suiteId }, tx, cancellationToken: ct));
            await InsertRevisionAsync(conn, tx, id, 1, casesCanonical, sha, caseCount, createdBy, ct);
            await tx.CommitAsync(ct);
            return ToRecord(suiteId, 1, new RevisionRow { CasesCanonical = casesCanonical, CasesSha256 = sha, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow });
        }

        var latestSha = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT cases_sha256 FROM eval_suite_revision WHERE suite_id=@id AND revision=@revision",
            new { suite.Id, revision = suite.CurrentRevision }, tx, cancellationToken: ct));
        if (string.Equals(latestSha, sha, StringComparison.Ordinal))
        {
            // Identical content republished: idempotent no-op, no new revision.
            var existing = await conn.QuerySingleAsync<RevisionRow>(new CommandDefinition(
                "SELECT cases_canonical AS CasesCanonical, cases_sha256 AS CasesSha256,"
                + " created_by AS CreatedBy, created_at AS CreatedAt"
                + " FROM eval_suite_revision WHERE suite_id=@id AND revision=@revision",
                new { suite.Id, revision = suite.CurrentRevision }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return ToRecord(suiteId, suite.CurrentRevision, existing);
        }

        var nextRevision = suite.CurrentRevision + 1;
        await InsertRevisionAsync(conn, tx, suite.Id, nextRevision, casesCanonical, sha, caseCount, createdBy, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE eval_suite SET current_revision=@nextRevision, updated_at=now() WHERE id=@id",
            new { suite.Id, nextRevision }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return ToRecord(suiteId, nextRevision, new RevisionRow { CasesCanonical = casesCanonical, CasesSha256 = sha, CreatedBy = createdBy, CreatedAt = DateTime.UtcNow });
    }

    private static Task InsertRevisionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid suiteId, int revision,
        string casesCanonical, string sha, int caseCount, string createdBy, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO eval_suite_revision(suite_id, revision, cases_canonical, cases_sha256, case_count, created_by)"
            + " VALUES(@suiteId, @revision, @casesCanonical, @sha, @caseCount, @createdBy)",
            new { suiteId, revision, casesCanonical, sha, caseCount, createdBy }, tx, cancellationToken: ct));

    public async Task<EvalRunWriteResult> CreateRunAsync(
        string tenantId, string idempotencyKeySha256, EvalRunWrite run, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var insertedId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "INSERT INTO eval_run(id, tenant_id, suite_id, suite_revision, candidate_kind, candidate_ref,"
            + " candidate_pins, candidate_identity_sha256, required_case_ids, freshness_seconds,"
            + " runner_version, started_at, completed_at, actor_id, idempotency_key_sha256)"
            + " VALUES(@Id, @tenantId, @SuiteId, @SuiteRevision, @CandidateKind, @CandidateRefJson::jsonb,"
            + " @CandidatePinsJson::jsonb, @CandidateIdentitySha256, @RequiredCaseIds, @FreshnessSeconds,"
            + " @RunnerVersion, @StartedAt, @CompletedAt, @ActorId, @idempotencyKeySha256)"
            + " ON CONFLICT(tenant_id, idempotency_key_sha256) DO NOTHING RETURNING id",
            new
            {
                run.Id, tenantId, run.SuiteId, run.SuiteRevision, run.CandidateKind, run.CandidateRefJson,
                run.CandidatePinsJson, run.CandidateIdentitySha256, RequiredCaseIds = run.RequiredCaseIds.ToArray(),
                run.FreshnessSeconds, run.RunnerVersion, run.StartedAt, run.CompletedAt, run.ActorId, idempotencyKeySha256,
            }, tx, cancellationToken: ct));

        if (insertedId is null)
        {
            var existing = await ReadRunAsync(conn, tx, tenantId, IdempotencyKeyWhere, new { tenantId, idempotencyKeySha256 }, ct);
            await tx.CommitAsync(ct);
            return existing is not null
                && string.Equals(existing.Run.SuiteId, run.SuiteId, StringComparison.Ordinal)
                && existing.Run.SuiteRevision == run.SuiteRevision
                && string.Equals(existing.Run.CandidateIdentitySha256, run.CandidateIdentitySha256, StringComparison.Ordinal)
                ? new EvalRunWriteResult(EvalRunWriteStatus.Replay, existing)
                : new EvalRunWriteResult(EvalRunWriteStatus.Conflict, existing);
        }

        // Case rows: a small, per-suite bounded set (tens of cases) -- one insert per row inside the
        // same transaction is simple and correct; a multi-row VALUES batch is not worth the added
        // complexity at this volume (unlike the bulk rag_chunks writer).
        foreach (var c in run.Cases)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO eval_case_result(run_id, case_id, canonical_identity, verdict, metrics, failure_reason)"
                + " VALUES(@runId, @CaseId, @CanonicalIdentity, @Verdict, @MetricsJson::jsonb, @FailureReason)",
                new { runId = run.Id, c.CaseId, c.CanonicalIdentity, c.Verdict, c.MetricsJson, c.FailureReason },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        var created = await GetRunAsync(tenantId, run.Id, ct);
        return new EvalRunWriteResult(EvalRunWriteStatus.Created, created);
    }

    public async Task<EvalRunDetail?> GetRunAsync(string tenantId, Guid runId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await ReadRunAsync(conn, null, tenantId, "id=@runId", new { tenantId, runId }, ct);
    }

    public async Task<EvalRunDetail?> GetRunByIdempotencyKeyAsync(string tenantId, string idempotencyKeySha256, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await ReadRunAsync(conn, null, tenantId, IdempotencyKeyWhere, new { tenantId, idempotencyKeySha256 }, ct);
    }

    private const string IdempotencyKeyWhere = "idempotency_key_sha256=@idempotencyKeySha256";

    public async Task<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition(
            SummarySelect + " WHERE r.tenant_id=@tenantId ORDER BY r.started_at DESC, r.id DESC",
            new { tenantId }, cancellationToken: ct));
        return rows.Select(ToSummary).ToList();
    }

    public async Task<EvalGateEvaluation?> EvaluateGateAsync(
        string tenantId, Guid runId, string expectedSuiteId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<GateRow>(new CommandDefinition(
            "SELECT suite_id AS SuiteId, required_case_ids AS RequiredCaseIds,"
            + " freshness_seconds AS FreshnessSeconds, completed_at AS CompletedAt"
            + " FROM eval_run WHERE tenant_id=@tenantId AND id=@runId",
            new { tenantId, runId }, cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var passingCaseIds = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT case_id FROM eval_case_result WHERE run_id=@runId AND verdict='PASS'",
            new { runId }, cancellationToken: ct))).ToHashSet(StringComparer.Ordinal);
        var failOrErrorCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM eval_case_result WHERE run_id=@runId AND verdict IN ('FAIL','ERROR')",
            new { runId }, cancellationToken: ct));

        var suiteMatches = string.Equals(row.SuiteId, expectedSuiteId, StringComparison.Ordinal);
        var requiredCaseIds = row.RequiredCaseIds ?? Array.Empty<string>();
        var missing = requiredCaseIds.Where(id => !passingCaseIds.Contains(id)).ToList();
        var fresh = row.FreshnessSeconds is not long limit
            || (DateTime.UtcNow - row.CompletedAt).TotalSeconds <= limit;

        // An empty required_case_ids list is a suite authoring gap, not a green light: without it,
        // "missing.Count==0" is vacuously true and the gate would pass on zero actual coverage.
        // Likewise a run with any FAIL/ERROR verdict elsewhere in the suite must never pass just
        // because the caller's specific required subset happened to succeed.
        var passed = suiteMatches && fresh && requiredCaseIds.Length > 0 && missing.Count == 0 && failOrErrorCount == 0;
        return new EvalGateEvaluation(passed, suiteMatches, fresh, missing);
    }

    private const string SummarySelect =
        "SELECT r.id AS Id, r.suite_id AS SuiteId, r.suite_revision AS SuiteRevision, r.candidate_kind AS CandidateKind,"
        + " r.candidate_ref::text AS CandidateRefJson, r.candidate_pins::text AS CandidatePinsJson,"
        + " r.candidate_identity_sha256 AS CandidateIdentitySha256, r.runner_version AS RunnerVersion,"
        + " r.started_at AS StartedAt, r.completed_at AS CompletedAt,"
        + " COALESCE((SELECT count(*) FROM eval_case_result c WHERE c.run_id=r.id AND c.verdict='PASS'),0)::int AS PassCount,"
        + " COALESCE((SELECT count(*) FROM eval_case_result c WHERE c.run_id=r.id AND c.verdict='FAIL'),0)::int AS FailCount,"
        + " COALESCE((SELECT count(*) FROM eval_case_result c WHERE c.run_id=r.id AND c.verdict='ERROR'),0)::int AS ErrorCount"
        + " FROM eval_run r";

    private static async Task<EvalRunDetail?> ReadRunAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string tenantId, string whereExtra, object parameters, CancellationToken ct)
    {
        var summary = await conn.QuerySingleOrDefaultAsync<SummaryRow>(new CommandDefinition(
            SummarySelect + $" WHERE r.tenant_id=@tenantId AND {whereExtra}",
            parameters, tx, cancellationToken: ct));
        if (summary is null)
        {
            return null;
        }

        var cases = await conn.QueryAsync<EvalCaseResultRecord>(new CommandDefinition(
            "SELECT case_id AS CaseId, canonical_identity AS CanonicalIdentity, verdict AS Verdict,"
            + " metrics::text AS MetricsJson, failure_reason AS FailureReason"
            + " FROM eval_case_result WHERE run_id=@Id ORDER BY case_id",
            new { summary.Id }, tx, cancellationToken: ct));
        return new EvalRunDetail(ToSummary(summary), cases.AsList());
    }

    private static EvalRunSummary ToSummary(SummaryRow r) => new(
        r.Id, r.SuiteId, r.SuiteRevision, r.CandidateKind, r.CandidateRefJson, r.CandidatePinsJson,
        r.CandidateIdentitySha256, r.RunnerVersion, r.StartedAt, r.CompletedAt, r.PassCount, r.FailCount, r.ErrorCount);

    private static EvalSuiteRevisionRecord ToRecord(string suiteId, int revision, RevisionRow row)
    {
        var content = EvalSuiteCodec.Parse(row.CasesCanonical);
        return new EvalSuiteRevisionRecord(
            suiteId, revision, row.CasesCanonical, row.CasesSha256, EvalSuiteCodec.CaseCount(content.Cases),
            content.Policy?.RequiredCaseIds ?? Array.Empty<string>(), content.Policy?.FreshnessSeconds,
            row.CreatedBy, row.CreatedAt);
    }

    private static string SkillHashOf(string text) => Skills.SkillHash.Sha256(text);

    private sealed class SuiteRow { public Guid Id { get; init; } public int CurrentRevision { get; init; } }
    private sealed class RevisionRow { public string CasesCanonical { get; init; } = ""; public string CasesSha256 { get; init; } = ""; public string CreatedBy { get; init; } = ""; public DateTime CreatedAt { get; init; } }
    private sealed class GateRow { public string SuiteId { get; init; } = ""; public string[]? RequiredCaseIds { get; init; } public long? FreshnessSeconds { get; init; } public DateTime CompletedAt { get; init; } }
    private sealed class SummaryRow
    {
        public Guid Id { get; init; }
        public string SuiteId { get; init; } = "";
        public int SuiteRevision { get; init; }
        public string CandidateKind { get; init; } = "";
        public string CandidateRefJson { get; init; } = "";
        public string? CandidatePinsJson { get; init; }
        public string CandidateIdentitySha256 { get; init; } = "";
        public string RunnerVersion { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public DateTime CompletedAt { get; init; }
        public int PassCount { get; init; }
        public int FailCount { get; init; }
        public int ErrorCount { get; init; }
    }
}
