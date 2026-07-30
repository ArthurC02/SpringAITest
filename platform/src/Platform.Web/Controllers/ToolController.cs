using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// Workflow Tool Registry 的唯讀安全目錄，需認證。
/// 下游只公開 picker 需要的 name/kind/description/risk/returns；Platform 原樣轉送，
/// 不以 hardcode 猜工具能力，也不公開 endpoint、token 或 executable implementation。
/// </summary>
[ApiController]
[Route("api/tools")]
[Authorize]
public sealed class ToolController : ControllerBase
{
    private readonly IWorkflowEngineClient _engine;

    public ToolController(IWorkflowEngineClient engine) => _engine = engine;

    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
        => Ok(await _engine.GetToolCatalogAsync(User.ToUserContext(), ct));
}
