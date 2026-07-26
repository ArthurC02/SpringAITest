using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Platform.Service.Abstractions;

namespace Platform.Web.Controllers;

/// <summary>共用的 CRUD/revision/lifecycle 動作在 <see cref="WorkflowAdminControllerBase"/>;
/// 這裡只留 workflows 獨有的 simulate 與 node catalog。</summary>
[Route("api/admin/workflows")]
public sealed class WorkflowAdminController : WorkflowAdminControllerBase
{
    public WorkflowAdminController(IWorkflowAdminService service) : base(service, "workflows")
    {
    }

    [HttpPost("{id:guid}/simulate")]
    public async Task<IActionResult> Simulate(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, id, "simulate", body, true, ct));

    [HttpGet("catalog/nodes")]
    public async Task<IActionResult> Catalog(CancellationToken ct) =>
        Write(await Send(HttpMethod.Get, null, "catalog/nodes", null, false, ct));
}
