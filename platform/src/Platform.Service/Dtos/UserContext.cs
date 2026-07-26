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
    /// 分隔字元不逃逸,所以身分本身含 <c>:</c> 會撞鍵(tenant <c>t</c> + user <c>a:b</c> 與
    /// tenant <c>t:a</c> + user <c>b</c> 同為 <c>t:a:b</c>)—— 短期視窗與 mem0 uid 都吃這把鍵,
    /// 撞鍵即跨使用者記憶可見。鍵格式刻意不改(既有 mem0 uid 與進行中的對話視窗會全斷),
    /// 改成兩道牆:backend 的註冊/登入邊界擋下含 <c>:</c> 的身分,這裡是衍生時的 fail-closed 守門。
    /// 只檢查**身分欄位**;組合後的 conversationId 本來就含 <c>:</c>,不在此列。
    /// </summary>
    public string IsolationKey => TenantCode.Contains(':') || UserId.Contains(':')
        ? throw new InvalidOperationException("身分含隔離鍵分隔字元「:」,拒絕衍生記憶鍵")
        : $"{TenantCode}:{UserId}";
}

/// <summary>
/// Bounds for signed capability claims. Kept next to <see cref="UserGroupContract"/> because both
/// describe how much issuer-controlled identity data Platform will accept on the wire, and the two
/// sets of numbers must stay comparable.
/// </summary>
public static class UserCapabilityContract
{
    public const int MaxCapabilities = 256;
    public const int MaxCapabilitiesWireUtf8Bytes = 2_048;
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
