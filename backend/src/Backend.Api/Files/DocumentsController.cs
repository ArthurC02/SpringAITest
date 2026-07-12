using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Files;

/// <summary>
/// 檔案讀取/刪除端點。需 X-Tenant-Id。建立改為非同步:由 platform 發佈 RabbitMQ 訊息、
/// DocumentConsumerService 背景消費處理(本控制器不再有 POST 端點)。
/// </summary>
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IRagRepository _rag;

    public DocumentsController(IRagRepository rag) => _rag = rag;

    /// <summary>列出文件 — created_at ASC,含 status。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DocumentInfo>>> List(CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        return Ok(await _rag.ListDocumentsAsync(tenantId, ct));
    }

    /// <summary>刪除文件 — 204 No Content;找不到(含跨租戶不可見)回 404。</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var deleted = await _rag.DeleteDocumentAsync(tenantId, id, ct);
        if (!deleted)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "找不到文件：" + id);
        }

        return NoContent();
    }
}
