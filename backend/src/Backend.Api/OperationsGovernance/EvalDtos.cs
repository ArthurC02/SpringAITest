using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Api.OperationsGovernance;

// E2/E3 durable eval suite/result authority. Wire contract with Workflow's POST /evals/run is
// snake_case; case `input`/`expected`/`fixtures` are opaque to Backend (Workflow-owned semantics),
// so they are forwarded verbatim as JsonElement rather than modeled field-by-field here.

/// <summary>A stored suite's mutable pointer: which revision is current. Tenant-scoped.</summary>
public sealed record EvalSuiteSummary(
    string SuiteId, int CurrentRevision, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>One immutable suite revision's audit-list row (no case bodies).</summary>
public sealed record EvalSuiteRevisionSummary(
    int Revision, string CasesSha256, int CaseCount, string CreatedBy, DateTime CreatedAt);

/// <summary>
/// A full immutable suite revision. <see cref="CasesCanonical"/> is the canonical JSON text
/// (produced by <c>AgentCanonicalizer.CanonicalizeDefinition</c>, never a second canonicalizer)
/// shaped <c>{"policy":{"required_case_ids":[...],"freshness_seconds":N},"cases":[...]}</c>.
/// <see cref="RequiredCaseIds"/>/<see cref="FreshnessSeconds"/> are pinned onto <c>eval_run</c> at
/// run-creation time so gate evaluation never needs a second join back to this immutable row.
/// </summary>
public sealed record EvalSuiteRevisionRecord(
    string SuiteId,
    int Revision,
    string CasesCanonical,
    string CasesSha256,
    int CaseCount,
    IReadOnlyList<string> RequiredCaseIds,
    long? FreshnessSeconds,
    string CreatedBy,
    DateTime CreatedAt);

/// <summary>Suite content shape parsed out of <see cref="EvalSuiteRevisionRecord.CasesCanonical"/>
/// only far enough to (a) forward `cases` verbatim to Workflow and (b) read the policy fields.</summary>
public sealed record EvalSuiteContent(
    [property: JsonPropertyName("policy")] EvalSuitePolicyWire? Policy,
    [property: JsonPropertyName("cases")] JsonElement Cases);

public sealed record EvalSuitePolicyWire(
    [property: JsonPropertyName("required_case_ids")] IReadOnlyList<string>? RequiredCaseIds,
    [property: JsonPropertyName("freshness_seconds")] long? FreshnessSeconds);

/// <summary>Shared parse/read helpers so the Dapper and in-memory repositories never diverge on how
/// suite content is interpreted -- there is exactly one reader of this shape.</summary>
public static class EvalSuiteCodec
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static EvalSuiteContent Parse(string casesCanonical)
        => JsonSerializer.Deserialize<EvalSuiteContent>(casesCanonical, JsonOpts)
           ?? new EvalSuiteContent(null, default);

    public static int CaseCount(JsonElement cases)
        => cases.ValueKind == JsonValueKind.Array ? cases.GetArrayLength() : 0;
}

/// <summary>Candidate under evaluation, exactly as the caller and Workflow both see it.</summary>
public sealed record EvalCandidateRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("ref")] JsonElement? Ref,
    [property: JsonPropertyName("pins")] JsonElement? Pins);

/// <summary>POST /api/admin/operations/eval-runs request body.</summary>
public sealed record EvalRunRequest(
    [property: JsonPropertyName("suite_id")] string? SuiteId,
    [property: JsonPropertyName("revision")] int? Revision,
    [property: JsonPropertyName("candidate")] EvalCandidateRequest? Candidate,
    [property: JsonPropertyName("budget_ms")] int? BudgetMs);

/// <summary>Workflow's POST /evals/run response (runner_version/suite_id/revision/started_at/
/// completed_at/cases), workflow-internal snake_case per the frozen cross-service contract.</summary>
public sealed record EvalRunResponseWire(
    [property: JsonPropertyName("runner_version")] string? RunnerVersion,
    [property: JsonPropertyName("suite_id")] string? SuiteId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("cases")] IReadOnlyList<EvalCaseResultWire>? Cases);

public sealed record EvalCaseResultWire(
    [property: JsonPropertyName("case_id")] string? CaseId,
    [property: JsonPropertyName("canonical_identity")] string? CanonicalIdentity,
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("metrics")] JsonElement? Metrics,
    [property: JsonPropertyName("failure_reason")] string? FailureReason);

/// <summary>One case result as persisted (metrics kept as raw JSON text -- opaque to Backend).</summary>
public sealed record EvalCaseResultRecord(
    string CaseId, string? CanonicalIdentity, string Verdict, string? MetricsJson, string? FailureReason);

/// <summary>Everything CreateRunAsync must write atomically after a successful Workflow call.</summary>
public sealed record EvalRunWrite(
    Guid Id,
    string SuiteId,
    int SuiteRevision,
    string CandidateKind,
    string CandidateRefJson,
    string? CandidatePinsJson,
    string CandidateIdentitySha256,
    IReadOnlyList<string> RequiredCaseIds,
    long? FreshnessSeconds,
    string RunnerVersion,
    DateTime StartedAt,
    DateTime CompletedAt,
    string ActorId,
    IReadOnlyList<EvalCaseResultRecord> Cases);

public enum EvalRunWriteStatus { Created, Replay, Conflict }

public sealed record EvalRunWriteResult(EvalRunWriteStatus Status, EvalRunDetail? Run);

/// <summary>Public run summary -- includes the candidate/suite identity fields a baseline-vs-
/// candidate comparison view needs, without exposing raw ref/pins bytes beyond what was submitted.</summary>
public sealed record EvalRunSummary(
    Guid Id,
    string SuiteId,
    int SuiteRevision,
    string CandidateKind,
    string CandidateRefJson,
    string? CandidatePinsJson,
    string CandidateIdentitySha256,
    string RunnerVersion,
    DateTime StartedAt,
    DateTime CompletedAt,
    int PassCount,
    int FailCount,
    int ErrorCount);

public sealed record EvalRunDetail(EvalRunSummary Run, IReadOnlyList<EvalCaseResultRecord> Cases);

/// <summary>
/// E3 gate computation result. <see cref="Passed"/> already folds in suite-identity match (the
/// caller's <c>suite</c> label vs. the run's own <c>suite_id</c>), required-case coverage
/// (including empty <c>required_case_ids</c> failing closed), zero FAIL/ERROR verdicts across the
/// whole run, and freshness -- callers needing a reason surface the individual fields; the
/// regression endpoint only stores the folded bool (no new public reason contract).
/// </summary>
public sealed record EvalGateEvaluation(
    bool Passed, bool SuiteMatches, bool Fresh, IReadOnlyList<string> MissingRequiredCases);
