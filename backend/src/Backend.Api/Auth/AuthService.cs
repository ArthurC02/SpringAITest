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
        var tenant = await _repo.FindTenantByCodeAsync(request.TenantCode!, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "找不到租戶：" + request.TenantCode);

        // 2. 邀請碼必須與租戶設定相符。
        if (tenant.InviteCode != request.InviteCode)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "邀請碼無效");
        }

        // 3. 帳號不可重複。
        if (await _repo.UsernameExistsAsync(request.Username!, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "使用者名稱已存在：" + request.Username);
        }

        // 4. 存使用者:BCrypt hash、role 一律 USER、掛在該租戶下。
        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password!);
        await _repo.AddUserAsync(request.Username!, hash, "USER", tenant.Id, ct);

        return new AuthResult(request.Username!, "USER", tenant.Code);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        // 找不到使用者、或密碼比對失敗,都回同一個「帳號或密碼錯誤」(不洩漏帳號是否存在)。
        var user = await _repo.FindUserByUsernameAsync(request.Username!, ct)
            ?? throw new ApiException(StatusCodes.Status401Unauthorized, "帳號或密碼錯誤");

        if (!BCrypt.Net.BCrypt.Verify(request.Password!, user.PasswordHash))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "帳號或密碼錯誤");
        }

        return new AuthResult(user.Username, user.Role, user.TenantCode);
    }
}
