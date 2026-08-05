using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Api.Auth;

public sealed class JwtSigningConfiguration
{
    internal const string DevelopmentIssuer = "springaitest-dev-issuer";
    internal const string DevelopmentAudience = "springaitest-dev-platform";
    internal const string DevelopmentKid = "dev-es256-2026-01";
    internal const string DevelopmentPrivateKeyPemBase64 =
        "LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0tCk1JR0hBZ0VBTUJNR0J5cUdTTTQ5QWdFR0NDcUdTTTQ5QXdFSEJHMHdhd0lCQVFRZ0ViQzA0ZUNORXNTeDJDVTEKY0ZVZnlyK0R2bXVCWW1UZkRiaG9tU2RsVkRPaFJBTkNBQVJSanQ3NVV0T2RQL2dMdk40RW9rRHJhNTJXdU1CVgoxZExPU0JMSXBNVmxJSURPRUxHSEgyVlBGVW1nZGZRRktITDI4L29scFpyMGtwNkZlajFlWUVWbQotLS0tLUVORCBQUklWQVRFIEtFWS0tLS0t";
    internal static readonly string DevelopmentPublicKeyFingerprint = FingerprintDevelopmentKey();

    private JwtSigningConfiguration(
        string issuer,
        string audience,
        string activeKid,
        ECDsaSecurityKey signingKey,
        string publicKeyFingerprint)
    {
        Issuer = issuer;
        Audience = audience;
        ActiveKid = activeKid;
        SigningKey = signingKey;
        PublicKeyFingerprint = publicKeyFingerprint;
    }

    public string Issuer { get; }
    public string Audience { get; }
    public string ActiveKid { get; }
    public ECDsaSecurityKey SigningKey { get; }
    public string PublicKeyFingerprint { get; }

    public static JwtSigningConfiguration Resolve(
        string? issuer,
        string? audience,
        string? activeKid,
        string? privateKeyPemBase64)
    {
        issuer ??= DevelopmentIssuer;
        audience ??= DevelopmentAudience;
        activeKid ??= DevelopmentKid;
        privateKeyPemBase64 ??= DevelopmentPrivateKeyPemBase64;
        ValidateText("JWT_ISSUER", issuer, 256);
        ValidateText("JWT_AUDIENCE", audience, 256);
        ValidateText("JWT_ACTIVE_KID", activeKid, 128);

        var pem = DecodeBase64("JWT_PRIVATE_KEY_PEM_BASE64", privateKeyPemBase64);
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException("JWT_PRIVATE_KEY_PEM_BASE64 must contain a valid EC private PEM key.");
        }

        if (!HasPrivateKey(ecdsa) || ecdsa.KeySize != 256)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException("JWT_PRIVATE_KEY_PEM_BASE64 must contain a P-256 private key.");
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()));
        var key = new ECDsaSecurityKey(ecdsa) { KeyId = activeKid };
        return new JwtSigningConfiguration(issuer, audience, activeKid, key, fingerprint);
    }

    private static bool HasPrivateKey(ECDsa key)
    {
        try
        {
            return key.ExportParameters(includePrivateParameters: true).D is { Length: > 0 };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string FingerprintDevelopmentKey()
    {
        var configuration = Resolve(null, null, null, null);
        try
        {
            return configuration.PublicKeyFingerprint;
        }
        finally
        {
            configuration.SigningKey.ECDsa.Dispose();
        }
    }

    private static string DecodeBase64(string key, string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{key} must be valid base64-encoded PEM.");
        }
    }

    private static void ValidateText(string key, string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{key} is invalid.");
        }
    }
}
