using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Web.Auth;

public sealed class JwtOptions
{
    internal const string DevelopmentIssuer = "springaitest-dev-issuer";
    internal const string DevelopmentAudience = "springaitest-dev-platform";
    internal const string DevelopmentKid = "dev-es256-2026-01";
    internal const string DevelopmentPublicKeyPemBase64 =
        "LS0tLS1CRUdJTiBQVUJMSUMgS0VZLS0tLS0KTUZrd0V3WUhLb1pJemowQ0FRWUlLb1pJemowREFRY0RRZ0FFVVk3ZStWTFRuVC80Qzd6ZUJLSkE2MnVkbHJqQQpWZFhTemtnU3lLVEZaU0NBemhDeGh4OWxUeFZKb0hYMEJTaHk5dlA2SmFXYTlKS2VoWG85WG1CRlpnPT0KLS0tLS1FTkQgUFVCTElDIEtFWS0tLS0t";
    internal static readonly string DevelopmentPublicKeyFingerprint = FingerprintDevelopmentKey();

    private JwtOptions(
        string issuer,
        string audience,
        IReadOnlyDictionary<string, ECDsaSecurityKey> verificationKeys,
        IReadOnlySet<string> publicKeyFingerprints)
    {
        Issuer = issuer;
        Audience = audience;
        VerificationKeys = verificationKeys;
        PublicKeyFingerprints = publicKeyFingerprints;
    }

    public string Issuer { get; }
    public string Audience { get; }
    public IReadOnlyDictionary<string, ECDsaSecurityKey> VerificationKeys { get; }
    internal IReadOnlySet<string> PublicKeyFingerprints { get; }

    public static JwtOptions Resolve(string? issuer, string? audience, string? publicKeyRingJson)
    {
        issuer ??= DevelopmentIssuer;
        audience ??= DevelopmentAudience;
        publicKeyRingJson ??= $"{{\"{DevelopmentKid}\":\"{DevelopmentPublicKeyPemBase64}\"}}";
        ValidateText("JWT_ISSUER", issuer, 256);
        ValidateText("JWT_AUDIENCE", audience, 256);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(publicKeyRingJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("JWT_PUBLIC_KEY_RING_JSON must be a valid JSON object.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("JWT_PUBLIC_KEY_RING_JSON must be a JSON object.");
            }

            var keys = new Dictionary<string, ECDsaSecurityKey>(StringComparer.Ordinal);
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                ValidateText("JWT public key kid", property.Name, 128);
                if (keys.Count >= 8)
                {
                    throw new InvalidOperationException("JWT_PUBLIC_KEY_RING_JSON may contain at most 8 keys.");
                }
                if (property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not { } encodedPem)
                {
                    throw new InvalidOperationException("JWT_PUBLIC_KEY_RING_JSON values must be base64-encoded PEM strings.");
                }

                var ecdsa = ParsePublicKey("JWT public key " + property.Name, encodedPem);
                var key = new ECDsaSecurityKey(ecdsa) { KeyId = property.Name };
                if (!keys.TryAdd(property.Name, key))
                {
                    ecdsa.Dispose();
                    throw new InvalidOperationException("JWT_PUBLIC_KEY_RING_JSON contains duplicate kid values.");
                }
                fingerprints.Add(Fingerprint(ecdsa));
            }

            if (keys.Count == 0)
            {
                throw new InvalidOperationException("JWT_PUBLIC_KEY_RING_JSON must contain at least one key.");
            }

            return new JwtOptions(issuer, audience, keys, fingerprints);
        }
    }

    private static ECDsa ParsePublicKey(string configKey, string encodedPem)
    {
        string pem;
        try
        {
            pem = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPem));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{configKey} must be valid base64-encoded PEM.");
        }

        if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{configKey} must not contain private key material.");
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException($"{configKey} must contain a valid EC public PEM key.");
        }

        if (HasPrivateKey(ecdsa))
        {
            ecdsa.Dispose();
            throw new InvalidOperationException($"{configKey} must not contain private key material.");
        }
        if (ecdsa.KeySize != 256)
        {
            ecdsa.Dispose();
            throw new InvalidOperationException($"{configKey} must contain a P-256 public key.");
        }
        return ecdsa;
    }

    private static string Fingerprint(ECDsa key)
        => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    private static string FingerprintDevelopmentKey()
    {
        using var key = ParsePublicKey("development JWT public key", DevelopmentPublicKeyPemBase64);
        return Fingerprint(key);
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
