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

    /// <summary>更新組態 — 非 ADMIN 回 403。</summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<ConfigItem>> Update(string key, [FromBody] ConfigUpdateRequest request, CancellationToken ct)
    {
        if (Request.UserRole() != "ADMIN")
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "權限不足，無法修改系統組態");
        }

        return Ok(await _repo.UpsertAsync(key, request.Value!, ct));
    }
}
