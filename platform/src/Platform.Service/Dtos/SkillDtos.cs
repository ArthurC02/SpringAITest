using System.Text.Json.Serialization;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

/// <summary>Skill 清單項目(不含 definition 內文)。JSON snake_case,原樣轉發 backend。</summary>
public sealed record SkillInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("current_revision")] int CurrentRevision,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

/// <summary>完整 Skill(含 definition 原文)。JSON snake_case;時間為字串,原樣轉發 backend 的值。</summary>
public sealed record Skill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("current_revision")] int CurrentRevision,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

/// <summary>
/// 建立/更新 Skill 的請求 body — **只有 definition 一個欄位**(YAML 原文)。
/// name/description/required_role 都寫在 YAML 裡(規格 §3.1),由引擎解析後回報給 backend 落欄位:
/// platform 不解析 YAML、不複製任何驗證規則,只擋掉「完全沒帶定義」這種明顯無效的請求。
/// </summary>
public sealed record SkillUpsert(
    [property: JsonPropertyName("definition")]
    [NotBlank(ErrorMessage = "definition 不可為空")]
    string? Definition);

/// <summary>
/// Skill 匯出成 Claude Skill 格式 zip 的結果 — 原封來自 backend 的 bytes(不反序列化)。
/// ContentType 取 backend 回應的 content-type,缺則 application/zip;FileName = "&lt;name&gt;.zip"。
/// </summary>
public sealed record SkillExport(byte[] Content, string ContentType, string FileName);

/// <summary>Skill 的一筆 revision 歷史(唯讀)。JSON snake_case,原樣轉發 backend。</summary>
public sealed record SkillRevisionInfo(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] string CreatedAt);
