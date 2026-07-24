using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Skills;

/// <summary>
/// Skill CRUD(規格 §7.2)。路由鍵一律用 name(不是 id);回應欄位 snake_case。
/// 角色:GET 清單/單筆/revisions = USER(執行清單要讓一般使用者列得出自訂 skill);
/// POST/PUT/DELETE/import = ADMIN(撰寫者等同可注入執行碼)。
/// ADMIN 把關以 [AdminOnly("權限不足，無法存取 Skill")] 掛在各撰寫動作上(authorization filter 階段,早於模型驗證):
/// 非 ADMIN 送不合法 body 也是 403,不會先被 400 短路而洩漏欄位規則。
/// 每一條查詢都以 X-Tenant-Id 過濾:跨租戶一律「不存在」(404),不洩漏存在性。
/// POST/PUT 的 body 只有 definition(YAML 原文,僅 flow):先送引擎 validate(唯一事實來源),
/// 通過才寫入並 bump revision;name/description/required_role 取自引擎回報的中繼資料。
/// agentic 的作者內容一律走 import(multipart zip → 引擎 validate-package → 存 package + canonical + 兩個 hash)。
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
        "summarize", "triage", "rag-qa", "analyze-report", "kb-query",
        "catalog", "validate", "nodes",
        "template-retrieval", "template-compare", "template-stats", "template-infer", "template-inspire",
    };

    /// <summary>
    /// 匯入上傳的傳輸層粗略上限(transport-safe pre-check,03-design §2.1)。
    /// 這**不是** archive 結構限制(檔案數/單檔/解壓/壓縮比上限由 workflow parser 單一來源持有,R2);
    /// 只是避免在轉送前緩衝過大 body 的天花板。
    /// ponytail: 固定天花板;真正的 archive 限制在引擎,不在此複製。
    /// </summary>
    private const long MaxImportUploadBytes = 25L * 1024 * 1024;

    private readonly ISkillRepository _repo;
    private readonly ISkillValidator _validator;
    private readonly ISkillPackageValidator _packageValidator;

    public SkillController(
        ISkillRepository repo, ISkillValidator validator, ISkillPackageValidator packageValidator)
    {
        _repo = repo;
        _validator = validator;
        _packageValidator = packageValidator;
    }

    /// <summary>列出本租戶所有啟用中的 skill(不含 definition 內文)。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SkillInfo>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), ct));

    /// <summary>取單筆完整 skill(含 definition 原文)。package bytes 不外洩(Skill.Package 標 JsonIgnore)。</summary>
    [HttpGet("{name}")]
    public async Task<ActionResult<Skill>> Get(string name, CancellationToken ct)
        => Ok(await _repo.GetAsync(Request.RequireTenant(), name, ct)
              ?? throw NotFound(name));

    /// <summary>
    /// 匯出為 zip。flow:SkillExporter 組自含式 `{name}/SKILL.md`(定義值內嵌 ```yaml block、無獨立 skill.yaml;與 GET {name} 同資料,只是打包)。
    /// agentic:原封回傳已儲存的 package bytes。角色 = USER(不掛 [AdminOnly]);租戶過濾靠 RequireTenant → 跨租戶 404。
    /// 兩者結構不對稱是刻意的:已存 package 一律 byte-identical round-trip(含其原有的 entry 佈局),
    /// 不重組、不補頂層資料夾;只有「沒有 package 的 definition-only flow」才由 SkillExporter 現場組出標準佈局。
    /// </summary>
    [HttpGet("{name}/export")]
    public async Task<IActionResult> Export(string name, CancellationToken ct)
    {
        var skill = await _repo.GetAsync(Request.RequireTenant(), name, ct) ?? throw NotFound(name);

        // 匯入的 flow/agentic 都原封回傳（保留所有 entries）；definition-only flow 才現場組 SKILL.md。
        var bytes = skill.Package ?? SkillExporter.ToZip(skill);
        return File(bytes, "application/zip", $"{skill.Name}.zip");
    }

    /// <summary>
    /// 內部限定:回傳已儲存的原始 package bytes(application/zip)。供 workflow invoke 時取得 agentic package。
    /// 守門:X-Internal-Token 由 InternalTokenMiddleware 全域強制(缺/錯 → 401,早於此 action);
    /// RequireTenant() 過濾租戶 → 跨租戶或不存在或 flow(無 package)一律 404(不洩漏存在性)。
    /// **不經 Platform 代理**(backend 皆內部端點,Platform 僅代理 import,不代理本端點)。
    /// </summary>
    [HttpGet("{name}/package")]
    public async Task<IActionResult> GetPackage(string name, CancellationToken ct)
    {
        var skill = await _repo.GetAsync(Request.RequireTenant(), name, ct);
        if (skill is null
            || !string.Equals(skill.Kind, "agentic", StringComparison.Ordinal)
            || skill.Package is null)
        {
            throw NotFound(name);
        }

        return File(skill.Package, "application/zip");
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

    /// <summary>建立 skill — 201(revision 1)。定義未通過引擎驗證 → 422;同名(含既有工作流)→ 409。僅 flow。</summary>
    [HttpPost]
    [AdminOnly("權限不足，無法存取 Skill")]
    public async Task<ActionResult<Skill>> Create([FromBody] SkillUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();

        // definition-only API 僅處理 flow(R3 / AST-P0-013):`kind: agentic` 定義由引擎 validate 判 invalid → 422。
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
    /// 更新 skill — 200,current_revision +1 並產生一筆 skill_revision。僅 flow。
    /// YAML 的 name 必須等於路由的 {name},不等 → 422(否則會出現「改 A 卻改到 B」)。
    /// 不支援改名:要改名就刪了重建。既有 agentic → 固定 409;送出 agentic 定義 → 引擎 validate 判 invalid → 422(走 import)。
    /// </summary>
    [HttpPut("{name}")]
    [AdminOnly("權限不足，無法存取 Skill")]
    public async Task<ActionResult<Skill>> Update(string name, [FromBody] SkillUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();

        // 既有 agentic skill 不得經 definition-only PUT 更新(R3 / AST-P0-013):先查再判,零副作用、不打引擎。
        var existing = await _repo.GetAsync(tenantId, name, ct);
        RejectAgenticDefinitionOnly(
            string.Equals(existing?.Kind, "agentic", StringComparison.Ordinal));

        // 送出的 definition 若本身宣告 agentic,引擎 validate 判 invalid → 422(不得藉 definition-only 把 flow 改成 agentic)。
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

    /// <summary>
    /// Agent Skill 匯入(ADMIN;R1 / AST-P0-010~013):multipart zip。
    /// [AdminOnly] 於 authorization filter 階段先擋非 ADMIN(早於 body/package 驗證)。
    /// backend 只做 transport-safe 前置檢查(content length),把 zip 原封以 multipart 轉送引擎
    /// POST /skills/validate-package(expected_name={name} + 內部 token + 身分標頭)。
    /// valid=true → 沿用 create/update/revive + revision CTE 於單一交易寫入
    /// (canonical definition、metadata、原始 package bytes、definition_sha256、package_sha256)。
    /// valid=false → 受控 422,零副作用;引擎不可達/5xx/timeout → validator 拋 502,零副作用。
    /// </summary>
    [HttpPost("{name}/import")]
    [AdminOnly("權限不足，無法存取 Skill")]
    public Task<ActionResult<Skill>> ImportNamed(string name, CancellationToken ct)
        => ImportCoreAsync(name, ct);

    /// <summary>
    /// Server-derived Agent Skill 匯入：不接受 client 提供名稱，Workflow 從 package canonical metadata
    /// 推導 skill.name；Backend 再套用標準名稱與 builtin/reserved 衝突防護後寫入。
    /// </summary>
    [HttpPost("import")]
    [AdminOnly("權限不足，無法存取 Skill")]
    public Task<ActionResult<Skill>> Import(CancellationToken ct)
        => ImportCoreAsync(expectedName: null, ct);

    private async Task<ActionResult<Skill>> ImportCoreAsync(
        string? expectedName, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();

        if (!Request.HasFormContentType)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "匯入需以 multipart/form-data 上傳 package zip");
        }

        // transport-safe pre-check:過大 body 在轉送前就擋(不是 archive 結構限制 — 那在引擎)。
        if (Request.ContentLength is long len && len > MaxImportUploadBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "上傳的 package 過大,超過傳輸上限");
        }

        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("package") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "缺少上傳的 package 檔案");
        }

        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        var result = await _packageValidator.ValidatePackageAsync(
            bytes, file.FileName, expectedName,
            tenantId, Request.UserId(), Request.UserRole(), ct);

        if (!result.Valid)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "Skill 套件驗證失敗")
            {
                FieldErrors = ToFieldErrors(result.Errors),
            };
        }

        // 真 validator 會把這些違約擋成 502；此處也守住 fake/替代實作的同一 trust boundary。
        if (result.Skill is null || string.IsNullOrWhiteSpace(result.CanonicalDefinition))
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "Skill 套件驗證服務呼叫失敗：引擎回應違反契約");
        }

        var meta = result.Skill;
        var canonical = result.CanonicalDefinition;

        if (expectedName is null
            && (string.IsNullOrWhiteSpace(meta.Name) || !SkillNameRules.IsStandard(meta.Name)))
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                "Skill 套件驗證服務呼叫失敗：引擎回應違反契約（非法 skill.name）");
        }

        if (ReservedNames.Contains(meta.Name))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, "名稱與既有工作流同名，無法建立：" + meta.Name);
        }

        // flow 也必須保存原 package，否則任意額外 entries 無法 import→export round-trip。
        var packageSha = SkillHash.Sha256(bytes);

        var stored = await _repo.ImportAsync(
            tenantId, ToSkill(meta, canonical), bytes, packageSha, Request.UserIdOrEmpty(), ct);

        // ImportAsync 是 upsert(建立/更新/復活)→ 一律成功(2xx)。
        return Ok(stored);
    }

    /// <summary>
    /// 以 server-side snapshot 回復指定 revision，並新增一筆正常 revision（不改寫/刪除歷史）。
    /// flow revision 重新走 definition validator；agentic revision 重新走 package validator。
    /// 舊 agentic revision 若建立於 package snapshot 欄位之前，無法安全重建作者 package，固定回 409。
    /// </summary>
    [HttpPost("{name}/revisions/{revision:int}/restore")]
    [AdminOnly("權限不足，無法存取 Skill")]
    public async Task<ActionResult<Skill>> Restore(string name, int revision, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        _ = await _repo.GetAsync(tenantId, name, ct) ?? throw NotFound(name);
        var target = await _repo.GetRevisionAsync(tenantId, name, revision, ct);
        if (target is null)
        {
            throw new ApiException(
                StatusCodes.Status404NotFound, $"找不到 Skill revision：{name}#{revision}");
        }

        SkillMetadata meta;
        string canonical;
        byte[]? package;
        string? packageSha;

        if (string.Equals(target.Kind, "agentic", StringComparison.Ordinal))
        {
            if (target.Package is null)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    $"Skill revision {name}#{revision} 建立於 package 快照功能之前，無法安全回復");
            }

            var result = await _packageValidator.ValidatePackageAsync(
                target.Package, $"{name}-r{revision}.zip", name,
                tenantId, Request.UserId(), Request.UserRole(), ct);
            if (!result.Valid)
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity, "Skill revision 套件驗證失敗")
                {
                    FieldErrors = ToFieldErrors(result.Errors),
                };
            }

            meta = result.Skill!;
            canonical = result.CanonicalDefinition!;
            package = target.Package;
            packageSha = SkillHash.Sha256(package);
        }
        else
        {
            meta = await ValidateAsync(target.Definition, tenantId, ct);
            if (!string.Equals(meta.Name, name, StringComparison.Ordinal))
            {
                throw new ApiException(
                    StatusCodes.Status422UnprocessableEntity,
                    $"Skill revision 的 name 與路由不符：定義為 {meta.Name}，路由為 {name}");
            }

            canonical = target.Definition;
            package = target.Package;
            packageSha = package is null ? null : SkillHash.Sha256(package);
        }

        var restored = await _repo.ImportAsync(
            tenantId, ToSkill(meta, canonical), package, packageSha, Request.UserIdOrEmpty(), ct);
        return Ok(restored);
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

        throw new ApiException(StatusCodes.Status422UnprocessableEntity, "Skill 定義驗證失敗")
        {
            FieldErrors = ToFieldErrors(result.Errors),
        };
    }

    /// <summary>引擎錯誤清單 → fieldErrors。同一錯誤碼多次(不同行)保留第一筆,行號附在訊息尾。</summary>
    private static Dictionary<string, string> ToFieldErrors(IReadOnlyList<SkillValidationError> errors)
    {
        var fieldErrors = new Dictionary<string, string>();
        foreach (var error in errors)
        {
            var message = error.Message ?? error.Code;
            if (error.Line is int line)
            {
                message += $"（第 {line} 行）";
            }

            fieldErrors.TryAdd(error.Code, message);
        }

        return fieldErrors;
    }

    /// <summary>對既有 agentic skill(已存 package)的 definition-only 更新 → 固定 409(agentic 一律走 import),零副作用。</summary>
    private static void RejectAgenticDefinitionOnly(bool isAgentic)
    {
        if (isAgentic)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "agentic Skill 僅能透過匯入(import)建立或更新;definition-only 寫入不支援 agentic");
        }
    }

    private static ApiException NotFound(string name) => ApiErrors.NotFound(" Skill", name);

    /// <summary>DB 列 = 引擎中繼資料 + definition(flow=YAML 原文、agentic=canonical 投影)。enabled/revision/時間戳由 DB 決定。</summary>
    private static Skill ToSkill(SkillMetadata meta, string definition) => new(
        meta.Name,
        meta.Description,
        definition,
        meta.RequiredRole,
        Enabled: true,
        CurrentRevision: 0,
        CreatedAt: default,
        UpdatedAt: default,
        Kind: meta.Kind);
}
