using System.Text.Json;
using Backend.Api.Common;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.BusinessWorkflows;

/// <summary>
/// 宣告式 YAML Business Workflow 的管理 API。物理儲存仍共用 skill/skill_revision，
/// 但此邊界只會讀寫 kind=flow；Agent Skill 則由 /api/skills 管理。
/// </summary>
[ApiController]
[Route("api/business-workflows")]
public sealed class BusinessWorkflowController : ControllerBase
{
    private readonly ISkillRepository _repo;
    private readonly ISkillValidator _validator;

    public BusinessWorkflowController(ISkillRepository repo, ISkillValidator validator)
    {
        _repo = repo;
        _validator = validator;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillInfo>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), "flow", ct));

    [HttpGet("{name}")]
    public async Task<ActionResult<Skill>> Get(string name, CancellationToken ct)
        => Ok(await _repo.GetAsync(Request.RequireTenant(), name, "flow", ct) ?? throw NotFound(name));

    [HttpGet("{name}/export")]
    public async Task<IActionResult> Export(string name, CancellationToken ct)
    {
        var workflow = await _repo.GetAsync(Request.RequireTenant(), name, "flow", ct) ?? throw NotFound(name);
        return File(workflow.Package ?? SkillExporter.ToZip(workflow), "application/zip", $"{workflow.Name}.zip");
    }

    [HttpPost]
    [AdminOnly("權限不足，無法存取 Business Workflow")]
    public async Task<ActionResult<Skill>> Create([FromBody] SkillUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var meta = await ValidateAsync(request.Definition!, tenantId, ct);
        if (SkillNameRules.ReservedBusinessWorkflowNames.Contains(meta.Name))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "名稱與既有工作流同名，無法建立：" + meta.Name);
        }

        var created = await _repo.CreateAsync(
            tenantId,
            "flow",
            ToWorkflow(meta, request.Definition!, SimpleFormText(request.SimpleForm)),
            Request.UserIdOrEmpty(),
            ct);
        if (created is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Business Workflow 名稱已存在：" + meta.Name);
        }

        return Created($"/api/business-workflows/{meta.Name}", created);
    }

    [HttpPut("{name}")]
    [AdminOnly("權限不足，無法存取 Business Workflow")]
    public async Task<ActionResult<Skill>> Update(
        string name, [FromBody] SkillUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var meta = await ValidateAsync(request.Definition!, tenantId, ct);
        if (!string.Equals(meta.Name, name, StringComparison.Ordinal))
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                $"Business Workflow 定義的 name 與路由不符：定義為 {meta.Name}，路由為 {name}")
            {
                FieldErrors = new Dictionary<string, string>
                {
                    ["name"] = $"定義的 name（{meta.Name}）必須與路由的 name（{name}）相同",
                },
            };
        }

        var updated = await _repo.UpdateAsync(
            tenantId,
            name,
            "flow",
            ToWorkflow(meta, request.Definition!, SimpleFormText(request.SimpleForm)),
            Request.UserIdOrEmpty(),
            ct);
        return Ok(updated ?? throw NotFound(name));
    }

    [HttpDelete("{name}")]
    [AdminOnly("權限不足，無法存取 Business Workflow")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (!await _repo.DeleteAsync(Request.RequireTenant(), name, "flow", ct))
        {
            throw NotFound(name);
        }

        return NoContent();
    }

    private async Task<SkillMetadata> ValidateAsync(string definition, string tenantId, CancellationToken ct)
    {
        var result = await _validator.ValidateAsync(
            definition, tenantId, Request.UserId(), Request.UserRole(), ct);
        if (result.Valid)
        {
            return result.Skill!;
        }

        var fieldErrors = new Dictionary<string, string>();
        foreach (var error in result.Errors)
        {
            var message = error.Message ?? error.Code;
            if (error.Line is int line)
            {
                message += $"（第 {line} 行）";
            }
            fieldErrors.TryAdd(error.Code, message);
        }

        throw new ApiException(StatusCodes.Status422UnprocessableEntity, "Business Workflow 定義驗證失敗")
        {
            FieldErrors = fieldErrors,
        };
    }

    private static string? SimpleFormText(JsonElement? form)
        => form is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value
            ? value.GetRawText()
            : null;

    private static Skill ToWorkflow(SkillMetadata meta, string definition, string? simpleForm) => new(
        meta.Name,
        meta.Description,
        definition,
        meta.RequiredRole,
        Enabled: true,
        CurrentRevision: 0,
        CreatedAt: default,
        UpdatedAt: default,
        Kind: "flow",
        SimpleForm: simpleForm);

    private static ApiException NotFound(string name)
        => ApiErrors.NotFound(" Business Workflow", name);
}
