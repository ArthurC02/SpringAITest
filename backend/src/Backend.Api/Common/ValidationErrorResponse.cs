using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Backend.Api.Common;

/// <summary>
/// [ApiController] 驗證失敗時的回應工廠:輸出 ApiError(400,"輸入驗證失敗",fieldErrors)。
/// fieldErrors 的 key 轉 camelCase(並剝掉可能的 "$." 前綴),每個欄位取第一個錯誤訊息。
/// 與 platform 完全一致。
/// </summary>
public static class ValidationErrorResponse
{
    public static IActionResult Create(ActionContext context)
    {
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

        var error = new ApiError(DateTime.UtcNow, StatusCodes.Status400BadRequest, "輸入驗證失敗", fieldErrors);
        // 用 "application/json"(格式化器會自動補上 charset=utf-8);
        // 不可寫成 "application/json; charset=utf-8",否則內容協商找不到格式化器會回 406。
        return new ObjectResult(error)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/json" },
        };
    }

    private static string ToCamelCaseKey(string key)
    {
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
