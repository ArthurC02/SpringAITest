using Backend.Api.Common;

namespace Backend.Api.Auth;

/// <summary>
/// 認證服務。邏輯與訊息逐字沿用 platform 原 AuthService:BCrypt 雜湊/驗證、register role 一律 USER、
/// login 失敗不洩漏帳號是否存在。錯誤以 ApiException 帶狀態碼(404/403/409/401)。
/// </summary>
public sealed class AuthService
{
    private readonly IAuthRepository _repo;

    public AuthService(IAuthRepository repo) => _repo = repo;

    public async Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        // 1. 依 code 找租戶,找不到直接擋下。
        var tenant = await FindOrThrowAsync(
            _repo.FindTenantByCodeAsync(request.TenantCode!, ct),
            StatusCodes.Status404NotFound, "找不到租戶：" + request.TenantCode);

        // 2. 邀請碼必須與租戶設定相符。
        ThrowIf(tenant.InviteCode != request.InviteCode, StatusCodes.Status403Forbidden, "邀請碼無效");

        // 3. 帳號不可重複。
        ThrowIf(await _repo.UsernameExistsAsync(request.Username!, ct),
            StatusCodes.Status409Conflict, AuthMessages.UsernameExists(request.Username!));

        // 4. 存使用者:BCrypt hash、role 一律 USER、掛在該租戶下。
        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password!);
        await _repo.AddUserAsync(request.Username!, hash, "USER", tenant.Id, ct);

        return new AuthResult(request.Username!, "USER", tenant.Code);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        // 找不到使用者、或密碼比對失敗,都回同一個「帳號或密碼錯誤」(不洩漏帳號是否存在)。
        var user = await FindOrThrowAsync(
            _repo.FindUserByUsernameAsync(request.Username!, ct),
            StatusCodes.Status401Unauthorized, "帳號或密碼錯誤");

        ThrowIf(!BCrypt.Net.BCrypt.Verify(request.Password!, user.PasswordHash),
            StatusCodes.Status401Unauthorized, "帳號或密碼錯誤");

        return new AuthResult(user.Username, user.Role, user.TenantCode);
    }

    /// <summary>共用守衛:repo 查詢回 null 就丟對應狀態碼的 ApiException(找租戶 404 / 找使用者 401 共用此段)。</summary>
    private static async Task<T> FindOrThrowAsync<T>(Task<T?> lookup, int status, string message) where T : class
        => await lookup ?? throw new ApiException(status, message);

    /// <summary>共用守衛:條件成立就丟 ApiException(邀請碼 403 / 帳號重複 409 / 密碼錯誤 401 共用此段)。</summary>
    private static void ThrowIf(bool condition, int status, string message)
    {
        if (condition)
        {
            throw new ApiException(status, message);
        }
    }
}
