using Backend.Api.Common;
using Backend.Api.Orchestrators;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Triggers;

/// <summary>
/// O5 one-shot durable triggers (04-operations-trigger-plan.md §6). Gated by
/// <c>AGENT_TRIGGERS_ENABLED</c> (Program.cs's pre-auth 404, default off) and the exact
/// <c>workflow.manage</c> capability D4/D5/O2's admin surfaces already require — ADMIN alone never
/// grants access. Deliberately no update route: changing a one-shot schedule is cancel + recreate,
/// which keeps the pinned target/principal snapshot immutable for the whole life of a trigger.
/// </summary>
[ApiController]
[Route("api/admin/triggers")]
public sealed class TriggerController(
    ITriggerRepository triggers, IOrchestratorRepository orchestrators, AgentTriggersState state)
    : ControllerBase
{
    private const int MaxListSize = 200;
    private const int DefaultOccurrencePageSize = 20;
    private const int MaxOccurrencePageSize = 100;
    private const int DefaultMisfireWindowSeconds = 300;
    private const int MaxMisfireWindowSeconds = 86400;
    private const int MaxNameLength = 128;
    private const int MaxDescriptionLength = 512;

    /// <summary>W2-03: how far ahead a one-shot <c>fire_at</c> may be scheduled. See <see cref="FireAt"/>.</summary>
    private const int MaxFireAtHorizonDays = 90;

    [HttpPost]
    public async Task<IActionResult> Create(TriggerCreateRequest request, CancellationToken ct)
    {
        var (tenantId, principal) = Caller();
        var input = new TriggerCreateInput(
            Name(request.Name),
            Description(request.Description),
            request.OrchestratorId,
            request.OrchestratorRevision,
            TriggerInputMapping.Validate(request.InputMapping),
            FireAt(request.FireAt),
            MisfireWindow(request.MisfireWindowSeconds));

        // The pin is validated against the live registry before it is frozen: creation may only
        // reference an enabled, published Orchestrator of this tenant at its current published
        // revision. A cross-tenant target is simply invisible here, so it is indistinguishable
        // from a nonexistent one.
        var orchestrator = await orchestrators.GetAsync(tenantId, input.OrchestratorId, ct)
            ?? throw new ApiException(404, "找不到 Orchestrator");
        if (!orchestrator.Enabled || orchestrator.PublishedRevision is not int published)
        {
            throw new ApiException(409, "Orchestrator must be enabled and published");
        }
        if (published != input.OrchestratorRevision)
        {
            throw new ApiException(409, "orchestrator_revision must pin the current published revision");
        }

        var result = await triggers.CreateAsync(tenantId, principal, input, ct);
        if (result.Status == TriggerWriteStatus.Duplicate)
        {
            throw new ApiException(409, "Trigger name already exists");
        }

        return Written(result.Trigger!, StatusCodes.Status201Created);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (tenantId, _) = Caller();
        var items = await triggers.ListAsync(tenantId, MaxListSize, ct);
        return Ok(new TriggerListResponse(items.Select(TriggerResponse.From).ToArray()));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var (tenantId, _) = Caller();
        var trigger = await triggers.GetAsync(tenantId, id, ct) ?? throw Missing();
        return Written(trigger, StatusCodes.Status200OK);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (tenantId, _) = Caller();
        var result = await triggers.CancelAsync(tenantId, id, Request.RequireIfMatchVersion(), ct);
        return result.Status switch
        {
            TriggerWriteStatus.Success => Written(result.Trigger!, StatusCodes.Status200OK),
            TriggerWriteStatus.NotFound => throw Missing(),
            TriggerWriteStatus.InvalidState => throw new ApiException(409, "Trigger is no longer scheduled"),
            _ => throw new ApiException(409, "Trigger version is stale")
            {
                FieldErrors = new Dictionary<string, string> { ["If-Match"] = "Trigger version is stale" },
            },
        };
    }

    [HttpGet("{id:guid}/occurrences")]
    public async Task<IActionResult> Occurrences(
        Guid id,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = DefaultOccurrencePageSize,
        CancellationToken ct = default)
    {
        var (tenantId, _) = Caller();
        if (limit is < 1 or > MaxOccurrencePageSize)
        {
            throw new ApiException(400, "limit 必須介於 1 到 100");
        }

        var position = cursor is null
            ? null
            : KeysetCursor.Decode(cursor, (scheduledFor, cursorId) => new TriggerOccurrencePosition(scheduledFor, cursorId));
        var rows = await triggers.OccurrencesAsync(tenantId, id, position, limit + 1, ct) ?? throw Missing();
        var hasMore = rows.Count > limit;
        var items = (hasMore ? rows.Take(limit) : rows).Select(TriggerOccurrenceResponse.From).ToArray();
        var nextCursor = hasMore
            ? KeysetCursor.Encode(items[^1].ScheduledFor, items[^1].Id)
            : null;
        return Ok(new TriggerOccurrencePage(items, nextCursor, hasMore));
    }

    private IActionResult Written(Trigger trigger, int status)
    {
        Response.SetVersionETag(trigger.Version);
        return StatusCode(status, TriggerResponse.From(trigger));
    }

    /// <summary>
    /// The creator's identity, captured here exactly once. §6.1's immutable grant snapshot is taken
    /// from this authenticated request; firing never re-reads the creator's current grants.
    /// </summary>
    private (string TenantId, TriggerPrincipal Principal) Caller()
    {
        RequireEnabled();
        Request.RequireCapability("workflow.manage");
        return (
            Request.RequireTenant(),
            new TriggerPrincipal(
                Request.RequireUserId(),
                Request.RequireUserRole(),
                Request.UserGroups(),
                Request.UserCapabilities()));
    }

    // Defense in depth: unreachable while Program.cs's AGENT_TRIGGERS_ENABLED middleware stands in
    // front of these routes, kept so a future routing change cannot expose them — same fixed
    // "找不到資源" message as RunDiscoveryController (a gate-specific string would leak that a
    // disabled feature lives here).
    private void RequireEnabled()
    {
        if (!state.Enabled)
        {
            throw new ApiException(404, "找不到資源");
        }
    }

    private static ApiException Missing() => new(404, "找不到觸發器");

    private static string Name(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            throw new ApiException(400, "name is required");
        }
        return name;
    }

    private static string Description(string? value)
    {
        var description = value?.Trim() ?? string.Empty;
        if (description.Length > MaxDescriptionLength)
        {
            throw new ApiException(400, "description is too long");
        }
        return description;
    }

    /// <summary>
    /// One-shot triggers are absolute UTC instants: an offset-less or local-offset literal is
    /// rejected rather than silently reinterpreted, so the stored instant always means what the
    /// caller sent. Timezone/DST handling stays a client input concern (§6, "明確不做").
    /// <para>
    /// W2-03 上限:<c>fire_at</c> 距現在不得超過 <see cref="MaxFireAtHorizonDays"/> 天。理由是安全
    /// 而非效能——觸發器持有一份建立當下的**不可變 principal 快照**(§6.1),fire 時不會重讀建立者
    /// 現況權限,所以一個排到無限遠的觸發器等於一個引信無限長的權限提升定時裝置。改期路徑本來就是
    /// 「取消後重建」(O5 刻意的 YAGNI 決策),因此 90 天對真實排程需求不構成限制,卻把引信長度砍到
    /// 有界。決策記錄:plans/wave2-decisions-2026-08-10.md(W2-03)。
    /// </para>
    /// </summary>
    private static DateTime FireAt(DateTime? value)
    {
        if (value is not { Kind: DateTimeKind.Utc } fireAt)
        {
            throw new ApiException(400, "fire_at must be an ISO-8601 UTC instant");
        }
        if (fireAt > DateTime.UtcNow.AddDays(MaxFireAtHorizonDays))
        {
            throw new ApiException(400, "fire_at 不得超過現在起算 90 天");
        }
        return fireAt;
    }

    private static int MisfireWindow(int? value)
    {
        var seconds = value ?? DefaultMisfireWindowSeconds;
        if (seconds is < 1 or > MaxMisfireWindowSeconds)
        {
            throw new ApiException(400, "misfire_window_seconds 必須介於 1 到 86400");
        }
        return seconds;
    }
}
