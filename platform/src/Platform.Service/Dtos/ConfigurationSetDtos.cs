using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Service.Dtos;

/// <summary>
/// Configuration Set 清單項目(不含 values 內容)。JSON snake_case,原樣轉發 backend。
/// 時間為字串,原樣轉發 backend 的值(與 SkillDtos 一致,不重新格式化)。
/// </summary>
public sealed record ConfigurationSetInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

/// <summary>
/// 完整 Configuration Set(含 values jsonb 物件)。JSON snake_case,原樣轉發 backend。
/// values 用 <see cref="JsonElement"/> 承載任意 jsonb 值 → 反序列化再序列化時原封保留
/// (數字保持數字、字串保持字串),platform 不解讀、不驗證(型別/範圍把關全在 backend §9)。
/// </summary>
public sealed record ConfigurationSet(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("values")] Dictionary<string, JsonElement> Values,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

/// <summary>
/// 建立/更新 Configuration Set 的請求 body —— 只有 name + values 兩欄。
/// is_active 刻意「不」在此(啟用走專屬 POST {id}/activate 端點,契約 §7.2);
/// 即使前端誤帶 is_active,模型也會把它丟掉,擋住「用 upsert 偷改 active」。
/// </summary>
public sealed record ConfigurationSetUpsert(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("values")] Dictionary<string, JsonElement>? Values);
