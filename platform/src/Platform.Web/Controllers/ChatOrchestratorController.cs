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
        // 非 canary 租戶看不到這個端點存在。走全域例外處理才有完整 ApiError envelope
        // (裸 NotFound() 會回一份沒有 code/correlationId 的 ProblemDetails)。
        if (user is null || !options.IsCanaryTenant(user.TenantCode))
            throw new WorkflowNotFoundException("找不到資源");

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
