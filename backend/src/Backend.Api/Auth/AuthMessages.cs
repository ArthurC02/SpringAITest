namespace Backend.Api.Auth;

/// <summary>
/// Auth 錯誤文案的共用來源 — 目前僅「使用者名稱已存在」,因它同時出現在 AuthService 的預檢
/// 與 AuthRepository 的 TOCTOU unique-violation 兜底兩處,兩處訊息必須逐字一致。
/// </summary>
internal static class AuthMessages
{
    public static string UsernameExists(string username) => "使用者名稱已存在：" + username;
}
