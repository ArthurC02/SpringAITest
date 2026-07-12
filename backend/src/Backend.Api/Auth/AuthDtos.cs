using System.ComponentModel.DataAnnotations;
using Backend.Api.Common;

namespace Backend.Api.Auth;

/// <summary>註冊請求。欄位宣告為可空字串以抑制不可空型別的隱含英文訊息,只保留指定中文驗證訊息。</summary>
public sealed record RegisterRequest(
    [NotBlank(ErrorMessage = "username 不可為空")]
    string? Username,

    [NotBlank(ErrorMessage = "password 不可為空")]
    [MinLength(8, ErrorMessage = "password 長度至少 8 碼")]
    string? Password,

    [NotBlank(ErrorMessage = "tenantCode 不可為空")]
    string? TenantCode,

    [NotBlank(ErrorMessage = "inviteCode 不可為空")]
    string? InviteCode);

/// <summary>登入請求。</summary>
public sealed record LoginRequest(
    [NotBlank(ErrorMessage = "username 不可為空")]
    string? Username,

    [NotBlank(ErrorMessage = "password 不可為空")]
    string? Password);

/// <summary>register 的回應 body。JSON:{ username, role, tenantCode }。</summary>
public sealed record AuthResult(string Username, string Role, string TenantCode);

/// <summary>login 的回應 body。JSON:{ token, username, role, tenantCode }。</summary>
public sealed record LoginResponse(string Token, string Username, string Role, string TenantCode);

/// <summary>租戶列(Dapper 讀取用)。</summary>
public sealed record TenantRow(long Id, string Code, string Name, string InviteCode);

/// <summary>使用者列(Dapper 讀取用,含租戶 code 以組回應與 JWT)。</summary>
public sealed record UserRow(string Username, string PasswordHash, string Role, string TenantCode);
