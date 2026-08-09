namespace Backend.Api.RunDiscovery;

/// <summary>
/// O2 unified runs/tasks discovery (04-operations-trigger-plan.md §3). Every implementation must
/// return exactly the set of runs the caller could also reach one at a time through the existing
/// detail endpoints — never more:
/// <list type="bullet">
/// <item>direct-agent/worker/verifier kinds live in <c>agent_run</c>, reachable one at a time only
/// through <c>GET /api/runs/{id}</c> (class-level <c>[AdminOnly]</c> + tenant+owner scoped), so
/// <paramref name="callerIsAdmin"/> must gate them out entirely for a non-ADMIN caller.</item>
/// <item>the orchestrator kind lives in <c>orchestrator_run</c>, reachable through
/// <c>GET /api/orchestrator-runs/{id}</c> (tenant+owner scoped, no ADMIN gate).</item>
/// </list>
/// Ordered created_at DESC, id DESC; <paramref name="position"/> is an exclusive keyset cursor in
/// that same order (mirrors the O3 approval queue's cursor semantics).
/// </summary>
public interface IRunDiscoveryRepository
{
    Task<IReadOnlyList<RunSummaryItem>> ListAsync(
        string tenantId,
        string userId,
        bool callerIsAdmin,
        RunDiscoveryFilter filter,
        RunDiscoveryPosition? position,
        int limit,
        CancellationToken ct);
}
