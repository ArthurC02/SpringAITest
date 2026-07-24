using Backend.Api.Auth;

namespace Backend.Api.Data.InMemory;

/// <summary>
/// 認證儲存庫的行程記憶體實作(Lite 模式,DB_PROVIDER=inmemory)。
/// 預置兩租戶與三名種子使用者,與 <see cref="DbBootstrap"/> 完全一致:密碼皆 password123 的
/// BCrypt hash(登入走 BCrypt.Verify,不比對 hash 字面值),租戶 invite code 同種子。
/// 執行緒安全:讀走無鎖 Dictionary.GetValueOrDefault,寫(AddUser)以 lock 保護。
/// </summary>
public sealed class InMemoryAuthRepository : IAuthRepository
{
    private static readonly string Password123 = BCrypt.Net.BCrypt.HashPassword("password123");

    private readonly Dictionary<string, TenantRow> _tenants = new()
    {
        ["demo-a"] = new TenantRow(1, "demo-a", "示範租戶 A", "demo-a-invite"),
        ["demo-b"] = new TenantRow(2, "demo-b", "示範租戶 B", "demo-b-invite"),
    };

    private readonly Dictionary<string, UserRow> _users = new()
    {
        // admin-a 是明確被指派的 workflow manager；ADMIN role 本身不會自動取得 capability。
        ["admin-a"] = new UserRow(
            "admin-a", Password123, "ADMIN", "demo-a", """["workflow.manage"]"""),
        ["user-a"] = new UserRow("user-a", Password123, "USER", "demo-a"),
        ["user-b"] = new UserRow("user-b", Password123, "USER", "demo-b"),
    };

    private readonly object _lockObj = new();

    public Task<TenantRow?> FindTenantByCodeAsync(string code, CancellationToken ct)
        => Task.FromResult(_tenants.GetValueOrDefault(code));

    public Task<bool> UsernameExistsAsync(string username, CancellationToken ct)
    {
        lock (_lockObj)
        {
            return Task.FromResult(_users.ContainsKey(username));
        }
    }

    public Task AddUserAsync(string username, string passwordHash, string role, long tenantId, CancellationToken ct)
    {
        lock (_lockObj)
        {
            var tenantCode = _tenants.Values.First(t => t.Id == tenantId).Code;
            _users[username] = new UserRow(
                username, passwordHash, role, tenantCode);
        }

        return Task.CompletedTask;
    }

    public Task<UserRow?> FindUserByUsernameAsync(string username, CancellationToken ct)
    {
        lock (_lockObj)
        {
            return Task.FromResult(_users.GetValueOrDefault(username));
        }
    }
}
