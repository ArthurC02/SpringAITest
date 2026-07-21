using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// Skill 端點,需認證(JWT)。兩個下游、一個路由前綴:
///   CRUD + revisions → backend(:8002);catalog / validate / invoke → workflow 引擎(:8001)。
/// 角色把關全在下游(非 ADMIN → backend 403 → 對外 403,同 Config PUT 模式);
/// 本層只負責:驗 JWT、從主體組 UserContext(轉發身分 header)、把下游錯誤映射成統一的 ApiError。
///
/// 路由優先序:ASP.NET Core 的字面段(catalog、validate)勝過參數段({name}),
/// 所以名為 "catalog" 的 skill 不會遮蔽 GET /api/skills/catalog(SkillApiTests 有測試釘住這件事)。
/// </summary>
[ApiController]
[Route("api/skills")]
[Authorize]
public sealed class SkillController : ControllerBase
{
    private readonly ISkillService _skills;
    private readonly IWorkflowService _engine;

    public SkillController(ISkillService skills, IWorkflowService engine)
    {
        _skills = skills;
        _engine = engine;
    }

    /// <summary>列出本租戶的自訂 Skill(CRUD 用清單,backend 不含 definition);原樣穿透 backend JSON。</summary>
    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
        => Ok(await _skills.ListAsync(User.ToUserContext(), ct));

    /// <summary>可執行 Skill 目錄:引擎合併內建 + 自訂,每筆帶 source 徽章(builtin/custom)。</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<JsonElement>> Catalog(CancellationToken ct)
        => Ok(await _engine.GetSkillCatalogAsync(User.ToUserContext(), ct));

    /// <summary>取單一 Skill(含 definition 原文);原樣穿透 backend JSON。</summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<JsonElement>> Get(string name, CancellationToken ct)
        => Ok(await _skills.GetAsync(name, User.ToUserContext(), ct));

    /// <summary>匯出 Skill 為 Claude Skill 格式 zip(代理 backend,不是引擎)。</summary>
    [HttpGet("{name}/export")]
    public async Task<IActionResult> Export(string name, CancellationToken ct)
    {
        var e = await _skills.ExportAsync(name, User.ToUserContext(), ct);
        return File(e.Content, e.ContentType, e.FileName);
    }

    /// <summary>唯讀 revision 歷史(依 revision 遞減);原樣穿透 backend JSON。</summary>
    [HttpGet("{name}/revisions")]
    public async Task<ActionResult<JsonElement>> Revisions(string name, CancellationToken ct)
        => Ok(await _skills.GetRevisionsAsync(name, User.ToUserContext(), ct));

    /// <summary>建立 Skill — 201 Created;定義未通過引擎驗證 → 422(fieldErrors 帶引擎錯誤碼)。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SkillUpsert request, CancellationToken ct)
    {
        var created = await _skills.CreateAsync(request, User.ToUserContext(), ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>更新 Skill(產生新 revision)。</summary>
    [HttpPut("{name}")]
    public async Task<ActionResult<Skill>> Update(
        string name, [FromBody] SkillUpsert request, CancellationToken ct)
        => Ok(await _skills.UpdateAsync(name, request, User.ToUserContext(), ct));

    /// <summary>停用 Skill(軟刪)— 204 No Content。</summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        await _skills.DeleteAsync(name, User.ToUserContext(), ct);
        return NoContent();
    }

    /// <summary>對一份定義跑靜態驗證(無副作用,編輯器即時校驗)。引擎一律回 200,結果在 body。</summary>
    [HttpPost("validate")]
    public async Task<ActionResult<JsonElement>> Validate([FromBody] SkillUpsert request, CancellationToken ct)
        => Ok(await _engine.ValidateSkillAsync(request.Definition!, User.ToUserContext(), ct));

    /// <summary>執行 Skill;錯誤碼映射見 WorkflowService.MapInvokeErrorAsync。</summary>
    [HttpPost("{name}/invoke")]
    public async Task<ActionResult<JsonElement>> Invoke(
        string name, [FromBody] WorkflowInvokeRequest request, CancellationToken ct)
        => Ok(await _engine.InvokeSkillAsync(name, request.Input!, User.ToUserContext(), ct));
}
