using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Backend.Api.Agents;
using Backend.Api.Common;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;
using static Backend.Api.Common.ApiErrors;

namespace Backend.Api.OperationsGovernance;

/// <summary>
/// E2/E3 durable eval suite/result authority. Same authorization level as
/// <see cref="OperationsGovernanceController"/> (exact <c>workflow.manage</c>); hidden entirely
/// behind <c>RUN_EVAL_ENABLED</c> in Program.cs (fail closed, 404 before auth while off).
/// No suite-authoring endpoint exists in this phase: <c>eval_suite</c>/<c>eval_suite_revision</c>
/// rows only ever come from the CSR-EVAL-001 bootstrap seed (see <see cref="CsrEval001Suite"/>).
/// </summary>
[ApiController, Route("api/admin/operations")]
public sealed class EvalController(IEvalRepository evals, IEvalRunner runner) : ControllerBase
{
    private const int MaxCandidateJsonBytes = 16_384;
    private const int MaxBudgetMs = 300_000; // matches the eval-runner HttpClient timeout (300s)

    [HttpGet("eval-suites")]
    public async Task<IActionResult> ListSuites(CancellationToken ct)
    {
        RequireManage();
        var suites = await evals.ListSuitesAsync(Request.RequireTenant(), ct);
        return Ok(suites.Select(PublicSuite));
    }

    [HttpGet("eval-suites/{suiteId}")]
    public async Task<IActionResult> GetSuite(string suiteId, CancellationToken ct)
    {
        RequireManage();
        var tenant = Request.RequireTenant();
        var suite = await evals.GetSuiteAsync(tenant, suiteId, ct) ?? throw NotFound(suiteId);
        var revisions = await evals.ListSuiteRevisionsAsync(tenant, suiteId, ct);
        return Ok(new
        {
            suite_id = suite.SuiteId,
            current_revision = suite.CurrentRevision,
            created_at = suite.CreatedAt,
            updated_at = suite.UpdatedAt,
            revisions = revisions.Select(PublicRevisionSummary),
        });
    }

    [HttpGet("eval-suites/{suiteId}/revisions/{revision:int}")]
    public async Task<IActionResult> GetSuiteRevision(string suiteId, int revision, CancellationToken ct)
    {
        RequireManage();
        var rev = await evals.GetSuiteRevisionAsync(Request.RequireTenant(), suiteId, revision, ct)
            ?? throw NotFound($"{suiteId}@{revision}");
        var content = JsonNode.Parse(rev.CasesCanonical)!;
        return Ok(new
        {
            suite_id = rev.SuiteId,
            revision = rev.Revision,
            cases_sha256 = rev.CasesSha256,
            case_count = rev.CaseCount,
            policy = content["policy"],
            cases = content["cases"],
            created_by = rev.CreatedBy,
            created_at = rev.CreatedAt,
        });
    }

