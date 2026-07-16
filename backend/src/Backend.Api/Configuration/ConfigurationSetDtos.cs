using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Configuration;

/// <summary>
/// Configuration Set 清單項目。JSON snake_case:{ id, name, is_active, created_at, updated_at }
/// 刻意**不含** values — 內文只在單筆查詢回傳(比照 SkillInfo 不帶 definition)。
/// </summary>
public sealed record ConfigurationSetInfo(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);

/// <summary>
/// Configuration Set 完整內容(含 values)。JSON snake_case。
/// values 用單一 jsonb 承載一組覆寫鍵(model 為 string、其餘 number);寫前逐鍵型別/範圍驗證
/// (ConfigurationValues,設計 §9)。回傳時 values 內的數字/字串維持原生 JSON 型別。
/// </summary>
public sealed record ConfigurationSet(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("values")] Dictionary<string, object> Values,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt);

/// <summary>
/// 建立/更新 Configuration Set 的請求 body:name(必填)+ values(覆寫鍵)。
/// is_active **不由 upsert 帶** — 啟用走專屬 POST {id}/activate 端點(否則兩條路徑都能改 active,
/// 「至多一 active」就有兩個寫入點)。
/// </summary>
public sealed record ConfigurationSetUpsert(
    [property: JsonPropertyName("name")]
    [NotBlank(ErrorMessage = "name 不可為空")]
    string? Name,
    [property: JsonPropertyName("values")] Dictionary<string, object>? Values);
