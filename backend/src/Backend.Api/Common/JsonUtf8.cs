using System.Text;
using System.Text.Json;

namespace Backend.Api.Common;

internal static class JsonUtf8
{
    public static int ByteCount(JsonElement value)
        => Encoding.UTF8.GetByteCount(value.GetRawText());

    public static bool IsNullOrWithinLimit(JsonElement? value, int maxBytes)
        => value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
           || ByteCount(value.Value) <= maxBytes;
}
