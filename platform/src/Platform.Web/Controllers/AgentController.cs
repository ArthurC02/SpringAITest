using System.Globalization;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
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
public sealed class AgentController : ProxyControllerBase
{
    private readonly IAgentService _agents;

    public AgentController(IAgentService agents) => _agents = agents;

    /// <summary>
    /// Agent id 已由 <c>{id:guid}</c>、revision 已由 <c>{revision:int}</c> 在路由層解析成型別值,
    /// 這裡只以固定格式(Guid 的 <c>D</c>、invariant 的整數)放進已知路徑片段 ——
    /// caller-controlled route 文字永遠不參與 URI normalization。
    /// </summary>
    private static string Suffix(Guid id, string? action = null)
        => action is null ? id.ToString("D") : $"{id:D}/{action}";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
        => Write(await _agents.SendAsync(HttpMethod.Get, string.Empty, User.ToUserContext(), ct: ct));

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Post, string.Empty, User.ToUserContext(), body: body, ct: ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Write(await _agents.SendAsync(HttpMethod.Get, Suffix(id), User.ToUserContext(), ct: ct));

    [HttpPut("{id:guid}/draft")]
    public async Task<IActionResult> UpdateDraft(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Put, Suffix(id, "draft"), User.ToUserContext(), IfMatch, body, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
        => Write(await _agents.SendAsync(HttpMethod.Delete, Suffix(id), User.ToUserContext(), ct: ct));

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Post, Suffix(id, "enable"), User.ToUserContext(), ct: ct));

    [HttpPost("{id:guid}/validate")]
    public async Task<IActionResult> Validate(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Post, Suffix(id, "validate"), User.ToUserContext(), IfMatch, body, ct));

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] JsonElement? body, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Post, Suffix(id, "publish"), User.ToUserContext(), IfMatch, body, ct));

    [HttpGet("{id:guid}/revisions")]
    public async Task<IActionResult> Revisions(Guid id, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Get, Suffix(id, "revisions"), User.ToUserContext(), ct: ct));

    [HttpPost("{id:guid}/revisions/{revision:int}/restore")]
    public async Task<IActionResult> RestoreRevision(Guid id, int revision, CancellationToken ct)
        => Write(await _agents.SendAsync(
            HttpMethod.Post,
            Suffix(id, $"revisions/{revision.ToString(CultureInfo.InvariantCulture)}/restore"),
            User.ToUserContext(),
            ct: ct));

    [HttpGet("catalog/rule-facts")]
    public async Task<ActionResult<JsonElement>> RuleFacts(
        [FromServices] IWorkflowEngineClient engine, CancellationToken ct)
        => Ok(await engine.GetBusinessRuleFactsAsync(User.ToUserContext(), ct));

    [HttpGet("catalog/rule-actions")]
    public async Task<ActionResult<JsonElement>> RuleActions(
        [FromServices] IWorkflowEngineClient engine, CancellationToken ct)
        => Ok(await engine.GetBusinessRuleActionsAsync(User.ToUserContext(), ct));

    [HttpPost("rules/validate")]
    public async Task<ActionResult<JsonElement>> ValidateRules(
        [FromServices] IWorkflowEngineClient engine,
        [FromBody] BusinessRuleValidateRequest request,
        CancellationToken ct)
        => Ok(await engine.ValidateBusinessRulesAsync(request, User.ToUserContext(), ct));

    [HttpPost("rules/simulate")]
    public async Task<ActionResult<JsonElement>> SimulateRules(
        [FromServices] IWorkflowEngineClient engine,
        [FromBody] BusinessRuleSimulateRequest request,
        CancellationToken ct)
        => Ok(await engine.SimulateBusinessRulesAsync(request, User.ToUserContext(), ct));
}
