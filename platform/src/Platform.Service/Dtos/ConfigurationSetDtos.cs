using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Service.Dtos;

// Configuration Set 清單(GET /api/configuration-sets)與單筆(GET /{id})改為原樣穿透 backend JSON
// (snake_case),不再套 ConfigurationSetInfo DTO(見 ConfigurationSetService / IConfigurationSetService)
// ——舊 ConfigurationSetInfo 會丟掉 backend 回的 created_at,穿透後補回。唯 Create/Update/Activate 的回應
// 仍用下面的 ConfigurationSet 型別。

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
