using Microsoft.AspNetCore.Mvc.Filters;

namespace Backend.Api.Common;

/// <summary>
/// 資源中性的 ADMIN 把關。刻意做成 authorization filter 而非 action body 內的檢查:
/// [ApiController] 的模型驗證是 action filter,會在 action 執行前把不合法 body 短路成 400 —
/// 角色檢查若寫在 action 內,非 ADMIN 送不合法 body 會拿到 400 與完整欄位規則(可據以探測),
/// 而不是 403。authorization filter 嚴格早於所有 action filter,所以「一律 403」在任何 body 下都成立。
/// 例外沿用 ApiException → GlobalExceptionHandler,ApiError 形狀一致。
/// 訊息預設資源中性;需要資源專屬文案(如 Skill)時衍生並在建構子傳入。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class AdminOnlyAttribute : Attribute, IAuthorizationFilter
{
    private readonly string _message;

    public AdminOnlyAttribute(string message = "權限不足，需要管理員權限") => _message = message;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.Request.UserRole() != "ADMIN")
        {
            throw new ApiException(StatusCodes.Status403Forbidden, _message);
        }
    }
}
