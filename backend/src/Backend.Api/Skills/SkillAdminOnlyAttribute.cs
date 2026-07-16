using Backend.Api.Common;

namespace Backend.Api.Skills;

/// <summary>
/// Skill 端點的 ADMIN 把關 — <see cref="AdminOnlyAttribute"/> 帶 Skill 專屬文案的衍生型。
/// 授權階段、ApiError 形狀與 403 訊息(「權限不足,無法存取 Skill」)與原本一致。
/// 掛在「撰寫類」動作上(POST/PUT/DELETE/匯出);讀取類(GET 清單/單筆)開放 USER —
/// 規格 §7.2 的角色欄:GET = USER、POST/PUT/DELETE = ADMIN(執行清單要讓 USER 看得到自訂 skill)。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkillAdminOnlyAttribute : AdminOnlyAttribute
{
    public SkillAdminOnlyAttribute() : base("權限不足，無法存取 Skill") { }
}
