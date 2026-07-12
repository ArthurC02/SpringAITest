using System.ComponentModel.DataAnnotations;

namespace Backend.Api.Common;

/// <summary>
/// 對應 platform 的同名驗證:null、空字串、全空白字元皆視為不合法。
/// 內建的 <see cref="RequiredAttribute"/> 只擋 null;這裡連「全是空白」也擋掉。
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class NotBlankAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
    {
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
