using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Config;

/// <summary>系統組態端點:key-value。PUT 需 X-User-Role: ADMIN。</summary>
[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly IConfigRepository _repo;

    public ConfigController(IConfigRepository repo) => _repo = repo;

    /// <summary>列出所有組態。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConfigItem>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(ct));

    /// <summary>更新組態 — 非 ADMIN 回 403。[AdminOnly] 是 authorization filter,早於模型驗證,
    /// 非 ADMIN 送不合法 body 也是 403,不會先被 400 短路而洩漏欄位規則(比照 SkillController)。</summary>
    [HttpPut("{key}")]
    [AdminOnly("權限不足，無法修改系統組態")]
    public async Task<ActionResult<ConfigItem>> Update(string key, [FromBody] ConfigUpdateRequest request, CancellationToken ct)
        => Ok(await _repo.UpsertAsync(key, request.Value!, ct));
}
