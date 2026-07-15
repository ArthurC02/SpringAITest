using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// 節點目錄(唯讀),需認證。純代理 workflow 引擎的 GET /nodes:
/// 節點是程式即事實來源(不入 DB),此端點即時反映引擎已註冊的節點契約。
/// </summary>
[ApiController]
[Route("api/nodes")]
[Authorize]
public sealed class NodeController : ControllerBase
{
    private readonly IWorkflowService _engine;

    public NodeController(IWorkflowService engine) => _engine = engine;

    /// <summary>節點契約清單:name/version/description/reads/writes/requires_tools。</summary>
    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
        => Ok(await _engine.GetNodeCatalogAsync(User.ToUserContext(), ct));
}
