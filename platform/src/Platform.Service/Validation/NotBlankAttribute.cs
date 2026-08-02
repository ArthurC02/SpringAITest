using System.ComponentModel.DataAnnotations;

namespace Platform.Service.Validation;

/// <summary>Rejects null, empty, and whitespace-only strings.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class NotBlankAttribute : ValidationAttribute
{
    public override bool IsValid(object? value)
        => value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text));
}
