using Microsoft.AspNetCore.Mvc;

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
    /// 樂觀鎖 409 + 當下最新 ETag(02-spec §5:資源仍對呼叫者可見時附帶,呼叫端不必再打一次 GET)。
    /// 刻意「不」走 ApiException/GlobalExceptionHandler:<c>UseExceptionHandler</c> 會註冊
    /// ClearCacheHeaders,在回應開始前把 ETag 清成 default —— 例外路徑上永遠帶不出這個 header。
    /// 只在「查詢已經做過、版本就在手上」的呼叫點使用,絕不為了這個 header 新增一次查詢
    /// (draft 寫入的 repo 是在區分 NotFound/Conflict 的同一趟查詢帶回版本,不是額外查詢;
    /// 真的沒有版本在手上的路徑仍照舊 throw,不帶 ETag)。
    /// </summary>
    public static ObjectResult VersionConflict(HttpContext http, string message, long currentVersion)
    {
        http.Response.SetVersionETag(currentVersion);
        return new ObjectResult(new ApiError(
            DateTime.UtcNow,
            StatusCodes.Status409Conflict,
            ApiErrorCodes.ForStatus(StatusCodes.Status409Conflict),
            message,
            http.TraceIdentifier,
            new Dictionary<string, string>()))
        {
            StatusCode = StatusCodes.Status409Conflict,
            // "application/json"(格式化器自動補 charset);寫死 charset 會讓內容協商回 406。
            ContentTypes = { "application/json" },
        };
    }

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
