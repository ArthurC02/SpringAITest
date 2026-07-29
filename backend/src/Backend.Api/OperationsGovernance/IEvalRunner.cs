using System.Text.Json;

namespace Backend.Api.OperationsGovernance;

/// <summary>
/// Executes a versioned eval suite against a candidate. The only implementation calls Workflow's
/// POST /evals/run (workflow-internal, no /api prefix); a hand-written fake replaces this in tests.
/// </summary>
public interface IEvalRunner
{
    Task<EvalRunResponseWire> RunAsync(
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
        CancellationToken ct);
}
