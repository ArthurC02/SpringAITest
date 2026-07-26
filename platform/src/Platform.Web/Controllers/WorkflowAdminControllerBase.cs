using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// WorkflowAdminController 與 OrchestratorAdminController 共用的透明代理骨架:兩者都是同一個
/// IWorkflowAdminService,差異僅在傳給 backend 的 resource 字串("workflows" vs "orchestrators")。
/// [Route] 與各 [HttpXxx] 動作仍留在衍生類別自己宣告。
/// </summary>
[ApiController]
[Authorize(Policy = "workflow.manage")]
public abstract class WorkflowAdminControllerBase : ControllerBase
{
    private readonly IWorkflowAdminService _service;
    private readonly string _resource;

    protected WorkflowAdminControllerBase(IWorkflowAdminService service, string resource)
    {
        _service = service;
        _resource = resource;
    }

    protected string? IfMatch => Request.Headers.IfMatch.Count > 0
        ? Request.Headers.IfMatch.ToString()
        : null;

    protected IActionResult Write(AdminProxyResponse response)
    {
        if (response.ETag is not null)
        {
            Response.Headers.ETag = response.ETag;
        }

        return new ContentResult
        {
            StatusCode = response.Status,
            Content = response.Body,
            ContentType = "application/json; charset=utf-8",
        };
    }

    protected Task<AdminProxyResponse> Send(
        HttpMethod method,
        Guid? id,
        string? suffix,
        JsonElement? body,
        bool forwardIfMatch,
        CancellationToken cancellationToken)
        => _service.SendAsync(
            method,
            _resource,
            id,
            suffix,
            User.ToUserContext(),
            forwardIfMatch ? IfMatch : null,
            body,
            cancellationToken);
}
