using System.ComponentModel.DataAnnotations;

namespace Platform.Service.Validation;

/// <summary>
/// 對應原 Java 的 <c>@NotBlank</c>:null、空字串、全空白字元皆視為不合法。
/// 內建的 <see cref="RequiredAttribute"/> 只擋 null;這裡要連「全是空白」也擋掉。
/// 與 backend/src/Backend.Api/Common/ 同名檔刻意保持一致,改任一邊須同步另一邊。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class NotBlankAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
        // 非字串一律交給其他驗證器;字串則不可為 null 或全空白。
        if (value is null)
        {
            return false;
        }

        if (value is string s)
        {
            return !string.IsNullOrWhiteSpace(s);
        }

        return true;
    }
}
