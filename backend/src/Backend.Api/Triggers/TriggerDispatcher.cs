using Backend.Api.Auth;
using Backend.Api.Orchestrators;
using Backend.Api.OrchestratorRuns;

namespace Backend.Api.Triggers;

/// <summary>
/// O5 fire path (04-operations-trigger-plan.md §6.1): claim a due occurrence, run every fail-closed
/// check against the pinned revision and the immutable grant snapshot, then call the <b>existing</b>
/// D5 root creation command — there is deliberately no second run-creation path, so a triggered root
/// is byte-for-byte the same immutable snapshot an operator-started root would be.
///
/// Every rejection is a bounded reason code on the occurrence; a raw downstream message is never
/// persisted. An unexpected exception intentionally leaves the occurrence claimed: the lease simply
/// expires and the next poll retries it, which is strictly safer than burning the one-shot.
/// </summary>
public sealed class TriggerDispatcher(
    ITriggerRepository triggers,
    IOrchestratorRepository orchestrators,
    IOrchestratorRunRepository runs,
    IAuthRepository users,
    AgentTriggersState state,
    ILogger<TriggerDispatcher> logger)
{
    public const int LeaseSeconds = 60;
    public const int BatchLimit = 20;

    private static readonly string WorkerId = "trigger-dispatcher-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Claims and resolves every occurrence the store considers due; returns how many were claimed.
    /// "Now" is never supplied by this process — the repository returns the authoritative instant it
    /// cut the leases from, and every decision below is made against that instant.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var batch = await triggers.ClaimDueAsync(WorkerId, LeaseSeconds, BatchLimit, ct);
        var claims = batch.Claims;
        foreach (var claim in claims)
        {
            try
            {
                var (status, rootRunId) = await ResolveAsync(claim, batch.Now, ct);
                await triggers.CompleteOccurrenceAsync(
                    claim.Occurrence.Id, claim.ClaimToken, status, rootRunId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left claimed on purpose: the lease expires and the occurrence is retried. One bad
                // claim must not abort the rest of the batch either.
                logger.LogError(ex, "Trigger occurrence {Occurrence} failed to dispatch", claim.Occurrence.Id);
            }
        }

        return claims.Count;
    }

    private async Task<(string Status, Guid? RootRunId)> ResolveAsync(
        TriggerClaim claim, DateTime now, CancellationToken ct)
    {
        var trigger = claim.Trigger;
        if (trigger.Status == TriggerStatuses.Cancelled)
        {
            return (TriggerOccurrenceStatuses.SkippedCancelled, null);
        }
        // Misfire is "later than the window", so an occurrence resolved exactly at the boundary
        // still fires. Nothing is ever back-filled: a missed one-shot stays missed (§6.2).
        if (now - claim.Occurrence.ScheduledFor > TimeSpan.FromSeconds(trigger.MisfireWindowSeconds))
        {
            return (TriggerOccurrenceStatuses.SkippedMisfired, null);
        }
        if (!state.Enabled || !state.DispatchEnabled)
        {
            return (TriggerOccurrenceStatuses.FailedDispatchDisabled, null);
        }

        // The snapshot principal must still be a live account of the same tenant. Its grants are
        // never re-read: the snapshot's role/groups/capabilities are what the root run is built
        // with, so a triggered run can never hold authority the creator did not have. D7's
        // separation of duties therefore applies to it exactly as to a hand-started run.
        var principal = trigger.Principal;
        var account = await users.FindUserByUsernameAsync(principal.UserId, ct);
        if (account is null || !string.Equals(account.TenantCode, trigger.TenantId, StringComparison.Ordinal))
        {
            return (TriggerOccurrenceStatuses.FailedPrincipalUnavailable, null);
        }

        // Tenant-scoped lookup: a cross-tenant target is invisible and reads as missing.
        var orchestrator = await orchestrators.GetAsync(trigger.TenantId, trigger.OrchestratorId, ct);
        if (orchestrator is null)
        {
            return (TriggerOccurrenceStatuses.FailedTargetMissing, null);
        }
        if (!orchestrator.Enabled || orchestrator.PublishedRevision is not int published)
        {
            return (TriggerOccurrenceStatuses.FailedTargetUnpublished, null);
        }
        if (published != trigger.OrchestratorRevision)
        {
            return (TriggerOccurrenceStatuses.FailedTargetRevisionChanged, null);
        }

        var result = await runs.CreateAsync(
            trigger.TenantId,
            principal.UserId,
            principal.Role,
            principal.Groups,
            principal.Capabilities,
            trigger.OrchestratorId,
            ConversationId(claim.Occurrence.Id),
            TriggerInputMapping.Message(trigger.InputMappingCanonical),
            IdempotencyKey(claim.Occurrence.Id),
            ct);
        // Replay is the at-most-once guarantee in action: a worker that died after creating the run
        // but before recording the outcome re-derives the same key and gets back the same root.
        return result.Status is OrchestratorRunWriteStatus.Success or OrchestratorRunWriteStatus.Replay
            ? (TriggerOccurrenceStatuses.Fired, result.Run!.Id)
            : (TriggerOccurrenceStatuses.FailedRootRejected, null);
    }

    /// <summary>Server-owned, occurrence-derived; never taken from the caller-supplied mapping.</summary>
    public static string ConversationId(Guid occurrenceId) => "trigger-" + occurrenceId.ToString("N");

    /// <summary>Stable per occurrence, which is what makes a retried fire idempotent at D5.</summary>
    public static string IdempotencyKey(Guid occurrenceId) => "trigger:" + occurrenceId.ToString("N");
}

/// <summary>
/// Polling host for <see cref="TriggerDispatcher"/>, following <c>DocumentConsumerService</c>'s
/// conventions (own scope per pass, never crashes the host).
///
/// ponytail: a plain poll loop instead of a scheduler library — §10 explicitly rules out replacing
/// workers with a scheduler. The trade-off is up to one interval of firing latency; shrink
/// <paramref name="pollInterval"/> if that ever matters, and only reach for a real scheduler when
/// recurring cron (deliberately out of scope here) arrives.
/// </summary>
public sealed class TriggerDispatchService(
    IServiceScopeFactory scopes, ILogger<TriggerDispatchService> logger, TimeSpan pollInterval)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TriggerDispatcher>()
                    .RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trigger dispatch pass failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
