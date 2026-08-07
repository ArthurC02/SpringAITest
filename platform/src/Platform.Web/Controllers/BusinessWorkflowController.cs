using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;

namespace Platform.Web.Controllers;

/// <summary>Business Workflow CRUD/export/validate 的已認證代理面。</summary>
[ApiController]
[Route("api/business-workflows")]
[Authorize]
public sealed class BusinessWorkflowController : ControllerBase
{
    private readonly IBusinessWorkflowService _workflows;
    private readonly IWorkflowEngineClient _engine;

    public BusinessWorkflowController(
        IBusinessWorkflowService workflows, IWorkflowEngineClient engine)
    {
        _workflows = workflows;
        _engine = engine;
    }

    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
        => Ok(await _workflows.ListAsync(User.ToUserContext(), ct));

    [HttpGet("{name}")]
    public async Task<ActionResult<JsonElement>> Get(string name, CancellationToken ct)
        => Ok(await _workflows.GetAsync(name, User.ToUserContext(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SkillUpsert request, CancellationToken ct)
    {
        var created = await _workflows.CreateAsync(request, User.ToUserContext(), ct);
        return Created(
            created.Location ?? $"/api/business-workflows/{created.Workflow.Name}",
            created.Workflow);
    }

    [HttpPut("{name}")]
    public async Task<ActionResult<Skill>> Update(
        string name, [FromBody] SkillUpsert request, CancellationToken ct)
        => Ok(await _workflows.UpdateAsync(name, request, User.ToUserContext(), ct));

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        await _workflows.DeleteAsync(name, User.ToUserContext(), ct);
        return NoContent();
    }

    [HttpGet("{name}/export")]
    public async Task<IActionResult> Export(string name, CancellationToken ct)
    {
        var result = await _workflows.ExportAsync(name, User.ToUserContext(), ct);
        return File(result.Content, result.ContentType, result.FileName);
    }

    [HttpPost("validate")]
    public async Task<ActionResult<JsonElement>> Validate(
        [FromBody] SkillUpsert request, CancellationToken ct)
        => Ok(await ArtifactCompatibilityUsageMetrics.TrackValidationAsync(
            HttpContext, "public_business_workflows",
            () => _engine.ValidateBusinessWorkflowAsync(
                request.Definition!, User.ToUserContext(), ct)));
}
