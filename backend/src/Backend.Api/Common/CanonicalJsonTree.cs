using System.Text.Json.Nodes;

namespace Backend.Api.Common;

/// <summary>Canonical JSON tree ordering shared by persisted-definition contracts.</summary>
internal static class CanonicalJsonTree
{
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
