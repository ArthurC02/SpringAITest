using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Platform.Web.Controllers;

/// <summary>
/// Agent Builder 管理端點(D1),需 ADMIN + 認證(JWT)。同路徑透明代理 backend /api/agents:身分從 JWT {tenant}:{user}
/// 派生下傳 identity headers(絕不信 body);原樣穿透 backend 的狀態碼、body 與 ETag/If-Match(含 409/428/404/422)。
///
/// 整個 family 都是 Builder authoring API，detail 會包含 system prompt / policy draft，因此讀寫皆先在 platform
/// 以 ADMIN fail-closed，backend 保留相同 boundary。一般 USER 未來如需挑選 published Agent，必須走獨立的
/// redacted catalog contract，不可重用本 controller。
/// Feature flag(AGENT_BUILDER_ENABLED)關閉時,整個 /api/agents* 由 Program.cs 的前置中介軟體 fail-closed 回 404,
/// 請求到不了本 controller。
/// </summary>
[ApiController]
[Route("api/agents")]
[Authorize]
[AdminOnly("權限不足，無法存取 Agent")]
public sealed class AgentController : ControllerBase
{
    private readonly IAgentService _agents;

    public AgentController(IAgentService agents) => _agents = agents;

    /// <summary>本次請求的 If-Match(樂觀鎖前置條件);缺則 null。原樣轉發給 backend。</summary>
    private string? IfMatch =>
        Request.Headers.IfMatch.Count > 0 ? Request.Headers.IfMatch.ToString() : null;

    /// <summary>把 backend 的透明代理回應原樣寫回:狀態碼、JSON body 與 ETag response header(若有)。</summary>
    private IActionResult Write(AgentProxyResponse r)
    {
        if (r.ETag is not null)
        {
            Response.Headers.ETag = r.ETag;
        }

        return new ContentResult
        {
            StatusCode = r.Status,
            Content = r.Body,
            ContentType = "application/json; charset=utf-8",
        };
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Write(await _agents.ListAsync(User.ToUserContext(), ct));

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.CreateAsync(User.ToUserContext(), body, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Write(await _agents.GetAsync(id, User.ToUserContext(), ct));

    [HttpPut("{id:guid}/draft")]
    public async Task<IActionResult> UpdateDraft(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.UpdateDraftAsync(id, User.ToUserContext(), IfMatch, body, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
        => Write(await _agents.DeactivateAsync(id, User.ToUserContext(), ct));

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
        => Write(await _agents.EnableAsync(id, User.ToUserContext(), ct));

    [HttpPost("{id:guid}/validate")]
    public async Task<IActionResult> Validate(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.ValidateAsync(id, User.ToUserContext(), IfMatch, body, ct));

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.PublishAsync(id, User.ToUserContext(), IfMatch, body, ct));

    [HttpGet("{id:guid}/revisions")]
    public async Task<IActionResult> Revisions(Guid id, CancellationToken ct)
        => Write(await _agents.RevisionsAsync(id, User.ToUserContext(), ct));

    [HttpPost("{id:guid}/revisions/{revision:int}/restore")]
    public async Task<IActionResult> RestoreRevision(Guid id, int revision, CancellationToken ct)
        => Write(await _agents.RestoreRevisionAsync(id, revision, User.ToUserContext(), ct));
}
