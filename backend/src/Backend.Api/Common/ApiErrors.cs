namespace Backend.Api.Common;

/// <summary>
/// 共用商業錯誤工廠。多個 controller 的 404「找不到&lt;資源&gt;：&lt;id&gt;」形狀一致,集中一處免逐字重寫。
/// resource 自帶必要的前導空白(中文與拉丁字之間的排版空白):" Skill"/" Configuration Set"/"文件"。
/// </summary>
internal static class ApiErrors
{
    public static ApiException NotFound(string resource, object id)
        => new(404, $"找不到{resource}：{id}");

    /// <summary>
    /// 多個 controller/service 共用的欄位必填檢查:trim 後仍空白、超過 <paramref name="max"/> 字元、
    /// 或含控制字元皆視為不合法 → 400。
    /// </summary>
    public static string Required(string? value, string field, int max)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
        {
            throw new ApiException(400, $"{field} is required");
        }
        return value;
    }
}
