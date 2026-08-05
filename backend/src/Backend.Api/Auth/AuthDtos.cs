using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Auth;

/// <summary>
/// 註冊請求。欄位宣告為可空字串以抑制不可空型別的隱含英文訊息,只保留指定中文驗證訊息。
/// username / tenantCode 不得含 ':':platform 的租戶隔離鍵是未逃逸的 `{tenantCode}:{userId}`,
/// 身分含 ':' 會讓兩組不同身分撞成同一把鍵(tenant "t" + user "a:b" 與 tenant "t:a" + user "b"),
/// 而短期記憶視窗與 mem0 uid 都用它 → 撞鍵即跨使用者記憶可見。鍵格式刻意不改(既有 mem0 uid 與
/// 進行中的對話視窗會全斷),改在產生這兩個值的邊界擋下;platform 衍生時另有第二道 fail-closed 守門。
/// </summary>
public sealed record RegisterRequest(
    [NotBlank(ErrorMessage = "username 不可為空")]
    [StringLength(AuthIdentityLimits.MaximumLength, ErrorMessage = "username 長度不可超過 128")]
    [RegularExpression(IdentityRules.NoColon, ErrorMessage = "username 不可包含冒號")]
    string? Username,

    [NotBlank(ErrorMessage = "password 不可為空")]
    [MinLength(8, ErrorMessage = "password 長度至少 8 碼")]
    string? Password,

    [NotBlank(ErrorMessage = "tenantCode 不可為空")]
    [StringLength(AuthIdentityLimits.MaximumLength, ErrorMessage = "tenantCode 長度不可超過 128")]
    [RegularExpression(IdentityRules.NoColon, ErrorMessage = "tenantCode 不可包含冒號")]
    string? TenantCode,

    [NotBlank(ErrorMessage = "inviteCode 不可為空")]
    string? InviteCode);

/// <summary>登入請求(同一組身分值的另一個入口,同樣不得含 ':')。</summary>
public sealed record LoginRequest(
    [NotBlank(ErrorMessage = "username 不可為空")]
    [StringLength(AuthIdentityLimits.MaximumLength, ErrorMessage = "username 長度不可超過 128")]
    [RegularExpression(IdentityRules.NoColon, ErrorMessage = "username 不可包含冒號")]
    string? Username,

    [NotBlank(ErrorMessage = "password 不可為空")]
    string? Password);

internal static class AuthIdentityLimits
{
    public const int MaximumLength = 128;
}

/// <summary>身分欄位的格式規則(register/login 共用,避免兩個入口各自維護一份 pattern)。</summary>
internal static class IdentityRules
{
    /// <summary>不含 platform 隔離鍵分隔字元 ':' 的字串([^:] 含換行,不順手收緊既有允許值)。</summary>
    public const string NoColon = @"^[^:]*\z";
}

/// <summary>
/// register 的回應 body 仍只有 { username, role, tenantCode }。
/// Capabilities 是登入後簽 JWT 的 server-side identity 資料，不直接擴張既有 public response。
/// </summary>
public sealed record AuthResult(
    string Username,
    string Role,
    string TenantCode,
    [property: JsonIgnore] IReadOnlyList<string>? Capabilities = null,
    [property: JsonIgnore] IReadOnlyList<string>? Groups = null);

/// <summary>login 的回應 body。JSON:{ token, username, role, tenantCode }。</summary>
public sealed record LoginResponse(string Token, string Username, string Role, string TenantCode);

/// <summary>租戶列(Dapper 讀取用)。</summary>
public sealed record TenantRow(long Id, string Code, string Name, string InviteCode);

/// <summary>使用者列(Dapper 讀取用,含租戶 code/capabilities 以組 JWT)。</summary>
public sealed record UserRow(
    string Username,
    string PasswordHash,
    string Role,
    string TenantCode,
    string CapabilitiesJson = "[]",
    string GroupsJson = "[]")
{
    public IReadOnlyList<string> Capabilities
        => JsonSerializer.Deserialize<string[]>(CapabilitiesJson) ?? Array.Empty<string>();

    public IReadOnlyList<string> Groups
        => JsonSerializer.Deserialize<string[]>(GroupsJson) ?? Array.Empty<string>();
}
