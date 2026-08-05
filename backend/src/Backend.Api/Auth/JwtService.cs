using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Backend.Api.Agents;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Api.Auth;

/// <summary>Issues ES256 JWTs with strict issuer, audience and active key id.</summary>
public sealed class JwtService
{
    public const int MaxAuthorizationValueBytes = 7 * 1_024;

    private readonly JwtSigningConfiguration _configuration;
    private readonly TimeSpan _expiration;

    public JwtService(JwtSigningConfiguration configuration, TimeSpan expiration)
    {
        _configuration = configuration;
        _expiration = expiration;
    }

    public string Issue(
        string username, string role, string tenantCode,
        IReadOnlyCollection<string>? capabilities = null,
        IReadOnlyCollection<string>? groups = null)
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(
            _configuration.SigningKey,
            SecurityAlgorithms.EcdsaSha256);
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
            issuer: _configuration.Issuer,
            audience: _configuration.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(_expiration),
            signingCredentials: credentials);

        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        if (Encoding.ASCII.GetByteCount("Bearer " + encoded) >= MaxAuthorizationValueBytes)
        {
            throw new InvalidOperationException(
                "Issued JWT exceeds the deployed Authorization header budget");
        }
        return encoded;
    }
}
