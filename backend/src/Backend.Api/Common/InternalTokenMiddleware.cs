using System.Security.Cryptography;
using System.Text;

namespace Backend.Api.Common;

/// <summary>
/// 內部憑證守門:除 health 端點外,所有請求都必須帶 header X-Internal-Token 且與設定值相符。
/// backend 不對公網,呼叫者只有 platform 與 workflow;不符一律回 401 ApiError。
/// </summary>
public sealed class InternalTokenMiddleware
{
    public const string HeaderName = "X-Internal-Token";

    private readonly RequestDelegate _next;
    private readonly string _token;

    public InternalTokenMiddleware(RequestDelegate next, string token)
    {
        _next = next;
        _token = token;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 只有明確的 health 端點免驗,避免放寬其他 /health/* 路徑。
        if (context.Request.Path.Value is "/health" or "/health/live" or "/health/ready")
        {
            await _next(context);
            return;
        }

        // 常數時間比對 UTF-8 bytes,避免以逐字元短路洩漏 token(FixedTimeEquals 長度不符即回 false)。
        var provided = context.Request.Headers[HeaderName].ToString();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(_token)))
        {
            await ApiErrorWriter.WriteAsync(context.Response, StatusCodes.Status401Unauthorized, "內部憑證無效");
            return;
        }

        await _next(context);
    }
}
