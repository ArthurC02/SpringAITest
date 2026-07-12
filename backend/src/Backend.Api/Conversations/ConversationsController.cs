using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Conversations;

/// <summary>聊天歷史端點(全域,不分租戶/使用者)。</summary>
[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationRepository _repo;

    public ConversationsController(IConversationRepository repo) => _repo = repo;

    /// <summary>新增一輪對話 — 201 Created,回 { id, createdAt }。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ConversationCreateRequest request, CancellationToken ct)
    {
        var created = await _repo.AddAsync(request.Prompt!, request.Reply!, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>歷史清單 — created_at DESC(最新在前)。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConversationItem>>> List(CancellationToken ct)
        => Ok(await _repo.ListDescAsync(ct));
}
