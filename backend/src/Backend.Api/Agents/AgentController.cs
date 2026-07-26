using Backend.Api.Common;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
    // Agent Rule AST v1 沒有 persisted gate；D2 以 action/tool policy 最保守且可執行的 gate 驗證。
    // 若 D3 需要同一 Agent 保存多 gate rules，必須升 AST version，不可偷加未版本化欄位。
    private const string AgentRuleGate = "pre-action";
    private readonly IAgentRepository _repo;
    private readonly IBusinessRuleValidator _ruleValidator;

    public AgentController(IAgentRepository repo, IBusinessRuleValidator ruleValidator)
    {
        _repo = repo;
        _ruleValidator = ruleValidator;
    }

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
        var slug = RequireCanonicalSlug(request.Slug);
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
        var expectedVersion = Request.RequireIfMatchVersion();

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
        var expectedVersion = Request.RequireIfMatchVersion();
        var agent = await _repo.GetAsync(tenantId, id, ct) ?? throw NotFound(id);
        if (agent.DraftVersion != expectedVersion)
        {
            throw VersionConflict();
        }

        var lifecycleDefinition =
            AgentCanonicalizer.CanonicalizeForLifecycleWrite(agent.DraftDefinition);
        var errors = AgentCanonicalizer.Validate(lifecycleDefinition, agent.Name).ToList();
        errors.AddRange(await _repo.ValidateReferencesAsync(tenantId, lifecycleDefinition, ct));
        var ruleValidation = await ValidateBusinessRulesAsync(lifecycleDefinition, tenantId, ct);
        errors.AddRange(ruleValidation.Errors);
        if (errors.Count == 0)
        {
            if (!await _repo.MarkValidatedAsync(
                    tenantId,
                    id,
                    expectedVersion,
                    ruleValidation.CanonicalDefinition,
                    SkillHash.Sha256(ruleValidation.CanonicalDefinition),
                    ct))
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
        var ifMatchVersion = Request.RequireIfMatchVersion();
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
        var lifecycleDefinition =
            AgentCanonicalizer.CanonicalizeForLifecycleWrite(agent.DraftDefinition);
        var definitionErrors =
            AgentCanonicalizer.Validate(lifecycleDefinition, agent.Name);
        if (definitionErrors.Count > 0)
        {
            throw InvalidDefinition(definitionErrors);
        }

        // Workflow 的 registry/validator 可能在 validate 與 publish 之間升級；publish 必須再次以正式
        // evaluator contract fail closed，而不能只相信先前的 validated_version。
        var ruleValidation = await ValidateBusinessRulesAsync(lifecycleDefinition, tenantId, ct);
        if (ruleValidation.Errors.Count > 0)
        {
            throw InvalidBusinessRules(ruleValidation.Errors);
        }
        var result = await _repo.PublishAsync(
            tenantId,
            id,
            expectedVersion,
            ruleValidation.CanonicalDefinition,
            SkillHash.Sha256(ruleValidation.CanonicalDefinition),
            Request.UserIdOrEmpty(),
            ct);

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

    /// <summary>
    /// rollback:把指定舊 revision 經目前 Workflow Rule contract 重新驗證/正規化後發布為新 revision。
    /// 舊 revision 永不改寫；Workflow 不可達或舊 AST 已不合法時 fail closed。
    /// </summary>
    [HttpPost("{id:guid}/revisions/{revision:int}/restore")]
    public async Task<ActionResult<AgentResponse>> Restore(Guid id, int revision, CancellationToken ct)
    {
        var tenantId = Request.RequireTenant();
        var agent = await _repo.GetAsync(tenantId, id, ct) ?? throw NotFound(id);
        var sourceDefinition = await _repo.GetRevisionDefinitionAsync(tenantId, id, revision, ct)
                               ?? throw new ApiException(
                                   StatusCodes.Status404NotFound,
                                   $"找不到 Agent revision：{id}#{revision}");
        var lifecycleDefinition =
            AgentCanonicalizer.CanonicalizeForLifecycleWrite(sourceDefinition);
        var definitionErrors =
            AgentCanonicalizer.Validate(lifecycleDefinition, agent.Name);
        if (definitionErrors.Count > 0)
        {
            throw InvalidDefinition(definitionErrors);
        }

        var ruleValidation = await ValidateBusinessRulesAsync(lifecycleDefinition, tenantId, ct);
        if (ruleValidation.Errors.Count > 0)
        {
            throw InvalidBusinessRules(ruleValidation.Errors);
        }

        var result = await _repo.RestoreAsync(
            tenantId,
            id,
            revision,
            ruleValidation.CanonicalDefinition,
            SkillHash.Sha256(ruleValidation.CanonicalDefinition),
            Request.UserIdOrEmpty(),
            ct);
        if (result.Status == AgentWriteStatus.NotFound)
        {
            throw new ApiException(StatusCodes.Status404NotFound, $"找不到 Agent revision：{id}#{revision}");
        }
        if (result.Status == AgentWriteStatus.InvalidReference)
        {
            throw InvalidDefinition(result.Errors ?? Array.Empty<AgentValidationError>());
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

    private ActionResult<AgentResponse> WithETag(Agent agent)
    {
        SetETag(agent.DraftVersion);
        return Ok(AgentResponse.From(agent));
    }

    private void SetETag(long draftVersion) => Response.SetVersionETag(draftVersion);

    private static string Require(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ApiException(StatusCodes.Status400BadRequest, $"{field} 不可為空")
            : value.Trim();

    /// <summary>
    /// slug 格式驗證,比照 <see cref="AgentAudience"/> 的 canonical group id(regex + 長度上限)。
    /// slug 不進 URL/檔案路徑/canonical 定義(路由一律 uuid),所以這不是路徑穿越防護,而是
    /// UNIQUE(tenant_id, slug) 這個顯示鍵的形狀約束。
    /// **只在 create 驗證**:slug 是 immutable(PUT 忽略、所有 UPDATE 不含該欄、canonical 定義排除),
    /// 而 validate/publish/restore 驗的是 Name —— 既有不合規的 Agent 因此不會在任何其他路徑上被追溯打爆。
    /// </summary>
    private static string RequireCanonicalSlug(string? value)
    {
        var slug = Require(value, "slug");
        return AgentAudience.IsCanonicalGroupId(slug)
            ? slug
            : throw new ApiException(StatusCodes.Status400BadRequest, "slug 格式不正確");
    }

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

    private static ApiException InvalidDefinition(IReadOnlyList<AgentValidationError> errors)
        => new(StatusCodes.Status422UnprocessableEntity, "Agent execution snapshot contract 驗證失敗")
        {
            FieldErrors = errors
                .GroupBy(e => e.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal),
        };

    private async Task<AgentRuleValidation> ValidateBusinessRulesAsync(
        string canonicalDefinition,
        string tenantId,
        CancellationToken ct)
    {
        using var definition = System.Text.Json.JsonDocument.Parse(canonicalDefinition);
        var ruleSet = definition.RootElement.GetProperty("business_rules").Clone();
        var result = await _ruleValidator.ValidateAsync(
            AgentRuleGate,
            ruleSet,
            new BusinessRuleReferenceCatalog(
                AgentCanonicalizer.SkillBindingsOf(canonicalDefinition)
                    .Select(binding => binding.Skill!)
                    .ToList(),
                AgentCanonicalizer.AllowedToolsOf(canonicalDefinition)),
            tenantId,
            Request.UserIdOrEmpty(),
            Request.UserRole(),
            ct);

        var errors = result.Errors.Select(error => new AgentValidationError(
            BusinessRulePath(error.Path),
            error.Message,
            error.Code)).ToList();
        var normalizedDefinition = result.Valid && result.CanonicalRuleSet is JsonElement canonicalRuleSet
            ? AgentCanonicalizer.WithBusinessRules(canonicalDefinition, canonicalRuleSet)
            : canonicalDefinition;
        return new AgentRuleValidation(normalizedDefinition, errors);
    }

    private static string BusinessRulePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
        {
            return "business_rules";
        }

        var suffix = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('.');
        if (suffix.StartsWith("ruleSet.", StringComparison.Ordinal))
        {
            suffix = suffix["ruleSet.".Length..];
        }
        return suffix.StartsWith("business_rules", StringComparison.Ordinal)
            ? suffix
            : "business_rules." + suffix;
    }

    private static ApiException InvalidBusinessRules(IReadOnlyList<AgentValidationError> errors)
        => new(StatusCodes.Status422UnprocessableEntity, "Business Rule 驗證失敗")
        {
            FieldErrors = errors
                .GroupBy(e => e.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Message, StringComparer.Ordinal),
        };

    private sealed record AgentRuleValidation(
        string CanonicalDefinition,
        IReadOnlyList<AgentValidationError> Errors);
}
