using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service;
using Platform.Service.Exceptions;
using Platform.Service.Options;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>D6 USER-safe discovery surface. It never proxies management drafts or policies.</summary>
[ApiController]
[Route("api/chat/orchestrators")]
[Authorize]
public sealed class ChatOrchestratorController(
    BackendClient backend,
    AgentChatOptions options) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var user = User.ToUsableChatUserContext();
        if (user is null || !options.IsCanaryTenant(user.TenantCode))
            return NotFound();

        using var request = backend.BuildRequest(
            HttpMethod.Get, "/api/runtime-discovery/orchestrators", user);
        var result = await backend.SendForJsonElementAsync(
            request,
            ex => new WorkflowInvocationException("Runtime discovery unavailable", ex),
            async (response, token) => new WorkflowInvocationException(
                await backend.ReadErrorMessageAsync(response, token)),
            ct);
        return Ok(result);
    }
}
