using System.Text.RegularExpressions;
using System.Text;

namespace Backend.Api.Agents;

/// <summary>
/// 契約鏡像:group id 的 regex、<see cref="MaxGroupIdLength"/>、<see cref="MaxCallerGroups"/>
/// (platform 端名為 <c>MaxGroups</c>)、<see cref="MaxGroupsWireUtf8Bytes"/> 與
/// <see cref="IsCanonicalGroupSet"/> 的四段條件,與
/// platform/src/Platform.Service/Dtos/UserContext.cs 的 <c>UserGroupContract</c> 刻意逐字一致
/// (跨服務各自部署,無法共用 assembly),改任一邊須同步另一邊 —— 這是安全 wire 契約:
/// 任一邊放寬,另一邊就會靜默丟棄整組 group(而非回錯),使用者的授權會無聲消失。
/// 兩側各有一支釘常數測試(<c>AgentAudienceTests</c> / <c>UserGroupContractTests</c>),單邊漂移會變紅。
/// </summary>
public static partial class AgentAudience
{
    public const int MaxGroupIdLength = 128;
    public const int MaxCallerGroups = 256;
    public const int MaxGroupsWireUtf8Bytes = 2_048;

    private static readonly IReadOnlySet<string> Roles =
        new HashSet<string>(StringComparer.Ordinal) { "ADMIN", "USER" };

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalGroupIdRegex();

    public static bool IsCanonicalGroupId(string? value)
        => value is not null
           && value.Length <= MaxGroupIdLength
           && CanonicalGroupIdRegex().IsMatch(value);

    public static bool IsCanonicalGroupSet(IReadOnlyCollection<string> groups)
        => groups.Count <= MaxCallerGroups
           && groups.All(IsCanonicalGroupId)
           && groups.Distinct(StringComparer.Ordinal).Count() == groups.Count
           && Encoding.UTF8.GetByteCount(string.Join(' ', groups))
           <= MaxGroupsWireUtf8Bytes;

    public static string NormalizeAuthoringEntry(string value)
    {
        var normalized = value.Trim();
        if (Roles.Contains(normalized))
        {
            return $"role:{normalized}";
        }
        if (normalized.StartsWith("role:", StringComparison.Ordinal))
        {
            var role = normalized["role:".Length..].ToUpperInvariant();
            return Roles.Contains(role) ? $"role:{role}" : normalized;
        }
        return normalized;
    }

    public static bool IsCanonicalEntry(string? value)
    {
        if (value is null)
        {
            return false;
        }
        if (value.StartsWith("role:", StringComparison.Ordinal))
        {
            return Roles.Contains(value["role:".Length..]);
        }
        return value.StartsWith("group:", StringComparison.Ordinal)
               && IsCanonicalGroupId(value["group:".Length..]);
    }

    public static bool Matches(
        IEnumerable<string> audience,
        string role,
        IReadOnlyCollection<string> groups,
        bool allowLegacyPublishedRoles)
    {
        var roleEntry = $"role:{role.ToUpperInvariant()}";
        var groupEntries = groups
            .Where(IsCanonicalGroupId)
            .Select(group => $"group:{group}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in audience)
        {
            if (string.Equals(entry, roleEntry, StringComparison.Ordinal)
                || groupEntries.Contains(entry))
            {
                return true;
            }
            if (allowLegacyPublishedRoles
                && Roles.Contains(entry)
                && string.Equals(entry, role, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
