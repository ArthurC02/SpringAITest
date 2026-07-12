using Backend.Api.Common;
using Backend.Api.Files;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Analysis;

/// <summary>分析端點:租戶文件統計摘要。需 X-Tenant-Id。</summary>
[ApiController]
[Route("api/analysis")]
public sealed class AnalysisController : ControllerBase
{
    private readonly IRagRepository _rag;

    public AnalysisController(IRagRepository rag) => _rag = rag;

    /// <summary>摘要 — 回 { document_count, chunk_count, latest_titles(最近 5 筆) }。</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<AnalysisSummary>> Summary(CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        return Ok(await _rag.SummaryAsync(tenantId, ct));
    }
}
