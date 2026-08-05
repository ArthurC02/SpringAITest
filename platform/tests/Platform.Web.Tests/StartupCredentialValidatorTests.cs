using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Web.Auth;
using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class StartupCredentialValidatorTests
{
    [Fact]
    public void Development_AllowsCommittedLocalDefaults()
    {
        StartupCredentialValidator.Validate(
            "Development",
            StartupCredentialValidator.DevelopmentInternalToken,
            JwtOptions.Resolve(null, null, null),
            StartupCredentialValidator.DevelopmentRabbitMqUrl);
    }

    [Theory]
    [InlineData("JWT_ISSUER")]
    [InlineData("JWT_AUDIENCE")]
    [InlineData("JWT_PUBLIC_KEY_RING_JSON")]
    public void Production_RejectsDevelopmentJwtConfiguration(string rejectedKey)
    {
        var production = ProductionJwtOptions();
        var options = rejectedKey switch
        {
            "JWT_ISSUER" => JwtOptions.Resolve(JwtOptions.DevelopmentIssuer, production.Audience, ProductionPublicKeyRing()),
            "JWT_AUDIENCE" => JwtOptions.Resolve(production.Issuer, JwtOptions.DevelopmentAudience, ProductionPublicKeyRing()),
            "JWT_PUBLIC_KEY_RING_JSON" => JwtOptions.Resolve(production.Issuer, production.Audience, null),
            _ => throw new InvalidOperationException(),
        };

        var exception = Assert.Throws<InvalidOperationException>(() => StartupCredentialValidator.Validate(
            "Production", "service-to-service-token", options, "amqp://queue-user:queue-password@rabbit.example:5672/vhost"));

        Assert.Contains("JWT", exception.Message);
    }

    [Theory]
    [InlineData("INTERNAL_API_TOKEN", false)]
    [InlineData("RABBITMQ_URL", false)]
    [InlineData("INTERNAL_API_TOKEN", true)]
    [InlineData("RABBITMQ_URL", true)]
    public void Production_RejectsCommittedDevelopmentOrBlankServiceCredentials(string rejectedKey, bool blank)
    {
        var internalToken = "service-to-service-token";
        var rabbitMqUrl = "amqp://queue-user:queue-password@rabbit.example:5672/vhost";
        switch (rejectedKey)
        {
            case "INTERNAL_API_TOKEN": internalToken = blank ? "" : StartupCredentialValidator.DevelopmentInternalToken; break;
            case "RABBITMQ_URL": rabbitMqUrl = blank ? "" : StartupCredentialValidator.DevelopmentRabbitMqUrl; break;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => StartupCredentialValidator.Validate(
            "Production", internalToken, ProductionJwtOptions(), rabbitMqUrl));

        Assert.Contains(rejectedKey, exception.Message);
    }

    [Fact]
    public void Production_AcceptsNonDevelopmentCredentials()
    {
        StartupCredentialValidator.Validate(
            "Production",
            "service-to-service-token",
            ProductionJwtOptions(),
            "amqp://queue-user:queue-password@rabbit.example:5672/vhost");
    }

    private static JwtOptions ProductionJwtOptions()
        => JwtOptions.Resolve("production-issuer", "production-audience", ProductionPublicKeyRing());

    private static string ProductionPublicKeyRing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()));
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["production-es256"] = publicKey });
    }
}
