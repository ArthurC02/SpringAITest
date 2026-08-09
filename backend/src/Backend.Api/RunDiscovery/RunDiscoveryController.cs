using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.RunDiscovery;

/// <summary>
/// O2 "Unified Runs and Tasks center" (04-operations-trigger-plan.md §3). Gated by
/// <c>RUN_DISCOVERY_ENABLED</c> (Program.cs's pre-auth 404, default off) and — per §8's "tenant +
/// exact capability" rollout strategy — the same exact <c>workflow.manage</c> capability D4/D5/D7's
/// admin surfaces already require (this repo has no other capability, and admin-experience-downshift
/// §8 explicitly rules out adding one). Visibility beyond that gate is the same minimal, owner-scoped
/// principle O3's queue established: every returned row is something this exact caller could also
/// fetch one at a time through the existing per-run detail endpoint — never a teammate's run.
/// </summary>
[ApiController]
[Route("api/runs")]
public sealed class RunDiscoveryController(IRunDiscoveryRepository runs, RunDiscoveryState state) : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    [HttpGet]
    public async Task<ActionResult<RunDiscoveryPage>> List(
        [FromQuery] string? kind = null,
        [FromQuery] string? status = null,
        [FromQuery] string? owner = null,
        [FromQuery(Name = "agent_id")] Guid? agentId = null,
        [FromQuery(Name = "orchestrator_id")] Guid? orchestratorId = null,
        [FromQuery(Name = "created_from")] DateTime? createdFrom = null,
        [FromQuery(Name = "created_to")] DateTime? createdTo = null,
        [FromQuery(Name = "pending_approval")] bool? pendingApproval = null,
        [FromQuery(Name = "needs_recovery")] bool? needsRecovery = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = DefaultPageSize,
        CancellationToken ct = default)
    {
        RequireEnabled();
        Request.RequireCapability("workflow.manage");
        var tenantId = Request.RequireTenant();
        var userId = Request.RequireUserId();

        if (limit is < 1 or > MaxPageSize)
        {
            throw new ApiException(400, "limit 必須介於 1 到 100");
        }
        if (kind is not null && !RunDiscoveryKinds.All.Contains(kind))
        {
            throw new ApiException(400, "kind 無效");
        }
        if (status is not null && !RunDiscoveryStatuses.All.Contains(status))
        {
            throw new ApiException(400, "status 無效");
        }
        if (createdFrom is not null && createdTo is not null && createdFrom > createdTo)
        {
            throw new ApiException(400, "created_from 不可晚於 created_to");
        }

        // "owner" narrows an already owner-scoped result set (see the class doc): a caller can
        // never be shown any other owner's run, so a foreign owner value is indistinguishable from
        // "nothing matched" rather than a distinct error — the same posture O2 §3 requires for
        // cross-tenant queries.
        if (owner is not null && !string.Equals(owner, userId, StringComparison.Ordinal))
        {
            return Ok(new RunDiscoveryPage(Array.Empty<RunSummaryItem>(), null, false));
        }

        var position = cursor is null
            ? null
            : KeysetCursor.Decode(cursor, (createdAt, id) => new RunDiscoveryPosition(createdAt, id));
        var filter = new RunDiscoveryFilter(
            kind, status, agentId, orchestratorId, createdFrom, createdTo, pendingApproval, needsRecovery);
        var rows = await runs.ListAsync(
            tenantId, userId, string.Equals(Request.UserRole(), "ADMIN", StringComparison.Ordinal),
            filter, position, limit + 1, ct);
        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.Take(limit).ToArray() : rows;
        var nextCursor = hasMore ? KeysetCursor.Encode(items[^1].CreatedAt, items[^1].Id) : null;
        return Ok(new RunDiscoveryPage(items, nextCursor, hasMore));
    }

    // Defense in depth: unreachable while Program.cs's RUN_DISCOVERY_ENABLED middleware stands in
    // front of this route, kept so a future routing change cannot expose it — same posture as
    // AgentRunApprovalController.RequireEnabled and the same fixed "找不到資源" message (a
    // gate-specific string would leak "there's a disabled feature here").
    private void RequireEnabled()
    {
        if (!state.Enabled)
        {
            throw new ApiException(404, "找不到資源");
        }
    }
}
