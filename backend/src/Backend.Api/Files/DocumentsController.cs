using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Files;

/// <summary>
/// 檔案讀取/刪除端點。需 X-Tenant-Id。公開建立仍由 platform 發佈 RabbitMQ；內部
/// ingest-intents 端點只在 publish 前配置持久 identity，不處理內容。
/// </summary>
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IRagRepository _rag;

    public DocumentsController(IRagRepository rag) => _rag = rag;

    [HttpPost("ingest-intents")]
    public async Task<ActionResult<DocumentIngestIntentResponse>> AllocateIngestIntent(
        [FromBody] DocumentIngestIntentRequest request,
        CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var userId = Request.RequireUserId();
        var idempotencyKey = Request.RequireIdempotencyKey();
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 500)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "title 不可為空且長度不可超過 500 字");
        }
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 1_000_000)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "text 不可為空且長度不可超過 1000000 字");
        }

        var allocation = await _rag.AllocateDocumentAsync(
            tenantId,
            userId,
            DocumentIngestIdentity.HashKey(idempotencyKey),
            DocumentIngestIdentity.HashRequest(request.Title, request.Text),
            request.Title,
            ct);
        if (allocation.Status == DocumentIngestAllocationStatus.PayloadConflict)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Idempotency-Key 已用於不同的文件請求");
        }
        if (allocation.Status == DocumentIngestAllocationStatus.DeletedConflict)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "已刪除文件的 Idempotency-Key 不可重用");
        }

        var response = new DocumentIngestIntentResponse(allocation.DocumentId, allocation.Title, "processing");
        return allocation.Status == DocumentIngestAllocationStatus.Created
            ? StatusCode(StatusCodes.Status201Created, response)
            : Ok(response);
    }

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
            throw ApiErrors.NotFound("文件", id);
        }

        return NoContent();
    }
}