    [HttpPost("eval-runs")]
    public async Task<IActionResult> CreateRun(EvalRunRequest request, CancellationToken ct)
    {
        RequireManage();
        var tenant = Request.RequireTenant();
        var key = Request.RequireIdempotencyKey();
        var userId = Request.RequireUserId();
        var suiteId = Required(request.SuiteId, "suite_id", 128);
        if (request.Revision is not int revision || revision < 1)
        {
            throw new ApiException(400, "revision is required");
        }
        var candidate = request.Candidate;
        if (candidate?.Kind is not ("skill" or "agent"))
        {
            throw new ApiException(400, "candidate.kind must be skill or agent");
        }
        if (candidate.Ref is not { ValueKind: JsonValueKind.Object } candidateRef
            || JsonBytes(candidateRef) > MaxCandidateJsonBytes)
        {
            throw new ApiException(400, "candidate.ref must be a JSON object");
        }
        if (candidate.Pins is { ValueKind: not JsonValueKind.Object } || candidate.Pins is JsonElement pinsEl && JsonBytes(pinsEl) > MaxCandidateJsonBytes)
        {
            throw new ApiException(400, "candidate.pins must be a JSON object");
        }
        if (request.BudgetMs is int budget && (budget <= 0 || budget > MaxBudgetMs))
        {
            throw new ApiException(400, $"budget_ms must be between 1 and {MaxBudgetMs}");
        }
        // Omitted budget_ms must not mean "unbounded": a 200-case suite would otherwise always run
        // into the eval-runner HttpClient's own 300s timeout and the whole run is discarded. Default
        // to the same cap MaxBudgetMs already enforces above.
        var budgetMs = request.BudgetMs ?? MaxBudgetMs;

        var suiteRevision = await evals.GetSuiteRevisionAsync(tenant, suiteId, revision, ct)
            ?? throw ApiErrors.NotFound(" Eval Suite revision", $"{suiteId}@{revision}");

        var candidateRefJson = candidateRef.GetRawText();
        var candidatePinsJson = candidate.Pins?.GetRawText();
        var candidateIdentitySha256 = SkillHash.Sha256(AgentCanonicalizer.CanonicalizeDefinition(
            new JsonObject
            {
                ["kind"] = candidate.Kind,
                ["ref"] = JsonNode.Parse(candidateRefJson),
                ["pins"] = candidatePinsJson is null ? null : JsonNode.Parse(candidatePinsJson),
            }.ToJsonString()));

        // Idempotency is checked *before* the (potentially minutes-long) runner call: a replay or a
        // conflicting reuse of the same key is resolved here without ever invoking Workflow again.
        var idempotencyKeySha256 = SkillHash.Sha256(key);
        var existing = await evals.GetRunByIdempotencyKeyAsync(tenant, idempotencyKeySha256, ct);
        if (existing is not null)
        {
            var matches = string.Equals(existing.Run.SuiteId, suiteId, StringComparison.Ordinal)
                && existing.Run.SuiteRevision == revision
                && string.Equals(existing.Run.CandidateIdentitySha256, candidateIdentitySha256, StringComparison.Ordinal);
            return matches
                ? Ok(PublicDetail(existing))
                : throw new ApiException(409, "Idempotency-Key 已用於不同的 eval run 請求");
        }

        var suiteContent = EvalSuiteCodec.Parse(suiteRevision.CasesCanonical);
        var response = await runner.RunAsync(
            suiteId, revision, suiteContent.Cases, candidate.Kind, candidateRef, candidate.Pins,
            budgetMs, tenant, Request.UserId(), Request.UserRole(), ct);

        if (!string.Equals(response.SuiteId, suiteId, StringComparison.Ordinal) || response.Revision != revision)
        {
            throw new ApiException(502, "Eval runner 回應違反契約（suite_id/revision 與請求不符）");
        }

        var cases = (response.Cases ?? Array.Empty<EvalCaseResultWire>()).Select(c =>
        {
            if (string.IsNullOrWhiteSpace(c.CaseId) || c.Verdict is not ("PASS" or "FAIL" or "ERROR"))
            {
                throw new ApiException(502, "Eval runner 回應違反契約（case 結果形狀不合法）");
            }
            return new EvalCaseResultRecord(c.CaseId!, c.CanonicalIdentity, c.Verdict!, c.Metrics?.GetRawText(), c.FailureReason);
        }).ToList();

        var write = new EvalRunWrite(
            Guid.NewGuid(), suiteId, revision, candidate.Kind, candidateRefJson, candidatePinsJson,
            candidateIdentitySha256, suiteRevision.RequiredCaseIds, suiteRevision.FreshnessSeconds,
            response.RunnerVersion!, response.StartedAt.UtcDateTime, response.CompletedAt.UtcDateTime,
            userId, cases);

        // A concurrent request for the same key can still race past the pre-check above; the
        // ON CONFLICT DO NOTHING write below is the authoritative, race-safe idempotency guard.
        var result = await evals.CreateRunAsync(tenant, idempotencyKeySha256, write, ct);
        return result.Status switch
        {
            EvalRunWriteStatus.Created or EvalRunWriteStatus.Replay => Ok(PublicDetail(result.Run!)),
            _ => throw new ApiException(409, "Idempotency-Key 已用於不同的 eval run 請求"),
        };
    }

