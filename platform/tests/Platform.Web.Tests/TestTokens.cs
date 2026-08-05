using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Tests;

internal static class TestTokens
{
    public const string Issuer = "platform-tests-issuer";
    public const string Audience = "platform-tests-audience";
    public const string ActiveKid = "platform-tests-es256";
    public static TestJwtKeyPair ActiveKey { get; } = TestJwtKeyPair.Create();
    public static string PublicKeyRingJson => ActiveKey.PublicKeyRingJson(ActiveKid);

    public static string Mint(
        string username = "user-a",
        string role = "USER",
        string tenantCode = "demo-a",
        DateTime? notBefore = null,
        DateTime? expires = null,
        IReadOnlyCollection<string>? capabilities = null,
        IReadOnlyCollection<string>? groups = null,
        TestJwtKeyPair? keyPair = null,
        string? issuer = null,
        string? audience = null,
        string? kid = null)
    {
        var now = DateTime.UtcNow;
        var key = (keyPair ?? ActiveKey).CreatePrivateSecurityKey(kid ?? ActiveKid);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new("role", role),
            new("tenantCode", tenantCode),
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
            foreach (var group in groups)
            {
                claims.Add(new Claim("groups", group));
            }
        }

        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? Audience,
            claims: claims,
            notBefore: notBefore ?? now,
            expires: expires ?? now.AddHours(24),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string MintMissingChatIdentityClaim(bool omitSubject)
    {
        var now = DateTime.UtcNow;
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
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddHours(24),
            signingCredentials: new SigningCredentials(
                ActiveKey.CreatePrivateSecurityKey(ActiveKid),
                SecurityAlgorithms.EcdsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

internal sealed class TestJwtKeyPair
{
    private readonly string _privatePem;
    private readonly string _publicPemBase64;

    private TestJwtKeyPair(string privatePem, string publicPemBase64)
    {
        _privatePem = privatePem;
        _publicPemBase64 = publicPemBase64;
    }

    public static TestJwtKeyPair Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new TestJwtKeyPair(
            key.ExportPkcs8PrivateKeyPem(),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())));
    }

    public ECDsaSecurityKey CreatePrivateSecurityKey(string kid)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(_privatePem);
        return new ECDsaSecurityKey(key) { KeyId = kid };
    }

    public string PublicKeyRingJson(string kid)
        => JsonSerializer.Serialize(new Dictionary<string, string> { [kid] = _publicPemBase64 });

    public string PublicPemBase64 => _publicPemBase64;
}
