using System.ComponentModel.DataAnnotations;
using Platform.Service.Validation;

namespace Platform.Service.Dtos;

/// <summary>更新組態的請求 body:{ value }。</summary>
public sealed record ConfigUpdateRequest(
    [NotBlank(ErrorMessage = "value 不可為空")]
    string? Value);

/// <summary>組態項目。原樣轉發 backend。JSON:{ key, value, updatedAt }(camelCase)。</summary>
public sealed record ConfigItem(string Key, string Value, DateTime UpdatedAt);
