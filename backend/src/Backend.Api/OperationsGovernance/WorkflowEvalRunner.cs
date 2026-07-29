using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.OperationsGovernance;

/// <summary>
/// Calls Workflow's POST /evals/run (workflow-internal, no /api prefix; 404 while its own eval flag
/// is off). Any non-2xx, unreachable or malformed response is a runner failure, not a candidate
/// failure -- backend must never durably record eval results computed from a half-understood
/// response, so every such case maps to 502 (same posture as WorkflowSkillValidator).
/// </summary>
public sealed class WorkflowEvalRunner : IEvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _internalToken;

    public WorkflowEvalRunner(HttpClient http, string baseUrl, string internalToken)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _internalToken = internalToken;
    }

    public async Task<EvalRunResponseWire> RunAsync(
        string suiteId,
        int revision,
        JsonElement cases,
        string candidateKind,
        JsonElement candidateRef,
        JsonElement? candidatePins,
        int? budgetMs,
        string tenantId,
        string? userId,
        string? role,
        CancellationToken ct)
    {
        var payload = new EvalRunPayload(
            new EvalSuitePayload(suiteId, revision, cases),
            new EvalCandidatePayload(candidateKind, candidateRef, candidatePins),
            new EvalRunnerPayload(budgetMs));

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/evals/run")
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        request.UseInternalIdentity(_internalToken, tenantId, userId, role);

        var body = await _http.SendJsonAsync<EvalRunResponseWire>(request, Failure, JsonOpts, ct);
        if (string.IsNullOrWhiteSpace(body.RunnerVersion) || body.Cases is null)
        {
            throw Failure("回應缺少 runner_version 或 cases");
        }

        return body;
    }

    private static ApiException Failure(string detail)
        => new(StatusCodes.Status502BadGateway, "Eval runner 呼叫失敗：" + detail);

    private sealed record EvalRunPayload(
        [property: JsonPropertyName("suite")] EvalSuitePayload Suite,
        [property: JsonPropertyName("candidate")] EvalCandidatePayload Candidate,
        [property: JsonPropertyName("runner")] EvalRunnerPayload Runner);

    private sealed record EvalSuitePayload(
        [property: JsonPropertyName("suite_id")] string SuiteId,
        [property: JsonPropertyName("revision")] int Revision,
        [property: JsonPropertyName("cases")] JsonElement Cases);

    private sealed record EvalCandidatePayload(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("ref")] JsonElement Ref,
        [property: JsonPropertyName("pins")] JsonElement? Pins);

    private sealed record EvalRunnerPayload(
        [property: JsonPropertyName("budget_ms")] int? BudgetMs);
}
