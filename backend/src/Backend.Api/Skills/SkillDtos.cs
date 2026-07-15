using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Skills;

/// <summary>Skill 清單項目。JSON snake_case:{ name, description, required_role, enabled, current_revision, created_at, updated_at }
/// (刻意不含 definition — 內文只在單筆查詢回傳)。清單只列 enabled=true 的 skill,但欄位仍在。</summary>
public sealed record SkillInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("current_revision")] int CurrentRevision,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);

/// <summary>
/// Skill 完整內容(含 definition 原文)。JSON snake_case。
/// name/description/required_role 不是使用者另外填的欄位 — 它們寫在 YAML 裡,存檔時由引擎
/// validate 回報的中繼資料落欄位,DB 只是引擎解析結果的投影。
/// </summary>
public sealed record Skill(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("current_revision")] int CurrentRevision,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);

/// <summary>
/// 建立/更新 Skill 的請求 body — **只有 definition 一個欄位**(YAML 原文)。
/// name/description/required_role 全部寫在 YAML 頂層(規格 §3.1);再從 body 傳一次就是兩份事實來源。
/// </summary>
public sealed record SkillUpsert(
    [NotBlank(ErrorMessage = "definition 不可為空")]
    string? Definition);

/// <summary>skill_revision 的一列(唯讀稽核歷史)。JSON snake_case;軟刪的 skill 其 revision 仍查得到。</summary>
public sealed record SkillRevisionInfo(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);
