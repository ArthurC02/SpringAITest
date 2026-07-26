using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Platform.Service.Abstractions;

namespace Platform.Web.Controllers;

[Route("api/admin/workflows")]
public sealed class WorkflowAdminController : WorkflowAdminControllerBase
{
    public WorkflowAdminController(IWorkflowAdminService service) : base(service, "workflows")
    {
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Write(await Send(HttpMethod.Get, null, null, null, false, ct));

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, null, null, body, false, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Write(await Send(HttpMethod.Get, id, null, null, false, ct));

    [HttpPut("{id:guid}/draft")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct) =>
        Write(await Send(HttpMethod.Put, id, "draft", body, true, ct));

    [HttpPost("{id:guid}/validate")]
    public async Task<IActionResult> Validate(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, id, "validate", body, true, ct));

    [HttpPost("{id:guid}/simulate")]
    public async Task<IActionResult> Simulate(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, id, "simulate", body, true, ct));

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body,
        CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, id, "publish", body, true, ct));

    [HttpGet("{id:guid}/revisions")]
    public async Task<IActionResult> Revisions(Guid id, CancellationToken ct) =>
        Write(await Send(HttpMethod.Get, id, "revisions", null, false, ct));

    [HttpPost("{id:guid}/revisions/{revision:int}/restore")]
    public async Task<IActionResult> Restore(Guid id, int revision, CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, id, $"revisions/{revision}/restore", null, false, ct));

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) =>
        Write(await Send(HttpMethod.Post, id, "enable", null, false, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) =>
        Write(await Send(HttpMethod.Delete, id, null, null, false, ct));

    [HttpGet("catalog/nodes")]
    public async Task<IActionResult> Catalog(CancellationToken ct) =>
        Write(await Send(HttpMethod.Get, null, "catalog/nodes", null, false, ct));
}
