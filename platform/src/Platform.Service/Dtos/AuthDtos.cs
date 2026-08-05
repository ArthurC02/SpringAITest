using System.ComponentModel.DataAnnotations;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

/// <summary>註冊請求。欄位全部宣告為可空字串,以抑制不可空參考型別的隱含 Required 英文訊息,
/// 只保留規格指定的中文驗證訊息。</summary>
public sealed record RegisterRequest(
    [NotBlank(ErrorMessage = "username 不可為空")]
    [StringLength(AuthIdentityLimits.MaximumLength, ErrorMessage = "username 長度不可超過 128")]
    string? Username,

    [NotBlank(ErrorMessage = "password 不可為空")]
    [MinLength(8, ErrorMessage = "password 長度至少 8 碼")]
    string? Password,

    [NotBlank(ErrorMessage = "tenantCode 不可為空")]
    [StringLength(AuthIdentityLimits.MaximumLength, ErrorMessage = "tenantCode 長度不可超過 128")]
    string? TenantCode,

    [NotBlank(ErrorMessage = "inviteCode 不可為空")]
    string? InviteCode);

/// <summary>登入請求。</summary>
public sealed record LoginRequest(
    [NotBlank(ErrorMessage = "username 不可為空")]
    [StringLength(AuthIdentityLimits.MaximumLength, ErrorMessage = "username 長度不可超過 128")]
    string? Username,

    [NotBlank(ErrorMessage = "password 不可為空")]
    string? Password);

internal static class AuthIdentityLimits
{
    public const int MaximumLength = 128;
}

/// <summary>AuthService 的輸出;也是 register 的 HTTP 回應 body。JSON:{ username, role, tenantCode }。</summary>
public sealed record AuthResult(string Username, string Role, string TenantCode);

/// <summary>
/// LoginAsync 的輸出:直接帶 backend 簽發的 token(platform 不再自行簽發)。
/// controller 直接回傳(比照 Register 回 AuthResult)。JSON 對外 body:{ token, username, role, tenantCode }。
/// </summary>
public sealed record LoginResult(string Token, string Username, string Role, string TenantCode);
