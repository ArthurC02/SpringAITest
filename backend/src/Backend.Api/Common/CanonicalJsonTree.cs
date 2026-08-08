using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.Common;

/// <summary>Canonical JSON tree ordering shared by persisted-definition contracts.</summary>
internal static class CanonicalJsonTree
{
    /// <summary>
    /// Canonical text for a request-body-bound <see cref="JsonElement"/>, i.e. the trust boundary.
    /// An absent property binds to <see cref="JsonValueKind.Undefined"/> (<c>GetRawText()</c> throws)
    /// and an explicit JSON null parses to a null <see cref="JsonNode"/> (the <c>!</c> below throws);
    /// both used to escape as a 500 *before* any validation ran. They become the JSON null literal
    /// here so the callers' own shape validators reject them as "must be an object" — the same
    /// field-named 4xx any other wrong-typed definition gets.
    /// </summary>
    public static string NormalizeBody(JsonElement value)
        => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "null"
            : Normalize(JsonNode.Parse(value.GetRawText()))!.ToJsonString();

    public static JsonNode? Normalize(JsonNode? node)
        => node switch
        {
            null => null,
            JsonObject obj => new JsonObject(
                obj.OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property => KeyValuePair.Create(
                        property.Key,
                        Normalize(property.Value)))),
            JsonArray array => new JsonArray(array.Select(Normalize).ToArray()),
            _ => node.DeepClone(),
        };
}
