namespace Backend.Api.Auth;

/// <summary>
/// 認證資料存取(薄介面,供 WebApplicationFactory 測試換 fake;SQL 正確性交給 E2E)。
/// </summary>
public interface IAuthRepository
{
    Task<TenantRow?> FindTenantByCodeAsync(string code, CancellationToken ct);

    Task<bool> UsernameExistsAsync(string username, CancellationToken ct);

    Task AddUserAsync(string username, string passwordHash, string role, long tenantId, CancellationToken ct);

    /// <summary>依 username 找使用者(含所屬租戶 code);找不到回 null。</summary>
    Task<UserRow?> FindUserByUsernameAsync(string username, CancellationToken ct);
}
