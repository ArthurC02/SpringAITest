using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.CheckpointRetention;

[ApiController]
[Route("api/internal/checkpoint-retention")]
public sealed class CheckpointRetentionController(
    ICheckpointRetentionRepository repository,
    CheckpointRetentionCodec codec) : ControllerBase
{
    [HttpGet("candidates")]
    public async Task<ActionResult<CheckpointRetentionPage>> Candidates(
        [FromQuery] DateTime retentionBefore,
        [FromQuery] DateTime recoveryBefore,
        [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (limit is < 1 or > 100
            || retentionBefore == default
            || recoveryBefore == default
            || retentionBefore.ToUniversalTime() > now
            || recoveryBefore.ToUniversalTime() > now)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "checkpoint retention query is invalid");
        }
        var rows = await repository.ListAsync(
            retentionBefore.ToUniversalTime(),
            recoveryBefore.ToUniversalTime(),
            codec.DecodeCursor(cursor),
            limit + 1,
            ct);
        var hasMore = rows.Count > limit;
        var selected = hasMore ? rows.Take(limit).ToArray() : rows;
        var items = selected.Select(row => new CheckpointRetentionCandidate(
            codec.EncodeCandidate(row.Kind, row.RunId),
            row.Kind,
            row.Source,
            row.TenantId,
            row.UserId,
            row.RunId,
            row.SnapshotSha256,
            row.MaxGeneration,
            row.CheckpointRef,
            row.CompletedAt)).ToArray();
        return Ok(new CheckpointRetentionPage(
            items,
            hasMore ? codec.EncodeCursor(selected[^1]) : null,
            hasMore));
    }

    [HttpPost("ack")]
    public async Task<IActionResult> Ack(
        [FromBody] CheckpointRetentionAckRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CandidateId)
            || request.DeletedThreads is < 0 or > 100_000
            || request.DeletedRootContexts is < 0 or > 100_000
            || request.EvidenceRef?.Length > 256)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "checkpoint retention acknowledgement is invalid");
        }
        var (kind, runId) = codec.DecodeCandidate(request.CandidateId);
        await repository.AckAsync(
            kind,
            runId,
            request.DeletedThreads,
            request.DeletedRootContexts,
            request.EvidenceRef,
            ct);
        return NoContent();
    }
}
