using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Controllers;

/// <summary>
/// Configuration Set 端點,需認證(JWT)。單一下游:backend(:8002)。**只代理公開 CRUD**。
/// 角色把關全在 backend(非 ADMIN → backend 403 → 對外 403,同 Skill CRUD 模式);
/// 本層只負責:驗 JWT、從主體組 UserContext(轉發身分 header)、把下游錯誤映射成統一的 ApiError。
///
/// 刻意「沒有」讀取 active set 的端點:那是 workflow → backend 直連(執行期 invoke 取值),
/// platform 不得提供 invoke active-set 讀取捷徑(SSR-P4-011)。{id:guid} 路由約束確保
/// GET/PUT/DELETE/activate 只接受 uuid —— 因此 GET /api/configuration-sets/active 不會被
/// {id} 參數段吞掉而轉發到 backend 的 active 端點(literal "active" 非 guid → 不匹配 → 404)。
/// </summary>
[ApiController]
[Route("api/configuration-sets")]
[Authorize]
public sealed class ConfigurationSetController : ControllerBase
{
    private readonly IConfigurationSetService _sets;

    public ConfigurationSetController(IConfigurationSetService sets) => _sets = sets;

    /// <summary>列出本租戶的 Configuration Set(backend 清單不含 values 內容);原樣穿透 backend JSON。</summary>
    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
        => Ok(await _sets.ListAsync(User.ToUserContext(), ct));

    /// <summary>取單一 Configuration Set(含 values);原樣穿透 backend JSON。</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<JsonElement>> Get(string id, CancellationToken ct)
        => Ok(await _sets.GetAsync(id, User.ToUserContext(), ct));

    /// <summary>建立 Configuration Set — 201 Created。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ConfigurationSetUpsert request, CancellationToken ct)
    {
        var created = await _sets.CreateAsync(request, User.ToUserContext(), ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>更新 Configuration Set。</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ConfigurationSet>> Update(
        string id, [FromBody] ConfigurationSetUpsert request, CancellationToken ct)
        => Ok(await _sets.UpdateAsync(id, request, User.ToUserContext(), ct));

    /// <summary>刪除 Configuration Set — 204 No Content。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _sets.DeleteAsync(id, User.ToUserContext(), ct);
        return NoContent();
    }

    /// <summary>啟用指定 Configuration Set(同租戶其餘自動停用,原子性由 backend 保證)。</summary>
    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<ConfigurationSet>> Activate(string id, CancellationToken ct)
        => Ok(await _sets.ActivateAsync(id, User.ToUserContext(), ct));
}
