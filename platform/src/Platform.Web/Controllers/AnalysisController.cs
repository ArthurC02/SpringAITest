using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>分析端點,需認證。從 JWT 主體組 UserContext(轉發身分 header)傳給 service。</summary>
[ApiController]
[Route("api/analysis")]
[Authorize]
public sealed class AnalysisController : ControllerBase
{
    private readonly IAnalysisService _analysis;

    public AnalysisController(IAnalysisService analysis) => _analysis = analysis;

    /// <summary>租戶文件統計摘要。</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<AnalysisSummary>> Summary(CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        return Ok(await _analysis.SummaryAsync(ctx, ct));
    }
}
