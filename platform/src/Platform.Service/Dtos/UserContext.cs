namespace Platform.Service.Dtos;

/// <summary>
/// 傳給下游 Python 服務的使用者情境。
/// 注意欄位順序與 AuthResult/LoginResult 不同:(userId, tenantCode, role)。
/// 其中 <see cref="UserId"/> 帶的是 username(不是數字 id),由 controller 從 JWT 主體組出。
/// </summary>
public sealed record UserContext(string UserId, string TenantCode, string Role)
{
    /// <summary>
    /// 租戶隔離鍵 <c>{tenantCode}:{userId}</c> 的單一真理來源(安全敏感,格式只此一份)。
    /// AG-UI 的 session isolation key 與 <c>/api/chat*</c> 已登入時的記憶 uid 都用它,
    /// 兩處不得各自手拼字串(改格式只改這裡)。
    /// </summary>
    public string IsolationKey => $"{TenantCode}:{UserId}";
}
