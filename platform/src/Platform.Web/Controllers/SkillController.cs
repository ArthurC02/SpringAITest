using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Web.Auth;
using Platform.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Platform.Web.Controllers;

/// <summary>
/// Skill 端點,需認證(JWT)。兩個下游、一個路由前綴:
///   CRUD + revisions → backend(:8002);catalog / validate / invoke → workflow 引擎(:8001)。
/// 一般角色把關在下游；import 另有本地 auth-first ADMIN filter，避免未授權的大型 multipart 先被解析。
/// 本層也負責:驗 JWT、從主體組 UserContext(轉發身分 header)、把下游錯誤映射成統一的 ApiError。
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
    private readonly IWorkflowEngineClient _engine;

    public SkillController(ISkillService skills, IWorkflowEngineClient engine)
    {
        _skills = skills;
        _engine = engine;
    }

    /// <summary>列出本租戶的自訂 Skill(CRUD 用清單,backend 不含 definition);原樣穿透 backend JSON。</summary>
    [HttpGet]
    public async Task<ActionResult<JsonElement>> List(CancellationToken ct)
        => Ok(await _skills.ListAsync(User.ToUserContext(), ct));

    /// <summary>
    /// 可執行 Skill 目錄:引擎合併內建 + 自訂,每筆帶 source 與 bindable metadata。
    /// Platform 原樣轉送引擎判定，不以名稱或 source 自行猜測可否綁定 Agent。
    /// </summary>
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

    /// <summary>以 backend 保存的完整 snapshot 回復指定 revision，成功後產生一筆新 revision。</summary>
    [HttpPost("{name}/revisions/{revision:int}/restore")]
    public async Task<ActionResult<JsonElement>> RestoreRevision(
        string name, int revision, CancellationToken ct)
        => Ok(await _skills.RestoreRevisionAsync(name, revision, User.ToUserContext(), ct));

    /// <summary>
    /// Agent Skill 匯入(ADMIN):以 ASP.NET 表單繫結(IFormFile)可靠地解析上傳的 multipart —— 這正是 backend 的做法。
    /// 不再代理原始 Request.Body:那條路徑在 [ApiController] MVC pipeline 下 body 會被排空,backend 收到空 multipart(400)。
    /// 拿到檔案位元組後由 SkillService 重建乾淨的 multipart 轉送。本地 authorization filter 先驗 ADMIN，
    /// 故此處用 IFormFile?(可為空)避免 [ApiController] 對缺檔的自動 400 搶在 403 之前觸發；ADMIN 缺檔再交由 backend 判。
    /// 回應原樣穿透 backend 的 Skill JSON(含 additive kind)。backend 內部端點 GET /api/skills/{name}/package 刻意不代理。
    /// </summary>
    [HttpPost("{name}/import")]
    [AdminOnly]
    [PackageTooLarge]
    [RequestSizeLimit(PackageSizeLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = PackageSizeLimitBytes)]
    public async Task<ActionResult<JsonElement>> Import(string name, IFormFile? package, CancellationToken ct)
        => Ok(await _skills.ImportAsync(
            name, await ReadPackageAsync(package, ct), PackageFileName(package),
            User.ToUserContext(), ct));

    /// <summary>Server-derived 匯入：不接受 client route name，由 backend/workflow 從 package 推導。</summary>
    [HttpPost("import")]
    [AdminOnly]
    [PackageTooLarge]
    [RequestSizeLimit(PackageSizeLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = PackageSizeLimitBytes)]
    public async Task<ActionResult<JsonElement>> Import(IFormFile? package, CancellationToken ct)
        => Ok(await _skills.ImportAsync(
            await ReadPackageAsync(package, ct), PackageFileName(package),
            User.ToUserContext(), ct));

    private const long PackageSizeLimitBytes = 17L * 1024 * 1024;

    /// <summary>
    /// 匯入路徑的上傳大小上限對外語意 = 413。沒有這一層時,超限會被 form model binding 攔下,
    /// 落到 [ApiController] 的自動 400「輸入驗證失敗」+ 空 fieldErrors,前端無從提示「檔案太大」。
    /// 刻意只掛在這兩條路由,不動全域例外對照表(那會改掉所有端點的 form/body 過大狀態碼)。
    /// resource filter 晚於所有 authorization filter,所以 USER 的超大上傳仍固定 403(不洩漏上傳限制)。
    /// 訊息與 Business Rule payload 的 413 明確可區分(那條說的是 payload,這條說的是 Skill 套件)。
    /// ponytail: 只看 Content-Length —— 瀏覽器送 FormData 一定會帶;沒帶(chunked)時仍由
    /// [RequestSizeLimit] 兜底成 400,需要 chunked 也精確時再補讀取端計數。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class PackageTooLargeAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (context.HttpContext.Request.ContentLength > PackageSizeLimitBytes)
            {
                throw new WorkflowPayloadTooLargeException(
                    $"Skill 套件超過上傳大小上限（{PackageSizeLimitBytes / (1024 * 1024)} MiB）");
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }

    private static async Task<byte[]> ReadPackageAsync(IFormFile? package, CancellationToken ct)
    {
        if (package is null)
        {
            // 缺檔:轉送空 package 讓 backend 依「角色 → 檔案」的順序判；
            // USER 已由 authorization filter 擋下，ADMIN 缺檔由 backend 回 400。
            return Array.Empty<byte>();
        }

        using var ms = new MemoryStream();
        await package.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string PackageFileName(IFormFile? package)
        => package is null || string.IsNullOrWhiteSpace(package.FileName)
            ? "package.zip"
            : package.FileName;

    /// <summary>P2–P5/C8 前相容窗口：建立 flow；新 UI 應改用 Business Workflow 面。</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SkillUpsert request, CancellationToken ct)
        => StatusCode(
            StatusCodes.Status201Created,
            await _skills.CreateAsync(request, User.ToUserContext(), ct));

    /// <summary>P2–P5/C8 前相容窗口：更新 flow；新 UI 應改用 Business Workflow 面。</summary>
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

    /// <summary>P2–P5/C8 前相容 alias；新 UI 應改用 /api/business-workflows/validate。</summary>
    [HttpPost("validate")]
    public async Task<ActionResult<JsonElement>> Validate(
        [FromBody] SkillUpsert request, CancellationToken ct)
        => Ok(await ArtifactCompatibilityUsageMetrics.TrackValidationAsync(
            HttpContext, "public_skills",
            () => _engine.ValidateSkillAsync(request.Definition!, User.ToUserContext(), ct)));

    /// <summary>執行 Skill;錯誤碼映射見 WorkflowEngineClient.MapInvokeErrorAsync。</summary>
    [HttpPost("{name}/invoke")]
    public async Task<ActionResult<JsonElement>> Invoke(
        string name, [FromBody] WorkflowInvokeRequest request, CancellationToken ct)
    {
        ArtifactCompatibilityUsageMetrics.MarkActionReached(HttpContext);
        return Ok(await _engine.InvokeSkillAsync(
            name, request.Input!, User.ToUserContext(), ArtifactUsageOrigin.PublicSkills, ct));
    }
}
