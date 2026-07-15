using Backend.Api.Common;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Backend.Api.Skills;

/// <summary>
/// Skill 端點的 ADMIN 把關。刻意做成 authorization filter 而非 action body 內的檢查:
/// [ApiController] 的模型驗證是 action filter,會在 action 執行前把不合法 body 短路成 400 —
/// 角色檢查若寫在 action 內,非 ADMIN 送不合法 body 會拿到 400 與完整欄位規則(可據以探測),
/// 而不是 403。authorization filter 嚴格早於所有 action filter,所以「一律 403」在任何 body 下都成立。
/// 例外沿用 ApiException → GlobalExceptionHandler,ApiError 形狀與訊息與原本一致。
/// 掛在「撰寫類」動作上(POST/PUT/DELETE/匯出);讀取類(GET 清單/單筆)開放 USER —
/// 規格 §7.2 的角色欄:GET = USER、POST/PUT/DELETE = ADMIN(執行清單要讓 USER 看得到自訂 skill)。
/// ponytail: 只掛 SkillController;ConfigController 的既有行為不在本輪範圍。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkillAdminOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.Request.UserRole() != "ADMIN")
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "權限不足，無法存取 Skill");
        }
    }
}
