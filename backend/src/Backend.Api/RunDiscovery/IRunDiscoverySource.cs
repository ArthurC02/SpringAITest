namespace Backend.Api.RunDiscovery;

/// <summary>
/// Lite-mode composition seam (mirrors <c>IOrchestratorChildRunRepository</c>'s existing pattern
/// in <c>AgentRunSnapshotSources.cs</c>): each InMemory run authority projects its own rows into
/// the unified O2 shape, so <see cref="InMemoryRunDiscoveryRepository"/> only merges, filters and
/// paginates — it never reaches into either authority's private state directly.
/// </summary>
internal interface IRunDiscoverySource
{
    /// <summary>Every row this tenant/user owns in this source's table, unfiltered/unpaginated —
    /// callers (here, only <see cref="InMemoryRunDiscoveryRepository"/>) apply O2's filters and
    /// keyset pagination uniformly across both sources.</summary>
    IReadOnlyList<RunSummaryItem> ListRunSummaries(string tenantId, string userId, DateTime now);
}
