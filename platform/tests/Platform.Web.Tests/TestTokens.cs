using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Tests;

/// <summary>
/// 測試用的 token 鑄造器。platform 已不再簽發 token(改由 backend 簽),此處以與 backend 位元相容的格式
/// (HS256、claims sub/role/tenantCode)自行簽出,供整合測試的 Bearer 中介軟體驗證。
/// 預設 secret 與 app 在 Testing 環境使用的 JWT_SECRET 預設值一致。
/// </summary>
internal static class TestTokens
{
    public const string DefaultSecret = "dev-jwt-secret-change-me-0123456789abcdef";

    public static string Mint(
        string username = "user-a",
        string role = "USER",
        string tenantCode = "demo-a",
        string? secret = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        IReadOnlyCollection<string>? capabilities = null)
    {
        var now = DateTime.UtcNow;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? DefaultSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new("role", role),
            new("tenantCode", tenantCode),
        };
        // 與 backend JwtService 完全相同：每個 capability 產生一個同名 claim，
        // JwtSecurityTokenHandler 會把多值序列化成 JSON array；null/空值不產生 claim。
        if (capabilities is not null)
        {
            foreach (var capability in capabilities
                         .Where(c => !string.IsNullOrWhiteSpace(c))
                         .Select(c => c.Trim())
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(c => c, StringComparer.Ordinal))
            {
                claims.Add(new Claim("capabilities", capability));
            }
        }

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: notBefore ?? now,
            expires: expires ?? now.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>簽章與期限均有效、但刻意缺少聊天 identity claim 的 JWT，用來釘住 fail-closed 邊界。</summary>
    public static string MintMissingChatIdentityClaim(bool omitSubject)
    {
        var now = DateTime.UtcNow;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DefaultSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new("role", "USER") };
        if (omitSubject)
        {
            claims.Add(new("tenantCode", "demo-a"));
        }
        else
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, "user-a"));
        }

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now,
            expires: now.AddHours(24),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
