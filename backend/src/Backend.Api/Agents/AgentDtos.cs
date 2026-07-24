using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Agents;

// 契約:領域欄位一律 snake_case(仿 skills);請求與回應對稱。platform 端照抄此形狀。
// 集合欄位缺席/null/空一律 canonicalize 成空集合(fail closed,禁止 null=unrestricted,02-spec §2.1/§7.2)。

/// <summary>建立/更新 draft 的請求 body。POST 用 slug + name + 定義欄位;PUT draft 忽略 slug(slug 不可改)。
/// 集合欄位可空(缺席/null)→ canonicalize 成空陣列。business_rules 本期一律存 canonical 空 AST(忽略輸入)。</summary>
public sealed record AgentUpsert(
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("system_prompt")] string? SystemPrompt,
    [property: JsonPropertyName("execution_roles")] IReadOnlyList<string>? ExecutionRoles,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities,
    [property: JsonPropertyName("output_contract")] JsonElement? OutputContract,
    [property: JsonPropertyName("audience")] IReadOnlyList<string>? Audience,
    [property: JsonPropertyName("allowed_tools")] IReadOnlyList<string>? AllowedTools,
    [property: JsonPropertyName("skill_bindings")] IReadOnlyList<AgentSkillBinding>? SkillBindings,
    [property: JsonPropertyName("knowledge_sources")] IReadOnlyList<string>? KnowledgeSources,
    [property: JsonPropertyName("business_rules")] JsonElement? BusinessRules,
    [property: JsonPropertyName("runtime_limits")] AgentRuntimeLimits? RuntimeLimits,
    [property: JsonPropertyName("runtime_workflow")] AgentWorkflowRef? RuntimeWorkflow);

/// <summary>Skill binding:以 skill name(本 codebase 的穩定識別字)引用;revision_policy 保留欄位,
/// 本期 publish 一律固定到該 Skill 的 current published revision(D1)。position 由陣列順序決定。</summary>
public sealed record AgentSkillBinding(
    [property: JsonPropertyName("skill")] string? Skill,
    [property: JsonPropertyName("revision_policy")] string? RevisionPolicy = "latest");

/// <summary>執行期上限(工具輪數、Context 輪數、逾時、token/step budget)。缺 → 全 0(D1 不執行 run,只存形狀)。</summary>
public sealed record AgentRuntimeLimits(
    [property: JsonPropertyName("max_tool_rounds")] int MaxToolRounds = 0,
    [property: JsonPropertyName("max_context_rounds")] int MaxContextRounds = 0,
    [property: JsonPropertyName("timeout_seconds")] int TimeoutSeconds = 0,
    [property: JsonPropertyName("token_budget")] int TokenBudget = 0,
    [property: JsonPropertyName("step_budget")] int StepBudget = 0);

/// <summary>pinned agent-runtime Workflow revision 指標。</summary>
public sealed record AgentWorkflowRef(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("revision")] int Revision = 0);

/// <summary>publish 請求:必帶 expected_draft_version(對到當時 draft version 才發布,防覆蓋他人更新)。</summary>
public sealed record AgentPublishRequest(
    [property: JsonPropertyName("expected_draft_version")] long? ExpectedDraftVersion);

/// <summary>Agent 讀取模型(repo → controller)。DraftDefinition 是 canonical JSON 原文。</summary>
public sealed record Agent(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    bool Enabled,
    long DraftVersion,
    long? DraftValidatedVersion,
    int? PublishedRevision,
    string DraftDefinition,
    string DraftDefinitionSha256,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>單筆 Agent 回應(含 draft 定義原文物件)。snake_case;draft 以原文 JSON 物件輸出。</summary>
public sealed record AgentResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("draft_version")] long DraftVersion,
    [property: JsonPropertyName("draft_validated_version")] long? DraftValidatedVersion,
    [property: JsonPropertyName("published_revision")] int? PublishedRevision,
    [property: JsonPropertyName("draft")]
    [property: JsonConverter(typeof(RawJsonConverter))] string Draft,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt)
{
    public static AgentResponse From(Agent a) => new(
        a.Id, a.Slug, a.Name, a.Description, a.Enabled, a.DraftVersion,
        a.DraftValidatedVersion, a.PublishedRevision, a.DraftDefinition, a.CreatedAt, a.UpdatedAt);
}

/// <summary>清單項目(不含 draft 定義內文)。</summary>
public sealed record AgentInfo(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("draft_version")] long DraftVersion,
    [property: JsonPropertyName("draft_validated_version")] long? DraftValidatedVersion,
    [property: JsonPropertyName("published_revision")] int? PublishedRevision,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);

/// <summary>不可變 revision 摘要(含固定的 Skill bindings 與 definition hash)。</summary>
public sealed record AgentRevisionInfo(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("runtime_workflow_id")] Guid? RuntimeWorkflowId,
    [property: JsonPropertyName("runtime_workflow_revision")] int? RuntimeWorkflowRevision,
    [property: JsonPropertyName("skill_bindings")] IReadOnlyList<AgentRevisionSkillInfo> SkillBindings,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

/// <summary>revision 內固定的一筆 Skill binding。skill = 名稱,skill_revision = 發布時固定的確切 revision。</summary>
public sealed record AgentRevisionSkillInfo(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("skill_revision")] int SkillRevision,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>publish/restore 寫入層用:已解析(name → 固定 revision)的 binding。</summary>
public sealed record ResolvedSkillBinding(
    string SkillName,
    int SkillRevision,
    int Position,
    Guid SkillId);

/// <summary>validate 回應(比照 skill validation:200 {valid, errors[]})。</summary>
public sealed record AgentValidationResponse(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("errors")] IReadOnlyList<AgentValidationError> Errors);

public sealed record AgentValidationError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("message")] string Message);

/// <summary>publish/restore 寫入結果狀態。</summary>
public enum AgentWriteStatus
{
    Success,
    NotFound,
    VersionConflict,
    InvalidReference,
}

public sealed record AgentPublishResult(
    AgentWriteStatus Status,
    int Revision,
    IReadOnlyList<AgentValidationError>? Errors = null);
