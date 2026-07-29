namespace Backend.Api.OperationsGovernance;

/// <summary>
/// E1 kill switch (default off, fail closed). Off: the extended run evidence envelope is never
/// built or written and <c>operations_run_evidence</c> stays empty; the pre-existing
/// <c>operations_execution_metric</c> write path is completely unaffected either way.
/// </summary>
public sealed record RunEvidenceState(bool Enabled);
