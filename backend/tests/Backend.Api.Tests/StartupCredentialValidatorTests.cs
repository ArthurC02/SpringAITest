using Backend.Api.Auth;
using Backend.Api.Common;

namespace Backend.Api.Tests;

public sealed class StartupCredentialValidatorTests
{
    [Fact]
    public void Development_AllowsCommittedLocalDefaults()
    {
        StartupCredentialValidator.Validate(
            "Development",
            StartupCredentialValidator.DevelopmentDatabaseConnectionString,
            StartupCredentialValidator.DevelopmentInternalToken,
            JwtSigningConfiguration.Resolve(null, null, null, null),
            StartupCredentialValidator.DevelopmentRabbitMqUrl);
    }

    [Theory]
    [InlineData("JWT_ISSUER")]
    [InlineData("JWT_AUDIENCE")]
    [InlineData("JWT_ACTIVE_KID")]
    [InlineData("JWT_PRIVATE_KEY_PEM_BASE64")]
    public void Production_RejectsDevelopmentJwtConfiguration(string rejectedKey)
    {
        var production = TestJwtSigningKeys.Configuration(
            issuer: "production-issuer",
            audience: "production-audience",
            kid: "production-es256");
        var signing = rejectedKey switch
        {
            "JWT_ISSUER" => TestJwtSigningKeys.Configuration(JwtSigningConfiguration.DevelopmentIssuer, production.Audience, production.ActiveKid),
            "JWT_AUDIENCE" => TestJwtSigningKeys.Configuration(production.Issuer, JwtSigningConfiguration.DevelopmentAudience, production.ActiveKid),
            "JWT_ACTIVE_KID" => TestJwtSigningKeys.Configuration(production.Issuer, production.Audience, JwtSigningConfiguration.DevelopmentKid),
            "JWT_PRIVATE_KEY_PEM_BASE64" => JwtSigningConfiguration.Resolve(production.Issuer, production.Audience, production.ActiveKid, null),
            _ => throw new InvalidOperationException(),
        };

        var values = SafeValues();
        var exception = Assert.Throws<InvalidOperationException>(() => StartupCredentialValidator.Validate(
            "Production", values.DatabaseConnectionString, values.InternalToken, signing, values.RabbitMqUrl));

        Assert.Contains("JWT", exception.Message);
    }

    [Theory]
    [InlineData("INTERNAL_API_TOKEN", false)]
    [InlineData("DB_CONNECTION_STRING", false)]
    [InlineData("RABBITMQ_URL", false)]
    [InlineData("INTERNAL_API_TOKEN", true)]
    [InlineData("DB_CONNECTION_STRING", true)]
    [InlineData("RABBITMQ_URL", true)]
    public void Production_RejectsCommittedDevelopmentOrBlankServiceCredentials(string rejectedKey, bool blank)
    {
        var values = SafeValues();
        switch (rejectedKey)
        {
            case "INTERNAL_API_TOKEN": values.InternalToken = blank ? "" : StartupCredentialValidator.DevelopmentInternalToken; break;
            case "DB_CONNECTION_STRING": values.DatabaseConnectionString = blank ? "" : StartupCredentialValidator.DevelopmentDatabaseConnectionString; break;
            case "RABBITMQ_URL": values.RabbitMqUrl = blank ? "" : StartupCredentialValidator.DevelopmentRabbitMqUrl; break;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => StartupCredentialValidator.Validate(
            "Production", values.DatabaseConnectionString, values.InternalToken, ProductionSigning(), values.RabbitMqUrl));

        Assert.Contains(rejectedKey, exception.Message);
    }

    [Fact]
    public void Production_AcceptsNonDevelopmentCredentials()
    {
        var values = SafeValues();

        StartupCredentialValidator.Validate(
            "Production", values.DatabaseConnectionString, values.InternalToken, ProductionSigning(), values.RabbitMqUrl);
    }

    private static JwtSigningConfiguration ProductionSigning()
        => TestJwtSigningKeys.Configuration("production-issuer", "production-audience", "production-es256");

    private static CredentialValues SafeValues() => new(
        "Host=db.example;Port=5432;Username=app-user;Password=app-password;Database=appdb",
        "service-to-service-token",
        "amqp://queue-user:queue-password@rabbit.example:5672/vhost");

    private sealed class CredentialValues(
        string databaseConnectionString,
        string internalToken,
        string rabbitMqUrl)
    {
        public string DatabaseConnectionString { get; set; } = databaseConnectionString;
        public string InternalToken { get; set; } = internalToken;
        public string RabbitMqUrl { get; set; } = rabbitMqUrl;
    }
}
