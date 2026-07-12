using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>文件端點,需認證。從 JWT 主體組 UserContext 傳給 service。</summary>
[ApiController]
[Route("api/documents")]
[Authorize]
public sealed class DocumentController : ControllerBase
{
    private readonly IDocumentService _documents;

    public DocumentController(IDocumentService documents) => _documents = documents;

    /// <summary>受理文件 — 202 Accepted,回 DocumentAccepted(id/title/status=processing);背景非同步處理。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DocumentCreateRequest request, CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        var accepted = await _documents.CreateAsync(request, ctx, ct);
        return StatusCode(StatusCodes.Status202Accepted, accepted);
    }

    /// <summary>列出文件。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DocumentInfo>>> List(CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        var documents = await _documents.ListAsync(ctx, ct);
        return Ok(documents);
    }

    /// <summary>刪除文件 — 204 No Content。</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        await _documents.DeleteAsync(id, ctx, ct);
        return NoContent();
    }
}
