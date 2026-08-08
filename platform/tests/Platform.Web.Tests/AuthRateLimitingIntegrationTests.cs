using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class AuthRateLimitingIntegrationTests
{
    [Fact]
    public async Task TestingEnvironment_DisablesAuthRateLimit()
    {
        await using var factory = new TestWebAppFactory();
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.LoginAccountPermitLimit + 1; i++)
        {
            var response = await LoginAsync(client, "SameUser");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Login_NormalizesAccount_AndReturnsStandard429ApiErrorAtBoundary()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        using var client = factory.CreateClient();
        var spellings = new[] { "RateUser", " rateuser", "RATEUSER ", "RateUser", " rateUSER " };

        foreach (var username in spellings)
        {
            var allowed = await LoginAsync(client, username);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var rejected = await LoginAsync(client, "rateuser");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var body = await rejected.ReadJsonAsync();
        body.AssertApiError(429, "rate_limited");
        Assert.Equal("請求過於頻繁，請稍後再試", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_InvalidModelStillConsumesRateLimitBeforeValidationShortCircuit()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.LoginAccountPermitLimit; i++)
        {
            var invalid = await LoginAsync(client, " ");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        var rejected = await LoginAsync(client, " ");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Register_PartitionsByNormalizedTenantAndUsername()
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.RegisterAccountPermitLimit; i++)
        {
            var allowed = await RegisterAsync(client, "demo-a", " NewUser ");
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        var rejected = await RegisterAsync(client, "DEMO-A", "newuser");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        var otherTenant = await RegisterAsync(client, "demo-b", "newuser");
        Assert.Equal(HttpStatusCode.Created, otherTenant.StatusCode);
    }

    [Fact]
    public async Task TrustedProxy_UsesForwardedClientIpAsPartition()
    {
        await using var factory = new TestWebAppFactory(
            new() { ["TRUSTED_PROXY_CIDR"] = "10.253.254.0/28" },
            environment: "RateLimitingTesting",
            remoteIpAddress: IPAddress.Parse("10.253.254.2"));
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.ClientIpPermitLimit; i++)
        {
            var allowed = await LoginAsync(client, "trusted-a-" + i, "198.51.100.10");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var exhausted = await LoginAsync(client, "trusted-a-over", "198.51.100.10");
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        var otherClient = await LoginAsync(client, "trusted-b", "198.51.100.11");
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task UntrustedPeer_CannotSplitBudgetWithSpoofedForwardedIps()
    {
        await using var factory = new TestWebAppFactory(
            new() { ["TRUSTED_PROXY_CIDR"] = "10.253.254.0/28" },
            environment: "RateLimitingTesting",
            remoteIpAddress: IPAddress.Parse("203.0.113.20"));
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.ClientIpPermitLimit; i++)
        {
            var allowed = await LoginAsync(client, "untrusted-" + i, $"198.51.100.{i + 1}");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var rejected = await LoginAsync(client, "untrusted-over", "192.0.2.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/login/")]
    [InlineData("/api/auth/register/")]
    public async Task TrailingSlashAlias_UsesSameIpBudgetAndRejectsNPlusOne(string aliasPath)
    {
        await using var factory = new TestWebAppFactory(environment: "RateLimitingTesting");
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.ClientIpPermitLimit; i++)
        {
            var allowed = aliasPath.Contains("login", StringComparison.Ordinal)
                ? await LoginAsync(client, "alias-login-" + i, path: aliasPath)
                : await RegisterAsync(client, "demo-a", "alias-register-" + i, aliasPath);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var rejected = aliasPath.Contains("login", StringComparison.Ordinal)
            ? await LoginAsync(client, "alias-login-over", path: aliasPath)
            : await RegisterAsync(client, "demo-a", "alias-register-over", aliasPath);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task FreshIp_AuthBodyAboveLimit_ReturnsCanonical413_WithoutBackendCall()
    {
        var auth = new RecordingAuthService();
        await using var factory = new TestWebAppFactory(
            environment: "RateLimitingTesting",
            authServiceOverride: auth);
        using var client = factory.CreateClient();
        var oversizedJson = "{\"username\":\"" + new string('u', AuthRequestBodyLimitMiddleware.MaximumBodyBytes)
            + "\",\"password\":\"password123\"}";

        using var knownLength = new StringContent(oversizedJson, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/auth/login", knownLength);

        Assert.Equal((HttpStatusCode)413, response.StatusCode);
        var body = await response.ReadJsonAsync();
        body.AssertApiError(413, "payload_too_large");
        Assert.Equal(AuthRequestBodyLimitMiddleware.RejectionMessage, body["message"]!.GetValue<string>());
        Assert.Equal(0, auth.Calls);

        using var unknownLength = new UnknownLengthContent(Encoding.UTF8.GetBytes(oversizedJson));
        var chunked = await client.PostAsync("/api/auth/login/", unknownLength);
        Assert.Equal((HttpStatusCode)413, chunked.StatusCode);
        (await chunked.ReadJsonAsync()).AssertApiError(413, "payload_too_large");
        Assert.Equal(0, auth.Calls);
    }

    [Theory]
    [InlineData(false, 0, HttpStatusCode.OK)]
    [InlineData(false, 1, (HttpStatusCode)413)]
    [InlineData(true, 0, HttpStatusCode.OK)]
    [InlineData(true, 1, (HttpStatusCode)413)]
    public async Task AuthBodyLimit_KnownAndUnknownLength_EnforcesNAndNPlusOne(
        bool unknownLength,
        int bytesOverLimit,
        HttpStatusCode expected)
    {
        var auth = new RecordingAuthService();
        await using var factory = new TestWebAppFactory(authServiceOverride: auth);
        using var client = factory.CreateClient();
        const string prefix = "{\"username\":\"body-boundary\",\"password\":\"password123\",\"padding\":\"";
        const string suffix = "\"}";
        var paddingBytes = AuthRequestBodyLimitMiddleware.MaximumBodyBytes
            + bytesOverLimit
            - Encoding.UTF8.GetByteCount(prefix)
            - Encoding.UTF8.GetByteCount(suffix);
        var payload = Encoding.UTF8.GetBytes(prefix + new string('p', paddingBytes) + suffix);
        Assert.Equal(AuthRequestBodyLimitMiddleware.MaximumBodyBytes + bytesOverLimit, payload.Length);

        using HttpContent content = unknownLength
            ? new UnknownLengthContent(payload)
            : new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await client.PostAsync("/api/auth/login", content);
        Assert.Equal(expected, response.StatusCode);
        if ((int)expected == 413)
        {
            var body = await response.ReadJsonAsync();
            body.AssertApiError(413, "payload_too_large");
            Assert.Equal(AuthRequestBodyLimitMiddleware.RejectionMessage, body["message"]!.GetValue<string>());
            Assert.Equal(0, auth.Calls);
        }
        else
        {
            Assert.Equal(1, auth.Calls);
        }
    }

    [Fact]
    public async Task ExhaustedIpBudget_RejectsBeforeAccountHashingOrLargeBodyProcessing()
    {
        var hasher = new RecordingHasher();
        var limiter = new AuthRateLimiter(enabled: true, hasher: hasher);
        await using var factory = new TestWebAppFactory(
            environment: "RateLimitingTesting",
            authRateLimiterOverride: limiter);
        using var client = factory.CreateClient();

        for (var i = 0; i < AuthRateLimiter.ClientIpPermitLimit; i++)
        {
            var allowed = await LoginAsync(client, "ip-first-" + i);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        hasher.Calls.Clear();
        var oversizedIdentifier = await LoginAsync(
            client,
            new string('u', AuthRateLimiter.MaximumIdentifierLength + 4096));
        Assert.Equal(HttpStatusCode.TooManyRequests, oversizedIdentifier.StatusCode);
        Assert.Equal(new[] { "client-ip" }, hasher.Calls.Select(call => call.Domain));

        hasher.Calls.Clear();
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = new StringContent(new string('x', 32 * 1024), Encoding.UTF8, "application/json"),
        };
        var malformedResponse = await client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.TooManyRequests, malformedResponse.StatusCode);
        Assert.Equal(new[] { "client-ip" }, hasher.Calls.Select(call => call.Domain));
    }

    [Fact]
    public async Task OversizedAuthIdentifiers_Return400AtDtoBoundary()
    {
        await using var factory = new TestWebAppFactory();
        using var client = factory.CreateClient();
        var oversized = new string('a', AuthRateLimiter.MaximumIdentifierLength + 1);

        var login = await LoginAsync(client, oversized);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.NotNull((await login.ReadJsonAsync())["fieldErrors"]!["username"]);

        var register = await RegisterAsync(client, oversized, "new-user");
        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.NotNull((await register.ReadJsonAsync())["fieldErrors"]!["tenantCode"]);
    }

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string username,
        string? forwardedFor = null,
        string path = "/api/auth/login")
        => SendAsync(
            client,
            path,
            new { username, password = "password123" },
            forwardedFor);

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string tenantCode,
        string username,
        string path = "/api/auth/register")
        => SendAsync(
            client,
            path,
            new { username, password = "password123", tenantCode, inviteCode = tenantCode.ToLowerInvariant() + "-invite" },
            forwardedFor: null);

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        object body,
        string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
        }

        return client.SendAsync(request);
    }

    private sealed class RecordingHasher : IAuthPartitionHasher
    {
        private readonly Dictionary<(string Domain, string Value), ulong> _tags = [];

        public List<(string Domain, string Value)> Calls { get; } = [];

        public AuthPartitionHash Hash(string domain, string value, int partitionCount)
        {
            Calls.Add((domain, value));
            var key = (domain, value);
            if (!_tags.TryGetValue(key, out var tag))
            {
                tag = (ulong)_tags.Count + 1;
                _tags.Add(key, tag);
            }

            return new AuthPartitionHash(
                InitialSlot: (int)(tag % (ulong)partitionCount),
                TagHigh: tag,
                TagLow: tag);
        }
    }

    private sealed class RecordingAuthService : IAuthService
    {
        public int Calls { get; private set; }

        public Task<AuthResult> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new AuthResult(request.Username!, "USER", request.TenantCode!));
        }

        public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LoginResult("token", request.Username!, "USER", "demo-a"));
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] _content;

        public UnknownLengthContent(byte[] content)
        {
            _content = content;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
