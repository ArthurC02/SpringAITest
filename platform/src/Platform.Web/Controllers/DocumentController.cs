using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Platform.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Exceptions;
using System.Diagnostics.Metrics;

namespace Platform.Web.Controllers;

/// <summary>文件端點,需認證。從 JWT 主體組 UserContext 傳給 service。</summary>
[ApiController]
[Route("api/documents")]
[Authorize]
public sealed class DocumentController : ControllerBase
{
    private const int MaxIdempotencyKeyLength = 128;
    private static readonly Meter Meter = new("Platform.Documents");
    private static readonly Counter<long> MissingIdempotencyKeyCounter =
        Meter.CreateCounter<long>("documents.create.missing_idempotency_key");
    private readonly IDocumentService _documents;

    public DocumentController(IDocumentService documents) => _documents = documents;

    /// <summary>受理文件 — 202 Accepted,回 DocumentAccepted(id/title/status=processing);背景非同步處理。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DocumentCreateRequest request, CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        var idempotencyKey = ResolveIdempotencyKey();
        var accepted = await _documents.CreateAsync(request, ctx, idempotencyKey, ct);
        return StatusCode(StatusCodes.Status202Accepted, accepted);
    }

    private string ResolveIdempotencyKey()
    {
        // 刻意不 Trim(與 chat 那側不同):前後空白在下面的可列印 ASCII 檢查就是無效字元。
        if (IdempotencyKeyHeader.SingleValueOrNull(Request.Headers[IdempotencyKeyHeader.Name]) is not { } value)
        {
            // Compatibility inventory: remove this fallback only after this counter remains zero
            // through the agreed client rollout/rollback window. Missing-key requests are not
            // idempotent across separate HTTP attempts.
            MissingIdempotencyKeyCounter.Add(1);
            return Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrEmpty(value)
            || value.Length > MaxIdempotencyKeyLength
            || value.Any(character => character is < '!' or > '~'))
        {
            throw new WorkflowBadInputException(IdempotencyKeyHeader.InvalidMessage);
        }

        return value;
    }

    /// <summary>列出文件 —— 原樣穿透 backend JSON。</summary>
    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
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
