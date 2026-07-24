using Backend.Api.Common;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Agents;

/// <summary>
/// Agent Registry(D1,02-spec §8)。路由鍵用 id(uuid);回應領域欄位 snake_case(仿 skills)。
/// 全部 `/api/agents*` 都是 Builder 管理 API，class-level ADMIN-only；未來 USER catalog 必須另開
/// audience-filtered、published-only、redacted DTO，不能重用含 draft 的管理端點。
/// [AdminOnly] 於 authorization filter 階段早於模型驗證，非 ADMIN 送不合法 body 仍是 403。
/// 每條查詢以 X-Tenant-Id 過濾:跨租戶一律「不存在」(404),不洩漏存在性。
/// draft optimistic concurrency 以 ETag(= draft_version)/If-Match 表達;stale → 409(02-spec §2.2)。
/// </summary>
[ApiController]
[Route("api/agents")]
[AdminOnly(Message)]
public sealed class AgentController : ControllerBase
{
    private readonly IAgentRepository _repo;

    public AgentController(IAgentRepository repo) => _repo = repo;

    /// <summary>列出本租戶所有 Agent(含已停用;enabled 欄位區分狀態)。</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AgentInfo>>> List(CancellationToken ct)
        => Ok(await _repo.ListAsync(Request.RequireTenant(), ct));

    /// <summary>取單筆 Agent(含 draft 定義);回應帶 ETag = draft_version。</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AgentResponse>> Get(Guid id, CancellationToken ct)
    {
        var agent = await _repo.GetAsync(Request.RequireTenant(), id, ct) ?? throw NotFound(id);
        return WithETag(agent);
    }

    /// <summary>建立 Agent — 201(draft_version 1、尚無 published revision)。slug 同租戶重複 → 409。</summary>
    [HttpPost]
    public async Task<ActionResult<AgentResponse>> Create([FromBody] AgentUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var slug = Require(request.Slug, "slug");
        var name = Require(request.Name, "name");

        var canonical = AgentCanonicalizer.Canonicalize(request);
        var created = await _repo.CreateAsync(
            tenantId, slug, name, request.Description ?? string.Empty,
            canonical, SkillHash.Sha256(canonical), Request.UserIdOrEmpty(), ct);
        if (created is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "Agent slug 已存在：" + slug);
        }

