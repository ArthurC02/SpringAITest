using System.Text.RegularExpressions;
using System.Text;

namespace Platform.Service.Dtos;

/// <summary>
/// 傳給下游 Python 服務的使用者情境。
/// 注意欄位順序與 AuthResult/LoginResult 不同:(userId, tenantCode, role)。
/// 其中 <see cref="UserId"/> 帶的是 username(不是數字 id),由 controller 從 JWT 主體組出。
/// </summary>
public sealed record UserContext(
    string UserId,
    string TenantCode,
    string Role,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<string>? Groups = null)
{
    /// <summary>
    /// 租戶隔離鍵 <c>{tenantCode}:{userId}</c> 的單一真理來源(安全敏感,格式只此一份)。
    /// AG-UI 的 session isolation key 與 <c>/api/chat*</c> 已登入時的記憶 uid 都用它,
    /// 兩處不得各自手拼字串(改格式只改這裡)。
    /// </summary>
    public string IsolationKey => $"{TenantCode}:{UserId}";
}

/// <summary>Shared Platform-side validation for signed D3 group membership claims.</summary>
public static partial class UserGroupContract
{
    public const int MaxGroups = 256;
    public const int MaxGroupIdLength = 128;
    public const int MaxGroupsWireUtf8Bytes = 2_048;

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?\\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalGroupIdRegex();

    public static bool IsCanonicalGroupId(string? value)
        => value is not null
           && value.Length <= MaxGroupIdLength
           && CanonicalGroupIdRegex().IsMatch(value);

    public static bool IsCanonicalGroupSet(IReadOnlyCollection<string> groups)
        => groups.Count <= MaxGroups
           && groups.All(IsCanonicalGroupId)
           && groups.Distinct(StringComparer.Ordinal).Count() == groups.Count
           && Encoding.UTF8.GetByteCount(string.Join(' ', groups))
           <= MaxGroupsWireUtf8Bytes;
}
