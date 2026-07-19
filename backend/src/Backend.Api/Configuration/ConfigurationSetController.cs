using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Configuration;

/// <summary>
/// Configuration Set CRUD + activate(設計 §7.2)。路由鍵用 id(uuid);回應欄位 snake_case。
/// 角色:CRUD/activate **全掛 [AdminOnly]**(連讀都要 ADMIN — 設定管理性質)。
/// [AdminOnly] 是 authorization filter(早於模型驗證):非 ADMIN 送不合法 body 也是 403,
/// 不會先被 400/422 短路而洩漏欄位規則(比照 SkillController)。
/// 例外:GET active 是 workflow 於 invoke 期直接取有效設定用,invoke 對一般 USER 也會發生,
/// 故**不掛** [AdminOnly] — 走 backend 既有 X-Internal-Token + 身分頭信任邊界即可。
/// 每一條查詢都以 X-Tenant-Id(RequireTenant)過濾:跨租戶一律 404,不洩漏存在性。
/// values 越界 → 422 + FieldErrors(ConfigurationValues,設計 §9);授權早於此驗證。
/// [AdminOnly] 是資源中性的 ADMIN 把關(見 Common/AdminOnlyAttribute):403 訊息不提 Skill。
/// </summary>
[ApiController]
[Route("api/configuration-sets")]
public sealed class ConfigurationSetController : ControllerBase
{
    private readonly IConfigurationSetRepository _repo;

    public ConfigurationSetController(IConfigurationSetRepository repo) => _repo = repo;

    /// <summary>列出本租戶所有 Configuration Set(不含 values)。</summary>
    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<IReadOnlyList<ConfigurationSetInfo>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), ct));

    /// <summary>
    /// 取本租戶當前 active 的 set(含 values)。**不掛 [AdminOnly]** — 供 workflow 於 invoke 期
    /// 直接取有效設定(USER 也會 invoke),走既有 internal-token + 身分頭信任邊界。
    /// 無 active → 404(呼叫端據此回落全域預設)。字面段 active 永遠勝過 {id:guid}。
    /// </summary>
    [HttpGet("active")]
    public async Task<ActionResult<ConfigurationSet>> Active(CancellationToken ct)
        => Ok(await _repo.GetActiveAsync(Request.RequireTenant(), ct)
              ?? throw new ApiException(StatusCodes.Status404NotFound, "沒有啟用中的 Configuration Set"));

    /// <summary>取單筆完整 Configuration Set(含 values)。</summary>
    [HttpGet("{id:guid}")]
    [AdminOnly]
    public async Task<ActionResult<ConfigurationSet>> Get(Guid id, CancellationToken ct)
        => Ok(await _repo.GetAsync(Request.RequireTenant(), id, ct) ?? throw NotFound(id));

    /// <summary>建立 — 201(is_active=false)。values 越界 → 422;同租戶同名 → 409。</summary>
    [HttpPost]
    [AdminOnly]
    public async Task<ActionResult<ConfigurationSet>> Create([FromBody] ConfigurationSetUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        ConfigurationValues.Validate(request.Values);

        var created = await _repo.CreateAsync(tenantId, request.Name!, request.Values ?? new(), Request.UserIdOrEmpty(), ct);
        if (created is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Configuration Set 名稱已存在：" + request.Name);
        }

        return Created($"/api/configuration-sets/{created.Id}", created);
    }

    /// <summary>更新 name/values(不動 is_active)— 200。values 越界 → 422;不存在 → 404;改名撞既有 → 409。</summary>
    [HttpPut("{id:guid}")]
    [AdminOnly]
    public async Task<ActionResult<ConfigurationSet>> Update(
        Guid id, [FromBody] ConfigurationSetUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        ConfigurationValues.Validate(request.Values);

        var updated = await _repo.UpdateAsync(tenantId, id, request.Name!, request.Values ?? new(), ct);
        return Ok(updated ?? throw NotFound(id));
    }

    /// <summary>刪除(硬刪)— 204;不存在 → 404。刪掉 active 者 → 該租戶變無 active(不自動改選)。</summary>
    [HttpDelete("{id:guid}")]
    [AdminOnly]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await _repo.DeleteAsync(Request.RequireTenant(), id, ct))
        {
            throw NotFound(id);
        }

        return NoContent();
    }

    /// <summary>啟用指定 set(原子切換,本租戶其餘轉 inactive)— 200 回啟用後的 set;不存在 → 404。</summary>
    [HttpPost("{id:guid}/activate")]
    [AdminOnly]
    public async Task<ActionResult<ConfigurationSet>> Activate(Guid id, CancellationToken ct)
        => Ok(await _repo.ActivateAsync(Request.RequireTenant(), id, ct) ?? throw NotFound(id));

    private static ApiException NotFound(Guid id) => ApiErrors.NotFound(" Configuration Set", id);
}
