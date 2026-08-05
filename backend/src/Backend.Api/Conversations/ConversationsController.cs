using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Conversations;

/// <summary>聊天歷史端點,以 (tenant_id, user_id) 隔離。</summary>
[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private readonly IConversationRepository _repo;
    private readonly ConversationCursorCodec _cursorCodec;

    public ConversationsController(IConversationRepository repo, ConversationCursorCodec cursorCodec)
    {
        _repo = repo;
        _cursorCodec = cursorCodec;
    }

    /// <summary>新增一輪對話 — 201 Created,回 { id, createdAt }。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ConversationCreateRequest request, CancellationToken ct)
    {
        var created = await _repo.AddAsync(Request.RequireTenant(), Request.UserIdOrEmpty(), request.Prompt!, request.Reply!, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>歷史清單 — created_at DESC(最新在前),同租戶同使用者。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationItem>>> List(CancellationToken ct)
        => Ok(await _repo.ListDescAsync(Request.RequireTenant(), Request.UserIdOrEmpty(), ct));

    /// <summary>Additive keyset-paginated history; the legacy array endpoint remains unchanged.</summary>
    [HttpGet("page")]
    public async Task<ActionResult<ConversationPage>> Page(
        [FromQuery] int limit = DefaultPageSize,
        [FromQuery] string? before = null,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > MaxPageSize)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "limit 必須介於 1 到 100");
        }

        var tenantId = Request.RequireTenant();
        var userId = Request.UserIdOrEmpty();
        var position = Request.Query.ContainsKey("before")
            ? _cursorCodec.Decode(before ?? string.Empty, tenantId, userId)
            : null;
        var rows = await _repo.ListPageDescAsync(tenantId, userId, position, limit + 1, ct);
        var hasMore = rows.Count > limit;
        var items = hasMore ? rows.Take(limit).ToList() : rows;
        var nextCursor = hasMore
            ? _cursorCodec.Encode(items[^1], tenantId, userId)
            : null;
        return Ok(new ConversationPage(items, nextCursor, hasMore));
    }
}
