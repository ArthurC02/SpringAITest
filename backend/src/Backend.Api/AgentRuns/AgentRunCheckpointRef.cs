using System.Globalization;
using System.Text.RegularExpressions;

namespace Backend.Api.AgentRuns;

internal static partial class AgentRunCheckpointRef
{
    private const int MaxLength = 160;

    [GeneratedRegex(
        @"\Av2:(?<generation>[1-9][0-9]*):(?<thread>[0-9a-f]{64}):(?<checkpoint>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex V2Pattern();

    public static bool IsValidPromotion(string? value, long leaseGeneration)
        => TryGetGeneration(value, out var parsedGeneration)
           && leaseGeneration >= 1
           && parsedGeneration == leaseGeneration;

    public static bool IsValidAtOrBefore(
        string? value,
        long currentLeaseGeneration)
        => TryGetGeneration(value, out var parsedGeneration)
           && currentLeaseGeneration >= 1
           && parsedGeneration <= currentLeaseGeneration;

    private static bool TryGetGeneration(
        string? value,
        out long parsedGeneration)
    {
        parsedGeneration = 0;
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxLength)
        {
            return false;
        }

        var match = V2Pattern().Match(value);
        return match.Success
               && long.TryParse(
                   match.Groups["generation"].Value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out parsedGeneration)
               && parsedGeneration >= 1
               && Guid.TryParseExact(match.Groups["checkpoint"].Value, "D", out var checkpoint)
               && string.Equals(
                   checkpoint.ToString("D"),
                   match.Groups["checkpoint"].Value,
                   StringComparison.Ordinal);
    }
}
