using Backend.Api.Common;

namespace Backend.Api.Config;

/// <summary>更新組態的請求 body:{ value }。</summary>
public sealed record ConfigUpdateRequest(
    [NotBlank(ErrorMessage = "value 不可為空")]
    string? Value);

/// <summary>組態項目。JSON:{ key, value, updatedAt }。</summary>
public sealed record ConfigItem(string Key, string Value, DateTime UpdatedAt);
