using Microsoft.Extensions.Hosting;
using Npgsql;
using Backend.Api.Auth;

namespace Backend.Api.Common;

/// <summary>
/// Prevents the committed local-development credentials from being used outside Development.
/// Error messages identify configuration keys only; credential values must never be logged.
/// </summary>
internal static class StartupCredentialValidator
{
    public const string DevelopmentDatabaseConnectionString =
        "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=springaitest";
    public const string DevelopmentInternalToken = "internal-dev-token";
    public const string DevelopmentRabbitMqUrl = "amqp://app:app-dev-password@localhost:5672";

    public static void Validate(
        string environmentName,
        string? databaseConnectionString,
        string? internalToken,
        JwtSigningConfiguration jwtSigning,
        string? rabbitMqUrl)
    {
        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(jwtSigning.PublicKeyFingerprint, JwtSigningConfiguration.DevelopmentPublicKeyFingerprint, StringComparison.Ordinal)
            || string.Equals(jwtSigning.Issuer, JwtSigningConfiguration.DevelopmentIssuer, StringComparison.Ordinal)
            || string.Equals(jwtSigning.Audience, JwtSigningConfiguration.DevelopmentAudience, StringComparison.Ordinal)
            || string.Equals(jwtSigning.ActiveKid, JwtSigningConfiguration.DevelopmentKid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JWT asymmetric signing configuration must use non-development values outside Development.");
        }
        RequireNonDevelopmentSecret("INTERNAL_API_TOKEN", internalToken, DevelopmentInternalToken);
        ValidateDatabaseConnectionString(databaseConnectionString);
        ValidateRabbitMqUrl(rabbitMqUrl);
    }

    private static void RequireNonDevelopmentSecret(string key, string? value, string developmentDefault)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, developmentDefault, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must be configured with a non-development value outside Development.");
        }
    }

    private static void ValidateDatabaseConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) ||
            string.Equals(connectionString, DevelopmentDatabaseConnectionString, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DB_CONNECTION_STRING must be configured with non-development database credentials outside Development.");
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Username) ||
                string.IsNullOrWhiteSpace(builder.Password) ||
                (string.Equals(builder.Username, "postgres", StringComparison.Ordinal) &&
                 string.Equals(builder.Password, "postgres", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("DB_CONNECTION_STRING must be configured with non-development database credentials outside Development.");
            }
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("DB_CONNECTION_STRING must contain valid non-development database credentials outside Development.");
        }
    }

    private static void ValidateRabbitMqUrl(string? rabbitMqUrl)
    {
        if (string.IsNullOrWhiteSpace(rabbitMqUrl) ||
            string.Equals(rabbitMqUrl, DevelopmentRabbitMqUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RABBITMQ_URL must be configured with non-development RabbitMQ credentials outside Development.");
        }

        if (!Uri.TryCreate(rabbitMqUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new InvalidOperationException("RABBITMQ_URL must contain non-development RabbitMQ credentials outside Development.");
        }

        var credentials = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(credentials[0]);
        var password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty;
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password) ||
            (string.Equals(username, "app", StringComparison.Ordinal) &&
             string.Equals(password, "app-dev-password", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("RABBITMQ_URL must be configured with non-development RabbitMQ credentials outside Development.");
        }
    }
}
