using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Api.Common;

/// <summary>
/// opaque JSON 在 DB 邊界以「原始 JSON 文字」流動(jsonb::text ↔ string),但對外/對內都必須是 JSON 值
/// 而非被逃脫的字串。此 converter 讓 string 屬性序列化為原文 JSON、反序列化回原文 JSON 文字(不解析內容)。
/// 由 skill(simple_form)與 agent(draft/output_contract/business_rules)共用。
/// </summary>
public sealed class RawJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null
            ? null
            : JsonDocument.ParseValue(ref reader).RootElement.GetRawText();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteRawValue(value);
}
