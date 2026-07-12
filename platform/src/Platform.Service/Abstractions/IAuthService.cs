using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>認證服務:轉呼叫 backend /api/auth/*。token 由 backend 簽發,platform 只轉發。</summary>
public interface IAuthService
{
    /// <summary>註冊新使用者;成功回 AuthResult(role 一律 "USER")。</summary>
    Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default);

    /// <summary>登入驗證;成功回 LoginResult(含 backend 簽發的 token、實際 role 與 tenantCode)。</summary>
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default);
}
