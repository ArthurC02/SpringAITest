using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// 系統組態端點,需認證。ADMIN 把關在 backend(GET 與 PUT 非 ADMIN → backend 403 → 對外 403)。
/// 從 JWT 主體組 UserContext(轉發身分 header)傳給 service。
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize]
public sealed class ConfigController : ControllerBase
{
    private readonly IConfigService _config;

    public ConfigController(IConfigService config) => _config = config;

    /// <summary>列出所有組態。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ConfigItem>>> List(CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        return Ok(await _config.ListAsync(ctx, ct));
    }

    /// <summary>更新組態 — 非 ADMIN 回 403。</summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<ConfigItem>> Update(string key, [FromBody] ConfigUpdateRequest request, CancellationToken ct)
    {
        var ctx = User.ToUserContext();
        return Ok(await _config.UpdateAsync(key, request, ctx, ct));
    }
}
