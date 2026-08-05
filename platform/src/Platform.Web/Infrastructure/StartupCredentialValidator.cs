using Microsoft.Extensions.Hosting;
using Platform.Web.Auth;

namespace Platform.Web.Infrastructure;

/// <summary>
/// Prevents the committed local-development credentials from being used outside Development.
/// Error messages identify configuration keys only; credential values must never be logged.
/// </summary>
internal static class StartupCredentialValidator
{
    public const string DevelopmentInternalToken = "internal-dev-token";
    public const string DevelopmentRabbitMqUrl = "amqp://app:app-dev-password@localhost:5672";

    public static void Validate(
        string environmentName,
        string? internalToken,
        JwtOptions jwtOptions,
        string? rabbitMqUrl)
    {
        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (jwtOptions.PublicKeyFingerprints.Contains(JwtOptions.DevelopmentPublicKeyFingerprint)
            || string.Equals(jwtOptions.Issuer, JwtOptions.DevelopmentIssuer, StringComparison.Ordinal)
            || string.Equals(jwtOptions.Audience, JwtOptions.DevelopmentAudience, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JWT public verification configuration must use non-development values outside Development.");
        }
        RequireNonDevelopmentSecret("INTERNAL_API_TOKEN", internalToken, DevelopmentInternalToken);
        ValidateRabbitMqUrl(rabbitMqUrl);
    }

    private static void RequireNonDevelopmentSecret(string key, string? value, string developmentDefault)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, developmentDefault, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must be configured with a non-development value outside Development.");
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