    [HttpGet("eval-runs")]
    public async Task<IActionResult> ListRuns(CancellationToken ct)
    {
        RequireManage();
        var runs = await evals.ListRunsAsync(Request.RequireTenant(), ct);
        return Ok(runs.Select(PublicSummary));
    }

    [HttpGet("eval-runs/{runId:guid}")]
    public async Task<IActionResult> GetRun(Guid runId, CancellationToken ct)
    {
        RequireManage();
        var run = await evals.GetRunAsync(Request.RequireTenant(), runId, ct) ?? throw NotFoundRun(runId.ToString("D"));
        return Ok(PublicDetail(run));
    }

    private void RequireManage() => Request.RequireCapability("workflow.manage");

    private static int JsonBytes(JsonElement value) => System.Text.Encoding.UTF8.GetByteCount(value.GetRawText());

    private static ApiException NotFound(string id) => ApiErrors.NotFound(" Eval Suite", id);

    private static ApiException NotFoundRun(string id) => ApiErrors.NotFound(" Eval Run", id);

    private static object PublicSuite(EvalSuiteSummary s) => new
    {
        suite_id = s.SuiteId, current_revision = s.CurrentRevision, created_at = s.CreatedAt, updated_at = s.UpdatedAt,
    };

    private static object PublicRevisionSummary(EvalSuiteRevisionSummary r) => new
    {
        revision = r.Revision, cases_sha256 = r.CasesSha256, case_count = r.CaseCount,
        created_by = r.CreatedBy, created_at = r.CreatedAt,
    };

    private static EvalRunResponse PublicSummary(EvalRunSummary r) => new(
        r.Id, r.SuiteId, r.SuiteRevision,
        new EvalRunCandidateResponse(r.CandidateKind, r.CandidateRefJson, r.CandidatePinsJson, r.CandidateIdentitySha256),
        r.RunnerVersion, r.StartedAt, r.CompletedAt, r.PassCount, r.FailCount, r.ErrorCount, null);

    private static EvalRunResponse PublicDetail(EvalRunDetail d) => PublicSummary(d.Run) with
    {
        Cases = d.Cases.Select(c => new EvalCaseResultResponse(c.CaseId, c.CanonicalIdentity, c.Verdict, c.MetricsJson, c.FailureReason)).ToList(),
    };
}

/// <summary>Ref/pins are stored as <c>jsonb</c> (candidate_ref/candidate_pins), so what comes back is
/// the same JSON content *normalized* by Postgres -- not the caller's original bytes (key order,
/// whitespace and number formatting are not preserved) -- so a future baseline-vs-candidate view can
/// still render *what* differs, not just *whether* it differs (identity_sha256 alone).</summary>
public sealed record EvalRunCandidateResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ref"), JsonConverter(typeof(RawJsonConverter))] string Ref,
    [property: JsonPropertyName("pins")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? Pins,
    [property: JsonPropertyName("identity_sha256")] string IdentitySha256);

public sealed record EvalCaseResultResponse(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("canonical_identity")] string? CanonicalIdentity,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("metrics")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? Metrics,
    [property: JsonPropertyName("failure_reason")] string? FailureReason);

public sealed record EvalRunResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("suite_id")] string SuiteId,
    [property: JsonPropertyName("suite_revision")] int SuiteRevision,
    [property: JsonPropertyName("candidate")] EvalRunCandidateResponse Candidate,
    [property: JsonPropertyName("runner_version")] string RunnerVersion,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("completed_at")] DateTime CompletedAt,
    [property: JsonPropertyName("pass_count")] int PassCount,
    [property: JsonPropertyName("fail_count")] int FailCount,
    [property: JsonPropertyName("error_count")] int ErrorCount,
    [property: JsonPropertyName("cases")] IReadOnlyList<EvalCaseResultResponse>? Cases);
