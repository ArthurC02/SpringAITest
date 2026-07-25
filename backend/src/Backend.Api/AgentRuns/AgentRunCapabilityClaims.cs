using Backend.Api.Common;

namespace Backend.Api.AgentRuns;

/// <summary>
/// D3 caller-grant claim contract v1. Claims are issued from persisted user capabilities and
/// forwarded by Platform from the authenticated JWT. Matching is ordinal and exact: ADMIN never
/// implies a grant, wildcards are forbidden, and unrelated capabilities do not grant runtime
/// access.
/// </summary>
internal static class AgentRunCapabilityClaims
{
    public const int ContractVersion = 1;
    public const string ToolPrefix = "tool.use:";
    public const string KnowledgePrefix = "knowledge.read:";

    internal sealed record Grants(
        IReadOnlySet<string> ToolNames,
        IReadOnlySet<string> KnowledgeSourceIds);

    public static Grants Parse(IEnumerable<string>? capabilityClaims)
    {
        var tools = new HashSet<string>(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in capabilityClaims ?? Array.Empty<string>())
        {
            if (claim.StartsWith(ToolPrefix, StringComparison.Ordinal))
            {
                var toolName = claim[ToolPrefix.Length..];
                if (!IsCanonicalToolName(toolName))
                {
                    throw Malformed(claim);
                }
                tools.Add(toolName);
                continue;
            }

            if (claim.StartsWith(KnowledgePrefix, StringComparison.Ordinal))
            {
                var sourceId = claim[KnowledgePrefix.Length..];
                if (!Guid.TryParseExact(sourceId, "D", out var parsed)
                    || !string.Equals(sourceId, parsed.ToString("D"), StringComparison.Ordinal))
                {
                    throw Malformed(claim);
                }
                sources.Add(sourceId);
                continue;
            }

            // Reserve both v1 namespaces. Misspelled separators, wildcard forms, or truncated
            // names fail closed instead of being silently treated as some future capability.
            if (claim.StartsWith("tool.use", StringComparison.Ordinal)
                || claim.StartsWith("knowledge.read", StringComparison.Ordinal))
            {
                throw Malformed(claim);
            }
        }
        return new Grants(tools, sources);
    }

    private static bool IsCanonicalToolName(string value)
    {
        if (value.Length is < 1 or > 256 || !IsAsciiAlphaNumeric(value[0]))
        {
            return false;
        }
        return value.All(ch => IsAsciiAlphaNumeric(ch) || ch is '.' or '_' or '-');
    }

    private static bool IsAsciiAlphaNumeric(char value)
        => value is >= 'a' and <= 'z'
           or >= 'A' and <= 'Z'
           or >= '0' and <= '9';

    private static ApiException Malformed(string claim)
        => new(
            StatusCodes.Status400BadRequest,
            $"Malformed Agent runtime capability claim: {claim}");
}
