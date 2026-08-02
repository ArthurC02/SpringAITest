namespace Backend.Api.AgentRuns;

/// <summary>
/// Internal parity seam for Lite mode.  D7 write-evidence must atomically recheck cancellation
/// under the AgentRun repository's own lock instead of trusting an earlier snapshot.  Callers must
/// hold <see cref="SyncRoot"/> before calling <see cref="IsCancelRequested"/>.
/// </summary>
public interface IAgentRunCancellationFence
{
    Lock SyncRoot { get; }

    bool IsCancelRequested(string tenantId, string userId, Guid runId);
}
