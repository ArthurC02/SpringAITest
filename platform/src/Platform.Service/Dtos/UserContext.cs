namespace Platform.Service.Dtos;

/// <summary>
/// 傳給下游 Python 服務的使用者情境。
/// 注意欄位順序與 AuthResult/LoginResult 不同:(userId, tenantCode, role)。
/// 其中 <see cref="UserId"/> 帶的是 username(不是數字 id),由 controller 從 JWT 主體組出。
/// </summary>
public sealed record UserContext(string UserId, string TenantCode, string Role);
