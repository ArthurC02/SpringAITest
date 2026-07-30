using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

// Skill 清單(GET /api/skills)與 revision 歷史(GET /api/skills/{name}/revisions)改為原樣穿透
// backend JSON(snake_case),不再套 SkillInfo/SkillRevisionInfo DTO(見 SkillService / ISkillService)
// ——避免 backend 新增欄位被靜默吃掉。單筆 GET 亦穿透;唯 Create/Update 的回應仍用下面的 Skill 型別。

/// <summary>完整 Skill(含 definition 原文)。JSON snake_case;時間為字串,原樣轉發 backend 的值。
/// 供 Create/Update 的回應型別使用(讀取端點已改穿透)。</summary>
public sealed record Skill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("current_revision")] int CurrentRevision,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("kind")]
    [property: JsonRequired]
    string Kind,
    [property: JsonPropertyName("simpleForm")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? SimpleForm = null);

/// <summary>
/// 建立/更新 Skill 的請求 body — **只有 definition 一個欄位**(YAML 原文)。
/// name/description/required_role 都寫在 YAML 裡(規格 §3.1),由引擎解析後回報給 backend 落欄位:
/// platform 不解析 YAML、不複製任何驗證規則,只擋掉「完全沒帶定義」這種明顯無效的請求。
/// </summary>
public sealed record SkillUpsert(
    [property: JsonPropertyName("definition")]
    [NotBlank(ErrorMessage = "definition 不可為空")]
    string? Definition,
    // 選填:簡單模式表單狀態(opaque JSON)。platform 不解析,原樣穿透給 backend 存取
    // (強型別 DTO 若不含此欄會靜默吃掉它)。無則省略,不送 null。
    [property: JsonPropertyName("simpleForm")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? SimpleForm = null);

/// <summary>
/// Skill 匯出成 Claude Skill 格式 zip 的結果 — 原封來自 backend 的 bytes(不反序列化)。
/// ContentType 取 backend 回應的 content-type,缺則 application/zip;FileName = "&lt;name&gt;.zip"。
/// </summary>
public sealed record SkillExport(byte[] Content, string ContentType, string FileName);

/// <summary>建立結果連同 backend Location；Platform 必須把 Location 轉送給瀏覽器。</summary>
public sealed record BusinessWorkflowCreated(Skill Workflow, string? Location);
