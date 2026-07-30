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
    /// <summary>
    /// 執行期單鍵讀取的 allowlist:目前僅 P1 prompt manifest canary 這一個鍵。刻意用白名單而非任意
    /// key,免得這條非 ADMIN 路由變成讀任何 ADMIN 內容(如 agent.defaults.system_prompt)的後門。
    /// </summary>
    public const string PromptManifestRevisionKey = "prompt.manifest_revision";

    private static readonly IReadOnlyCollection<string> RuntimeReadableKeys =
        new HashSet<string>(StringComparer.Ordinal) { PromptManifestRevisionKey };

    private readonly IConfigRepository _repo;

    public ConfigController(IConfigRepository repo) => _repo = repo;

    /// <summary>列出本租戶所有組態 — 非 ADMIN 回 403。</summary>
    [HttpGet]
    [AdminOnly("權限不足，無法讀取系統組態")]
    public async Task<ActionResult<IReadOnlyList<ConfigItem>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), ct));

    /// <summary>
    /// 執行期單鍵讀取:給 platform 的 prompt manifest 解析器等「執行時期」呼叫端用,刻意**不掛
    /// [AdminOnly]**(比照 ConfigurationSetController 的 <c>active</c> 路由與 PromptManifestResolutionController
    /// 的 resolved 路由 —— 都是執行期姿態,不是 authoring 期)。key 以 allowlist 限制,未列入的 key
    /// 一律 404(不得成為讀任意 ADMIN 內容的後門);已列入但本租戶未設定值,同樣 404。
    /// </summary>
    [HttpGet("runtime/{key}")]
    public async Task<ActionResult<ConfigItem>> GetRuntime(string key, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        if (!RuntimeReadableKeys.Contains(key))
        {
            throw ApiErrors.NotFound(" runtime config key", key);
        }

        var item = await _repo.GetAsync(tenantId, key, ct);
        return item is null ? throw ApiErrors.NotFound(" runtime config key", key) : Ok(item);
    }

    /// <summary>更新本租戶組態 — 非 ADMIN 回 403。[AdminOnly] 是 authorization filter,早於模型驗證,
    /// 非 ADMIN 送不合法 body 也是 403,不會先被 400 短路而洩漏欄位規則(比照 SkillController)。</summary>
    [HttpPut("{key}")]
    [AdminOnly("權限不足，無法修改系統組態")]
    public async Task<ActionResult<ConfigItem>> Update(string key, [FromBody] ConfigUpdateRequest request, CancellationToken ct)
        => Ok(await _repo.UpsertAsync(Request.RequireTenant(), key, request.Value!, ct));
}
