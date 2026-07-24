using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Skills;

/// <summary>Skill 清單項目。JSON snake_case:{ name, description, required_role, enabled, current_revision, created_at, updated_at }
/// (刻意不含 definition — 內文只在單筆查詢回傳)。清單只列 enabled=true 的 skill,但欄位仍在。
/// simpleForm 例外用 camelCase(對齊 auth/config 命名側;skills CRUD 是 backend 自有 API),無則省略。</summary>
public sealed record SkillInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required_role")] string RequiredRole,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("current_revision")] int CurrentRevision,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("kind")] string Kind = "flow",
    [property: JsonPropertyName("simpleForm")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? SimpleForm = null);

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
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,
    [property: JsonPropertyName("kind")] string Kind = "flow",
    // 匯入 package 的原始 zip bytes(flow/agentic 都可有；definition-only flow 為 null)。
    // **永不序列化**：package 不得出現在任何公開 JSON。export 與內部 package 端點讀它。
    [property: JsonIgnore] byte[]? Package = null,
    // 簡單模式表單狀態(opaque JSON:{ templateId, form })。UI 便利欄,非權威定義(權威仍是 definition YAML);
    // backend 不解析/不驗證內容,整包當 jsonb 原樣存取。只有 Create/Update 帶入;Import/Restore 不帶不清。無則省略。
    [property: JsonPropertyName("simpleForm")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonConverter(typeof(RawJsonConverter))]
    string? SimpleForm = null);

/// <summary>
/// 建立/更新 Skill 的請求 body — **只有 definition 一個欄位**(YAML 原文)。
/// name/description/required_role 全部寫在 YAML 頂層(規格 §3.1);再從 body 傳一次就是兩份事實來源。
/// </summary>
public sealed record SkillUpsert(
    [NotBlank(ErrorMessage = "definition 不可為空")]
    string? Definition,
    // 選填:簡單模式表單狀態(opaque JSON)。backend 不解析內容;缺席或顯式 null 皆保留既有值
    // (寫入層 COALESCE / ?? 保留 — 前端永遠帶完整值,清空語意不由 backend 聰明推斷)。
    [property: JsonPropertyName("simpleForm")] JsonElement? SimpleForm = null);

/// <summary>skill_revision 的一列(唯讀稽核歷史)。JSON snake_case;軟刪的 skill 其 revision 仍查得到。</summary>
public sealed record SkillRevisionInfo(
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("definition_sha256")] string DefinitionSha256,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("kind")] string Kind = "flow",
    [property: JsonPropertyName("has_package")] bool HasPackage = false,
    // 匯入 revision 記 package SHA-256；definition-only flow 為 null。
    [property: JsonPropertyName("package_sha256")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PackageSha256 = null);

/// <summary>
/// Server-side restore 使用的完整 revision。Package 僅在 backend 內部流動，不會直接序列化。
/// 舊 agentic revision 在加入 package snapshot 前可能為 null；controller 會回 409，避免錯誤回復。
/// </summary>
public sealed record StoredSkillRevision(
    int Revision,
    string Definition,
    string DefinitionSha256,
    string CreatedBy,
    DateTime CreatedAt,
    string Kind,
    byte[]? Package,
    string? PackageSha256);
