using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Platform.Service.Abstractions;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>
/// WorkflowAdminController 與 OrchestratorAdminController 共用的透明代理骨架:兩者都是同一個
/// IWorkflowAdminService,差異僅在傳給 backend 的 resource 字串("workflows" vs "orchestrators")。
/// 兩個 resource 逐字相同的 CRUD/revision/lifecycle 動作連同 [HttpXxx] 都住在這裡(MVC 會繼承帶
/// 路由屬性的 action);[Route] 與各自獨有的動作(workflows 的 simulate/catalog)留在衍生類別。
/// </summary>
[ApiController]
[Authorize(Policy = "workflow.manage")]
public abstract class WorkflowAdminControllerBase : ProxyControllerBase
{
    private readonly IWorkflowAdminService _service;
    private readonly string _resource;

    protected WorkflowAdminControllerBase(IWorkflowAdminService service, string resource)
    {
        _service = service;
        _resource = resource;
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

    protected Task<AgentProxyResponse> Send(
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
