using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Config;

/// <summary>系統組態端點:key-value,**租戶隔離**(每租戶各一份,以 X-Tenant-Id 區隔;缺標頭 → 400)。
/// GET 與 PUT 皆需 X-User-Role: ADMIN
/// (組態值含 agent.defaults.system_prompt 全文等管理者內容,不對一般 USER 開放讀取)。
/// 對外 wire 契約不變:ConfigItem 仍是 { key, value, updatedAt },不洩漏 tenant。</summary>
[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly IConfigRepository _repo;

    public ConfigController(IConfigRepository repo) => _repo = repo;

    /// <summary>列出本租戶所有組態 — 非 ADMIN 回 403。</summary>
    [HttpGet]
    [AdminOnly("權限不足，無法讀取系統組態")]
    public async Task<ActionResult<IReadOnlyList<ConfigItem>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), ct));

    /// <summary>更新本租戶組態 — 非 ADMIN 回 403。[AdminOnly] 是 authorization filter,早於模型驗證,
    /// 非 ADMIN 送不合法 body 也是 403,不會先被 400 短路而洩漏欄位規則(比照 SkillController)。</summary>
    [HttpPut("{key}")]
    [AdminOnly("權限不足，無法修改系統組態")]
    public async Task<ActionResult<ConfigItem>> Update(string key, [FromBody] ConfigUpdateRequest request, CancellationToken ct)
        => Ok(await _repo.UpsertAsync(Request.RequireTenant(), key, request.Value!, ct));
}
