using Backend.Api.AgentRuns;
using Backend.Api.Common;
using Backend.Api.OrchestratorRuns;

namespace Backend.Api.RunDiscovery;

/// <summary>
/// Lite-mode parity for <see cref="RunDiscoveryRepository"/>. Both run authorities already
/// implement <see cref="IRunDiscoverySource"/> (mirrors <c>IOrchestratorChildRunRepository</c>'s
/// existing cross-repository seam); this type only merges, filters and paginates their rows the
/// same way the Dapper CTE does, so a change to one side without the other is caught by parity
/// tests rather than silently drifting.
/// </summary>
public sealed class InMemoryRunDiscoveryRepository : IRunDiscoveryRepository
{
    private readonly IRunDiscoverySource _agentRuns;
    private readonly IRunDiscoverySource _orchestratorRuns;
    private readonly TimeProvider _timeProvider;

    public InMemoryRunDiscoveryRepository(
        IAgentRunRepository agentRuns,
        IOrchestratorRunRepository orchestratorRuns,
        TimeProvider? timeProvider = null)
    {
        _agentRuns = agentRuns as IRunDiscoverySource
            ?? throw new ArgumentException("O2 run discovery 需要 IRunDiscoverySource", nameof(agentRuns));
        _orchestratorRuns = orchestratorRuns as IRunDiscoverySource
            ?? throw new ArgumentException("O2 run discovery 需要 IRunDiscoverySource", nameof(orchestratorRuns));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<RunSummaryItem>> ListAsync(
        string tenantId,
        string userId,
        bool callerIsAdmin,
        RunDiscoveryFilter filter,
        RunDiscoveryPosition? position,
        int limit,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        // The orchestrator source has no ADMIN gate on its own detail endpoint (GET
        // /api/orchestrator-runs/{id}); the agent_run-sourced source mirrors AgentRunController's
        // class-level [AdminOnly] and is only consulted for an ADMIN caller — see
        // IRunDiscoveryRepository's contract.
        var items = _orchestratorRuns.ListRunSummaries(tenantId, userId, now);
        if (callerIsAdmin)
        {
            items = items.Concat(_agentRuns.ListRunSummaries(tenantId, userId, now)).ToArray();
        }

        var filtered = items
            .Where(x => filter.Kind is null || x.Kind == filter.Kind)
            .Where(x => filter.Status is null || x.Status == filter.Status)
            .Where(x => filter.AgentId is null || x.AgentId == filter.AgentId)
            .Where(x => filter.OrchestratorId is null || x.OrchestratorId == filter.OrchestratorId)
            .Where(x => filter.CreatedFrom is null || x.CreatedAt >= filter.CreatedFrom)
            .Where(x => filter.CreatedTo is null || x.CreatedAt <= filter.CreatedTo)
            .Where(x => filter.PendingApproval is null || x.PendingApproval == filter.PendingApproval)
            .Where(x => filter.NeedsRecovery is null || x.NeedsRecovery == filter.NeedsRecovery)
            .Where(x => position is null || KeysetCursor.FollowsPosition(x.CreatedAt, x.Id, position.CreatedAt, position.Id))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(limit)
            .ToArray();
        return Task.FromResult<IReadOnlyList<RunSummaryItem>>(filtered);
    }
}
