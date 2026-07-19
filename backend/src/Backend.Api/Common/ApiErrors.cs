namespace Backend.Api.Common;

/// <summary>
/// 共用商業錯誤工廠。多個 controller 的 404「找不到&lt;資源&gt;：&lt;id&gt;」形狀一致,集中一處免逐字重寫。
/// resource 自帶必要的前導空白(中文與拉丁字之間的排版空白):" Skill"/" Configuration Set"/"文件"。
/// </summary>
internal static class ApiErrors
{
    public static ApiException NotFound(string resource, object id)
        => new(404, $"找不到{resource}：{id}");
}
