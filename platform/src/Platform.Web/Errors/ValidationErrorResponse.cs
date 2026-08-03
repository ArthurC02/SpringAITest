using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Web.Errors;

/// <summary>
/// [ApiController] 驗證失敗時的回應工廠:輸出 ApiError(400,"輸入驗證失敗",fieldErrors)。
/// fieldErrors 的 key 轉 camelCase(並剝掉可能的 "$." 前綴),每個欄位取第一個錯誤訊息。
/// 與 backend/src/Backend.Api/Common/ 同名檔刻意保持一致,改任一邊須同步另一邊。
/// </summary>
public static class ValidationErrorResponse
{
    public static IActionResult Create(ActionContext context)
    {
        // 契約鏡像:與 backend/src/Backend.Api/Common/ 同名檔為刻意重複(跨服務各自部署,無法共用 assembly)。修改 422/ApiError 格式化邏輯時務必同步另一邊。
        var fieldErrors = new Dictionary<string, string>();
        foreach (var (key, entry) in context.ModelState)
        {
            if (entry.Errors.Count == 0)
            {
                continue;
            }

            var field = ToCamelCaseKey(key);
            if (!string.IsNullOrEmpty(field) && !fieldErrors.ContainsKey(field))
            {
                fieldErrors[field] = entry.Errors[0].ErrorMessage;
            }
        }

        var error = new ApiError(
            DateTime.UtcNow,
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.ForStatus(StatusCodes.Status400BadRequest),
            "輸入驗證失敗",
            context.HttpContext.TraceIdentifier,
            fieldErrors);
        // 用 "application/json"(JSON 格式化器會自動補上 charset=utf-8);
        // 不可寫成 "application/json; charset=utf-8",否則內容協商找不到對應格式化器會回 406。
        return new ObjectResult(error)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/json" },
        };
    }

    private static string ToCamelCaseKey(string key)
    {
        // 契約鏡像:與 backend/src/Backend.Api/Common/ 同名檔為刻意重複(跨服務各自部署,無法共用 assembly)。修改 422/ApiError 格式化邏輯時務必同步另一邊。
        var k = key;
        if (k.StartsWith("$.", StringComparison.Ordinal))
        {
            k = k[2..];
        }
        else if (k == "$")
        {
            k = string.Empty;
        }

        return string.IsNullOrEmpty(k) ? k : JsonNamingPolicy.CamelCase.ConvertName(k);
    }
}
