using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Contexts;

[ApiController, Route("api")]
public sealed class ContextController(IContextRepository contexts) : ControllerBase
{
    [HttpPost("contexts/{contextId:guid}/revisions")]
    public async Task<IActionResult> Submit(Guid contextId, ContextRevisionSubmitRequest request, CancellationToken ct)
    {
        try
        {
            var result = await contexts.CreateRevisionAsync(Request.RequireTenant(), Request.RequireUserId(), contextId, request, ct);
            Response.SetVersionETag(result.Revision.Revision);
            return CreatedAtAction(nameof(GetRevision), new { contextId, revision = result.Revision.Revision }, result.Revision);
        }
        catch (ContextPolicyUnavailableException) { throw new ApiException(503, "No active context policy is available"); }
        catch (ContextPolicyInvalidException e) { throw new ApiException(422, e.Message); }
        catch (ArgumentException e) { throw new ApiException(400, e.Message); }
    }

    [HttpGet("contexts/{contextId:guid}/revisions/{revision:int}")]
    public async Task<IActionResult> GetRevision(Guid contextId, int revision, CancellationToken ct)
    {
        var result = await contexts.GetRevisionAsync(Request.RequireTenant(), contextId, revision, ct);
        if (result is null) throw ApiErrors.NotFound(" Context revision", $"{contextId:D}/{revision}");
        Response.SetVersionETag(result.Revision);
        return Ok(result);
    }

    [HttpGet("context-views/{viewId:guid}")]
    public async Task<IActionResult> GetView(Guid viewId, CancellationToken ct)
    {
        var result = await contexts.GetViewAsync(Request.RequireTenant(), viewId, ct) ?? throw ApiErrors.NotFound(" Context view", viewId);
        Response.SetVersionETag(result.Revision);
        return Ok(result);
    }

    [HttpGet("context-policies")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        try { return Ok(await contexts.GetActivePolicyAsync(Request.RequireTenant(), ct) ?? throw new ApiException(503, "No active context policy is available")); }
        catch (ContextPolicyInvalidException e) { throw new ApiException(422, e.Message); }
    }
}