        SetETag(created.DraftVersion);
        return Created($"/api/agents/{created.Id}", AgentResponse.From(created));
    }

    /// <summary>
    /// 更新 draft — 200,draft_version +1、draft_validated_version 清空(改過就要重新驗證)。
    /// 必帶 If-Match = 當前 draft_version;版本不符(stale)→ 409,不覆蓋他人更新(A-DATA-08)。slug 不可改(忽略 body slug)。
    /// </summary>
    [HttpPut("{id:guid}/draft")]
    public async Task<ActionResult<AgentResponse>> UpdateDraft(
        Guid id, [FromBody] AgentUpsert request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var name = Require(request.Name, "name");
        var expectedVersion = ExpectedVersionFromIfMatch();

        var canonical = AgentCanonicalizer.Canonicalize(request);
        var result = await _repo.UpdateDraftAsync(
            tenantId, id, expectedVersion, name, request.Description ?? string.Empty,
            canonical, SkillHash.Sha256(canonical), ct);

        return result.Status switch
        {
            AgentWriteStatus.NotFound => throw NotFound(id),
            AgentWriteStatus.VersionConflict => throw VersionConflict(),
            _ => WithETag(result.Agent!),
        };
    }

    /// <summary>
    /// 驗證指定 ETag 的 draft。valid → 只在 draft_version 仍相同時記錄 validated version；
    /// 讀取後若被其他 ADMIN 修改，統一回 409，不回誤導性的 valid=true。
    /// </summary>
    [HttpPost("{id:guid}/validate")]
    public async Task<ActionResult<AgentValidationResponse>> Validate(Guid id, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var expectedVersion = ExpectedVersionFromIfMatch();
        var agent = await _repo.GetAsync(tenantId, id, ct) ?? throw NotFound(id);
        if (agent.DraftVersion != expectedVersion)
        {
            throw VersionConflict();
        }

        var errors = AgentCanonicalizer.Validate(agent.DraftDefinition).ToList();
        errors.AddRange(await _repo.ValidateReferencesAsync(tenantId, agent.DraftDefinition, ct));
        if (errors.Count == 0)
        {
            if (!await _repo.MarkValidatedAsync(tenantId, id, expectedVersion, ct))
            {
                throw VersionConflict();
            }
        }

        SetETag(expectedVersion);
        return Ok(new AgentValidationResponse(errors.Count == 0, errors));
    }

    /// <summary>
    /// 發布 — 建立不可變 revision,固定每個 Skill binding 到其 current published revision 與 definition hash。
    /// 必帶 If-Match 與 expected_draft_version，兩者必須相同；版本不符或「draft 未重新驗證」
    /// → 409(A-DATA-09)。綁定的 Skill 不存在/已停用 → 422。
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<AgentResponse>> Publish(
        Guid id, [FromBody] AgentPublishRequest request, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var ifMatchVersion = ExpectedVersionFromIfMatch();
        if (request.ExpectedDraftVersion is not long expectedVersion)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "publish 必須帶 expected_draft_version");
        }
        if (ifMatchVersion != expectedVersion)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "If-Match 與 expected_draft_version 必須指向同一個 draft version");
        }

        var agent = await _repo.GetAsync(tenantId, id, ct) ?? throw NotFound(id);
        if (agent.DraftVersion != expectedVersion)
        {
            throw VersionConflict();
        }

        if (agent.DraftValidatedVersion != agent.DraftVersion)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict, "draft 尚未重新驗證,無法發布(請先呼叫 validate)");
        }

        var result = await _repo.PublishAsync(
            tenantId, id, expectedVersion, Request.UserIdOrEmpty(), ct);

        return result.Status switch
        {
            AgentWriteStatus.NotFound => throw NotFound(id),
            AgentWriteStatus.VersionConflict => throw VersionConflict(),
            AgentWriteStatus.InvalidReference => throw InvalidReferences(result.Errors),
            _ => WithETag((await _repo.GetAsync(tenantId, id, ct))!),
        };
    }

    /// <summary>唯讀 revision 歷史(依 revision 遞減,含固定 skill bindings 與 definition hash)。</summary>
    [HttpGet("{id:guid}/revisions")]
    public async Task<ActionResult<IReadOnlyList<AgentRevisionInfo>>> Revisions(Guid id, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        _ = await _repo.GetAsync(tenantId, id, ct) ?? throw NotFound(id);
        return Ok(await _repo.ListRevisionsAsync(tenantId, id, ct));
    }

    /// <summary>rollback:把指定舊 revision 重新發布為新 revision(不改寫歷史,A-DATA-06)。</summary>
    [HttpPost("{id:guid}/revisions/{revision:int}/restore")]
    public async Task<ActionResult<AgentResponse>> Restore(Guid id, int revision, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        _ = await _repo.GetAsync(tenantId, id, ct) ?? throw NotFound(id);

        var result = await _repo.RestoreAsync(tenantId, id, revision, Request.UserIdOrEmpty(), ct);
        if (result.Status == AgentWriteStatus.NotFound)
        {
            throw new ApiException(StatusCodes.Status404NotFound, $"找不到 Agent revision：{id}#{revision}");
        }

        return WithETag((await _repo.GetAsync(tenantId, id, ct))!);
    }

    /// <summary>軟停用(enabled=false)— 204;不存在(含跨租戶)→ 404。revision 保留。</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await _repo.SetEnabledAsync(Request.RequireTenant(), id, false, ct))
        {
            throw NotFound(id);
        }

        return NoContent();
    }

    /// <summary>重新啟用(enabled=true)— 200;不存在(含跨租戶)→ 404。</summary>
    [HttpPost("{id:guid}/enable")]
    public async Task<ActionResult<AgentResponse>> Enable(Guid id, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        if (!await _repo.SetEnabledAsync(tenantId, id, true, ct))
        {
            throw NotFound(id);
        }

        return WithETag((await _repo.GetAsync(tenantId, id, ct))!);
    }

    private const string Message = "權限不足，無法存取 Agent";

    private long ExpectedVersionFromIfMatch()
    {
        var raw = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ApiException(
                StatusCodes.Status428PreconditionRequired, "缺少 If-Match 標頭:draft 寫入必須帶當前 ETag");
        }

        if (!long.TryParse(raw.Trim().Trim('"'), out var version))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "If-Match 標頭格式不正確");
        }

        return version;
    }

    private ActionResult<AgentResponse> WithETag(Agent agent)
    {
        SetETag(agent.DraftVersion);
        return Ok(AgentResponse.From(agent));
    }

    private void SetETag(long draftVersion)
        => Response.Headers.ETag = $"\"{draftVersion}\"";

    private static string Require(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ApiException(StatusCodes.Status400BadRequest, $"{field} 不可為空")
            : value.Trim();

    private static ApiException NotFound(Guid id) => ApiErrors.NotFound(" Agent", id);

    private static ApiException VersionConflict()
        => new(StatusCodes.Status409Conflict, "draft 版本衝突:已被他人更新,請重新載入");

    private static ApiException InvalidReferences(IReadOnlyList<AgentValidationError>? errors)
    {
        var list = errors ?? Array.Empty<AgentValidationError>();
        return new ApiException(StatusCodes.Status422UnprocessableEntity, "Agent reference 驗證失敗")
        {
            FieldErrors = list
                .GroupBy(e => e.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal),
        };
    }
}
