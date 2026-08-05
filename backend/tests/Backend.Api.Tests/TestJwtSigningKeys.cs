using System.Security.Cryptography;
using System.Text;
using Backend.Api.Auth;

namespace Backend.Api.Tests;

internal static class TestJwtSigningKeys
{
    public const string Issuer = "backend-tests-issuer";
    public const string Audience = "backend-tests-platform";
    public const string ActiveKid = "backend-tests-es256";
    private static readonly string PrivateKeyPemBase64 = CreatePrivateKeyPemBase64();

    public static JwtSigningConfiguration Configuration(
        string? issuer = null,
        string? audience = null,
        string? kid = null,
        string? privateKeyPemBase64 = null)
        => JwtSigningConfiguration.Resolve(
            issuer ?? Issuer,
            audience ?? Audience,
            kid ?? ActiveKid,
            privateKeyPemBase64 ?? PrivateKeyPemBase64);

    public static string PrivateKeySetting => PrivateKeyPemBase64;

    private static string CreatePrivateKeyPemBase64()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()));
    }
}
