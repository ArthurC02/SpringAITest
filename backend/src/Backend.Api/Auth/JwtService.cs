using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Api.Auth;

/// <summary>
/// HS256 JWT 簽發。金鑰用 UTF-8 bytes 當 HMAC key、claims sub/role/tenantCode + iat、exp = now+24h。
/// 與 platform 現行 JwtService 位元相容(platform 只驗不簽):claim 名稱、順序、notBefore/expires 皆一致。
/// backend 只簽發(login),不驗證(對內以 X-Internal-Token + 身分 header 授權)。
/// </summary>
public sealed class JwtService
{
    private readonly string _secret;
    private readonly TimeSpan _expiration;

    public JwtService(string secret, TimeSpan expiration)
    {
        _secret = secret;
        _expiration = expiration;
    }

    public string Issue(string username, string role, string tenantCode)
    {
        var now = DateTime.UtcNow;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new("role", role),
            new("tenantCode", tenantCode),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now,
            expires: now.Add(_expiration),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
