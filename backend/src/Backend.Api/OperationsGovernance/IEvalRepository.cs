namespace Backend.Api.OperationsGovernance;

/// <summary>
/// Durable eval suite/result authority (E2/E3). Every query is tenant-scoped -- tenant isolation is
/// this interface's contract, exactly like <see cref="IOperationsGovernanceRepository"/>.
/// </summary>
public interface IEvalRepository
{
    Task<IReadOnlyList<EvalSuiteSummary>> ListSuitesAsync(string tenantId, CancellationToken ct);

    /// <summary>Not found (including cross-tenant) → null.</summary>
    Task<EvalSuiteSummary?> GetSuiteAsync(string tenantId, string suiteId, CancellationToken ct);

    Task<IReadOnlyList<EvalSuiteRevisionSummary>> ListSuiteRevisionsAsync(
        string tenantId, string suiteId, CancellationToken ct);

    /// <summary>Not found (including cross-tenant) → null.</summary>
    Task<EvalSuiteRevisionRecord?> GetSuiteRevisionAsync(
        string tenantId, string suiteId, int revision, CancellationToken ct);

    /// <summary>
    /// Publish (idempotent) a suite revision from already-canonicalized cases JSON text. Same
    /// canonical content on an existing suite → returns the existing (unchanged) current revision;
    /// different content → creates and returns the next revision. A revision's bytes never change
    /// once written -- this is the only write path for <c>eval_suite</c>/<c>eval_suite_revision</c>,
    /// used by the CSR-EVAL-001 bootstrap seed (no public authoring API in this phase).
    /// </summary>
    Task<EvalSuiteRevisionRecord> PublishSuiteRevisionAsync(
        string tenantId, string suiteId, string casesCanonical, string createdBy, CancellationToken ct);

    /// <summary>
    /// Write run + case rows atomically, keyed by (tenant, idempotency key hash). Only ever called
    /// after Workflow's call has already succeeded, so no half-written run is ever observable: the
    /// row either does not exist yet (nothing happened) or exists complete with all its case rows.
    /// A replay with a matching (suite_id, suite_revision, candidate identity) returns the existing
    /// run without writing a new row; a replay with a different one is reported as a conflict.
    /// </summary>
    Task<EvalRunWriteResult> CreateRunAsync(
        string tenantId, string idempotencyKeySha256, EvalRunWrite run, CancellationToken ct);

    /// <summary>
    /// Cheap pre-check for the controller to use *before* calling the (potentially minutes-long)
    /// eval runner: a caller can detect a replay/conflict and skip the runner entirely instead of
    /// re-running it only to discard the result in <see cref="CreateRunAsync"/>. Not found → null.
    /// </summary>
    Task<EvalRunDetail?> GetRunByIdempotencyKeyAsync(string tenantId, string idempotencyKeySha256, CancellationToken ct);

    /// <summary>Not found (including cross-tenant) → null.</summary>
    Task<EvalRunDetail?> GetRunAsync(string tenantId, Guid runId, CancellationToken ct);

    Task<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string tenantId, CancellationToken ct);

    /// <summary>
    /// E3 gate computation straight off stored results, never a caller-supplied boolean. Returns
    /// null only when the run itself cannot be found for this tenant (caller error -- the
    /// controller rejects that with 400). <paramref name="expectedSuiteId"/> is the regression
    /// request's own `suite` label, which must name the exact suite this run evaluated.
    /// </summary>
    Task<EvalGateEvaluation?> EvaluateGateAsync(
        string tenantId, Guid runId, string expectedSuiteId, CancellationToken ct);
}
