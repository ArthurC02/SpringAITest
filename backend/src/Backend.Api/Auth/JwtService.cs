using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Backend.Api.Agents;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Api.Auth;

/// <summary>
/// HS256 JWT 簽發。金鑰用 UTF-8 bytes 當 HMAC key、claims sub/role/tenantCode + iat、exp = now+24h。
/// 與 platform 現行 JwtService 位元相容(platform 只驗不簽):claim 名稱、順序、notBefore/expires 皆一致。
/// backend 只簽發(login),不驗證(對內以 X-Internal-Token + 身分 header 授權)。
/// </summary>
public sealed class JwtService
{
    public const int MaxAuthorizationValueBytes = 7 * 1_024;

    private readonly string _secret;
    private readonly TimeSpan _expiration;

    public JwtService(string secret, TimeSpan expiration)
    {
        _secret = secret;
        _expiration = expiration;
    }

    public string Issue(
        string username, string role, string tenantCode,
        IReadOnlyCollection<string>? capabilities = null,
        IReadOnlyCollection<string>? groups = null)
    {
        var now = DateTime.UtcNow;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // 既有 4 個 claim(sub/role/tenantCode/iat)名稱與順序**位元相容**:platform 只驗不簽,
        // 舊 token 一位元都不能變。capabilities(如 workflow.manage,02-spec §9)只在使用者實際具備時
        // 才**附加在最後**(空/無 → 不加任何欄位 → token 與過去逐位元相同);tenant ADMIN 不自動取得 —
        // 呼叫端只在 principal 真正持有該 capability 時才傳入。多值以重複 claim 名輸出成 JSON 陣列。
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new("role", role),
            new("tenantCode", tenantCode),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };

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

        if (groups is not null)
        {
            var groupSet = groups
                .Distinct(StringComparer.Ordinal)
                .OrderBy(group => group, StringComparer.Ordinal)
                .ToArray();
            if (!AgentAudience.IsCanonicalGroupSet(groupSet))
            {
                throw new InvalidOperationException(
                    "Persisted group membership set exceeds the signed identity contract");
            }
            foreach (var group in groupSet)
            {
                claims.Add(new Claim("groups", group));
            }
        }

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now,
            expires: now.Add(_expiration),
            signingCredentials: credentials);

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        if (Encoding.ASCII.GetByteCount("Bearer " + encoded)
            >= MaxAuthorizationValueBytes)
        {
            throw new InvalidOperationException(
                "Issued JWT exceeds the deployed Authorization header budget");
        }
        return encoded;
    }
}
