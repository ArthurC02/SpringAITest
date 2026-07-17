using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Skills;

/// <summary>
/// Skill CRUD(規格 §7.2)。路由鍵一律用 name(不是 id);回應欄位 snake_case。
/// 角色:GET 清單/單筆/revisions = USER(執行清單要讓一般使用者列得出自訂 skill);
/// POST/PUT/DELETE = ADMIN(撰寫者等同可注入執行碼)。
/// ADMIN 把關以 [AdminOnly("權限不足，無法存取 Skill")] 掛在各撰寫動作上(authorization filter 階段,早於模型驗證):
/// 非 ADMIN 送不合法 body 也是 403,不會先被 400 短路而洩漏欄位規則。
/// 每一條查詢都以 X-Tenant-Id 過濾:跨租戶一律「不存在」(404),不洩漏存在性。
/// POST/PUT 的 body 只有 definition(YAML 原文):先送引擎 validate(唯一事實來源),
/// 通過才寫入並 bump revision;name/description/required_role 取自引擎回報的中繼資料。
/// </summary>
[ApiController]
[Route("api/skills")]
public sealed class SkillController : ControllerBase
{
    /// <summary>
    /// 保留字:(a) code 註冊工作流的名稱 — skill 不得同名(否則執行時路由鍵撞名);
    /// (b) platform 的字面路由段 catalog/validate/nodes — 字面段永遠勝過 {name},
    /// 這種名字的 skill 建得起來卻永遠點不進去(GET /api/skills/catalog 回的是引擎目錄)。
    /// ponytail: 硬寫保留字，等 workflow 名單真的會變再改成打 GET /workflows。
    /// </summary>
    // 新增 workflow 內建 skill 時必須同步此清單。
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "summarize", "triage", "rag_qa", "analyze_report", "kb_query",
        "catalog", "validate", "nodes",
        "template_retrieval", "template_compare", "template_stats", "template_infer", "template_inspire",
    };

    private readonly ISkillRepository _repo;
    private readonly ISkillValidator _validator;

    public SkillController(ISkillRepository repo, ISkillValidator validator)
    {
        _repo = repo;
        _validator = validator;
    }

    /// <summary>列出本租戶所有啟用中的 skill(不含 definition 內文)。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillInfo>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), ct));

    /// <summary>取單筆完整 skill(含 definition 原文)。</summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<Skill>> Get(string name, CancellationToken ct)
        => Ok(await _repo.GetAsync(Request.RequireTenant(), name, ct)
              ?? throw NotFound(name));

    /// <summary>
    /// 匯出為 Claude Skill 格式 zip(SKILL.md + skill.yaml)。內容與 GET {name} 完全相同(只是打包),
    /// 故角色與該端點一致 = USER 可用(不掛 [AdminOnly])。租戶過濾靠 RequireTenant → 跨租戶自然 404。
    /// </summary>
    [HttpGet("{name}/export")]
    public async Task<IActionResult> Export(string name, CancellationToken ct)
    {
        var skill = await _repo.GetAsync(Request.RequireTenant(), name, ct) ?? throw NotFound(name);
        return File(SkillExporter.ToZip(skill), "application/zip", $"{skill.Name}.zip");
    }

    /// <summary>唯讀 revision 歷史(依 revision 遞減);軟刪的 skill 其歷史仍查得到(稽核紅線)。</summary>
    [HttpGet("{name}/revisions")]
    public async Task<ActionResult<IReadOnlyList<SkillRevisionInfo>>> Revisions(string name, CancellationToken ct)
    {
        var revisions = await _repo.ListRevisionsAsync(Request.RequireTenant(), name, ct);

        // 每個 skill 建立時必寫 revision 1 → 空清單只可能是「本租戶沒有這個 skill」。
        if (revisions.Count == 0)
        {
            throw NotFound(name);
        }

        return Ok(revisions);
    }

    /// <summary>建立 skill — 201(revision 1)。定義未通過引擎驗證 → 422;同名(含既有工作流)→ 409。</summary>
    [HttpPost]
    [AdminOnly("權限不足，無法存取 Skill")]
    public async Task<ActionResult<Skill>> Create([FromBody] SkillUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var meta = await ValidateAsync(request.Definition!, tenantId, ct);

        if (ReservedNames.Contains(meta.Name))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "名稱與既有工作流同名，無法建立：" + meta.Name);
        }

        var created = await _repo.CreateAsync(
            tenantId, ToSkill(meta, request.Definition!), Request.UserIdOrEmpty(), ct);
        if (created is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Skill 名稱已存在：" + meta.Name);
        }

        return Created($"/api/skills/{meta.Name}", created);
    }

    /// <summary>
    /// 更新 skill — 200,current_revision +1 並產生一筆 skill_revision。
    /// YAML 的 name 必須等於路由的 {name},不等 → 422(否則會出現「改 A 卻改到 B」)。
    /// 不支援改名:要改名就刪了重建。
    /// </summary>
    [HttpPut("{name}")]
    [AdminOnly("權限不足，無法存取 Skill")]
    public async Task<ActionResult<Skill>> Update(string name, [FromBody] SkillUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var meta = await ValidateAsync(request.Definition!, tenantId, ct);

        if (!string.Equals(meta.Name, name, StringComparison.Ordinal))
        {
            throw new ApiException(
                StatusCodes.Status422UnprocessableEntity,
                $"Skill 定義的 name 與路由不符：定義為 {meta.Name}，路由為 {name}")
            {
                FieldErrors = new Dictionary<string, string>
                {
                    ["name"] = $"定義的 name（{meta.Name}）必須與路由的 name（{name}）相同",
                },
            };
        }

        var updated = await _repo.UpdateAsync(
            tenantId, name, ToSkill(meta, request.Definition!), Request.UserIdOrEmpty(), ct);
        if (updated is null)
        {
            throw NotFound(name);
        }

        return Ok(updated);
    }

    /// <summary>停用 skill(軟刪 enabled=false)— 204;不存在(含跨租戶不可見、已停用)回 404。revision 保留供稽核。</summary>
    [HttpDelete("{name}")]
    [AdminOnly("權限不足，無法存取 Skill")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        var deleted = await _repo.DeleteAsync(Request.RequireTenant(), name, ct);
        if (!deleted)
        {
            throw NotFound(name);
        }

        return NoContent();
    }

    /// <summary>
    /// 引擎驗證閘門:valid=false → 422,fieldErrors 帶引擎錯誤碼清單(key = 錯誤碼、value = 訊息/行號),
    /// 且**不得寫入 DB**(呼叫端在寫入前先過這關)。引擎不可達 → validator 拋 502(不是 422):
    /// 驗證服務故障不是使用者的定義有問題,更不可放行未驗證的定義。
    /// </summary>
    private async Task<SkillMetadata> ValidateAsync(string definition, string tenantId, CancellationToken ct)
    {
        var result = await _validator.ValidateAsync(
            definition, tenantId, Request.UserId(), Request.UserRole(), ct);

        if (result.Valid)
        {
            // valid=true 時 skill 必存在(WorkflowSkillValidator 已把「缺中繼資料」擋成 502)。
            return result.Skill!;
        }

        var fieldErrors = new Dictionary<string, string>();
        foreach (var error in result.Errors)
        {
            // 同一錯誤碼可能出現多次(不同行);key 為錯誤碼 → 保留第一筆,行號附在訊息尾。
            var message = error.Message ?? error.Code;
            if (error.Line is int line)
            {
                message += $"（第 {line} 行）";
            }

            fieldErrors.TryAdd(error.Code, message);
        }

        throw new ApiException(StatusCodes.Status422UnprocessableEntity, "Skill 定義驗證失敗")
        {
            FieldErrors = fieldErrors,
        };
    }

    private static ApiException NotFound(string name)
        => new(StatusCodes.Status404NotFound, "找不到 Skill：" + name);

    /// <summary>DB 列 = 引擎中繼資料 + YAML 原文。enabled/revision/時間戳由 DB 決定,此處佔位。</summary>
    private static Skill ToSkill(SkillMetadata meta, string definition) => new(
        meta.Name,
        meta.Description,
        definition,
        meta.RequiredRole,
        Enabled: true,
        CurrentRevision: 0,
        CreatedAt: default,
        UpdatedAt: default);
}
