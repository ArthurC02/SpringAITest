using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// 工作流端點,需認證(任何角色皆可;角色把關在 Python 下游)。
/// 從 JWT 主體組 UserContext 傳給 service。
/// </summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
public sealed class WorkflowController : ControllerBase
{
    private readonly IWorkflowService _workflows;

    public WorkflowController(IWorkflowService workflows) => _workflows = workflows;

    /// <summary>列出工作流。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkflowInfo>>> List(CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        var workflows = await _workflows.ListAsync(ctx, ct);
        return Ok(workflows);
    }

    /// <summary>執行指定工作流。</summary>
    [HttpPost("{name}")]
    public async Task<ActionResult<WorkflowInvokeResponse>> Invoke(
        string name, [FromBody] WorkflowInvokeRequest request, CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        var result = await _workflows.InvokeAsync(name, request.Input!, ctx, ct);
        return Ok(result);
    }
}
