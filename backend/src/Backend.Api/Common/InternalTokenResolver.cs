namespace Backend.Api.Common;

/// <summary>
/// 解析 INTERNAL_API_TOKEN 設定值。
/// 未設(null)→ 沿用 dev 預設(零設定啟動);顯式設為空/空白 → fail-fast 拒絕啟動
/// (空字串會讓 InternalTokenMiddleware 的 FixedTimeEquals 對「未帶 header」的請求也比對通過,等同關閉服務間信任邊界)。
/// </summary>
public static class InternalTokenResolver
{
    public const string DevDefault = "internal-dev-token";

    public static string Resolve(string? configuredValue)
    {
        var token = configuredValue ?? DevDefault;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "INTERNAL_API_TOKEN 不可為空字串(留空等於關閉服務間信任邊界);請設定非空 token 或移除該環境變數以使用 dev 預設。");
        }

        return token;
    }
}
