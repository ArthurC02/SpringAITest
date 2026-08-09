using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service;

namespace Platform.Web.Tests;

/// <summary>
/// O5 one-shot durable triggers (04-operations-trigger-plan.md §6): Platform's own gate, capability
/// and transparent-proxy wiring. Every schedule/target/fire rule belongs to Backend (see
/// TriggerApiTests + TriggerDispatchTests in Backend.Api.Tests) — this file only proves Platform
/// forwards identity and preconditions faithfully and never invents an answer of its own.
/// </summary>
public sealed class TriggerApiTests
    : IClassFixture<TriggerApiTests.ProxyFixture>, IClassFixture<TriggerApiTests.DownstreamStatusFixture>
{
    private const string Id = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

    private readonly ProxyFixture _proxy;
    private readonly DownstreamStatusFixture _downstreamStatus;

    public TriggerApiTests(ProxyFixture proxy, DownstreamStatusFixture downstreamStatus)
    {
        _proxy = proxy;
        _downstreamStatus = downstreamStatus;
    }

    [Fact]
    public async Task FlagOff_HidesTriggersBeforeAuthentication_WithTheUnknownRouteContract()
    {
        using var factory = new TestWebAppFactory();
        using var client = factory.CreateClient();

        using var gated = await client.GetAsync("/api/admin/triggers");
        using var unknown = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, gated.StatusCode);
        var gatedBody = await gated.ReadJsonAsync();
        var unknownBody = await unknown.ReadJsonAsync();
        gatedBody.AssertApiError(404, "not_found");
        unknownBody.AssertApiError(404, "not_found");
        Assert.Equal(gatedBody["message"]!.GetValue<string>(), unknownBody["message"]!.GetValue<string>());
        Assert.Equal(gated.Content.Headers.ContentType?.MediaType, unknown.Content.Headers.ContentType?.MediaType);
    }

    // The write half of the gate decision table: creating is hidden too, even with a valid token
    // carrying the exact capability -- the gate sits before authentication for every verb.
    [Fact]
    public async Task FlagOff_HidesCreate_EvenForACapabilityBearingCaller()
    {
        using var factory = new TestWebAppFactory();
        using var client = factory.CreateClient()
            .WithToken(factory.IssueToken(capabilities: ["workflow.manage"]));

        var response = await client.PostAsync("/api/admin/triggers", Json());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        (await response.ReadJsonAsync()).AssertApiError(404, "not_found");
    }

    [Fact]
    public async Task FlagOn_Anonymous_IsUnauthorized()
    {
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _proxy.CreateClient().GetAsync("/api/admin/triggers")).StatusCode);
    }

    // workflow.manage is an exact match: the ADMIN role never implies it, nor does a near-miss.
    [Theory]
    [InlineData(null)]
    [InlineData("workflow.manage.all")]
    [InlineData("WORKFLOW.MANAGE")]
    public async Task MissingOrApproximateCapability_IsForbidden(string? capability)
    {
        using var client = _proxy.CreateClient().WithToken(_proxy.IssueToken(
            role: "ADMIN", capabilities: capability is null ? null : [capability]));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/triggers")).StatusCode);
    }

    [Fact]
    public async Task NonAdminWithExactCapability_Succeeds()
    {
        using var client = _proxy.CreateClient().WithToken(
            _proxy.IssueToken(role: "USER", capabilities: ["workflow.manage"]));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/triggers")).StatusCode);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("GET", "/" + Id)]
    [InlineData("POST", "/" + Id + "/cancel")]
    [InlineData("GET", "/" + Id + "/occurrences")]
    public async Task Routes_ForwardVerbAndPathVerbatim(string method, string suffix)
    {
        using var client = Authorized(_proxy);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/admin/triggers" + suffix);
        if (method == "POST" && suffix.Length == 0)
        {
            request.Content = Json();
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(method, _proxy.Handler.Method);
        Assert.Equal("/api/admin/triggers" + suffix, _proxy.Handler.Path);
    }

    [Fact]
    public async Task Create_ForwardsAuthenticatedIdentityAndBodyVerbatim()
    {
        using var client = _proxy.CreateClient().WithToken(_proxy.IssueToken(
            username: "operator-a", tenantCode: "tenant-a", capabilities: ["workflow.manage"]));

        var response = await client.PostAsync("/api/admin/triggers", Json());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tenant-a", _proxy.Handler.Header("X-Tenant-Id"));
        Assert.Equal("operator-a", _proxy.Handler.Header("X-User-Id"));
        Assert.Equal("workflow.manage", _proxy.Handler.Header("X-User-Capabilities"));
        Assert.Contains(
            "\"fire_at\":\"2030-01-01T00:00:00Z\"",
            Encoding.UTF8.GetString(_proxy.Handler.Body!),
            StringComparison.Ordinal);
    }

    // Backend owns the keyset cursor and the page bounds, so the caller's query string rides
    // through untouched -- Platform must neither default nor re-type a single parameter.
    [Fact]
    public async Task Occurrences_ForwardQueryStringVerbatim()
    {
        using var client = Authorized(_proxy);

        var response = await client.GetAsync(
            $"/api/admin/triggers/{Id}/occurrences?cursor=abc123&limit=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"/api/admin/triggers/{Id}/occurrences", _proxy.Handler.Path);
        Assert.Equal("?cursor=abc123&limit=7", _proxy.Handler.Query);
    }

    [Fact]
    public async Task Occurrences_WithoutQueryString_ForwardsNothingExtra()
    {
        using var client = Authorized(_proxy);

        var response = await client.GetAsync($"/api/admin/triggers/{Id}/occurrences");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, _proxy.Handler.Query);
    }

    // Cancel is the only route with an optimistic-lock precondition; If-Match must reach Backend
    // unchanged there and must not be smuggled onto the routes that do not use it.
    [Fact]
    public async Task Cancel_ForwardsIfMatchVerbatim()
    {
        using var client = Authorized(_proxy);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/triggers/{Id}/cancel");
        request.Headers.TryAddWithoutValidation("If-Match", "\"3\"");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"3\"", _proxy.Handler.Header("If-Match"));
    }

    [Fact]
    public async Task Create_DoesNotForwardIfMatch()
    {
        using var client = Authorized(_proxy);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/triggers") { Content = Json() };
        request.Headers.TryAddWithoutValidation("If-Match", "\"3\"");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_proxy.Handler.Header("If-Match"));
    }

    // The other half of the downstream decision table: Backend's own 4xx decisions (a stale ETag,
    // a target that is no longer published) pass through byte for byte, while a 5xx collapses into
    // the controlled gateway error without leaking a single byte of the backend body.
    [Theory]
    [InlineData(404, 404, "下游訊息")]
    [InlineData(409, 409, "下游訊息")]
    [InlineData(428, 428, "下游訊息")]
    [InlineData(500, 502, null)]
    [InlineData(503, 502, null)]
    public async Task DownstreamStatus_PassesThrough4xx_AndCollapses5xxWithoutLeakingBody(
        int downstream, int expected, string? expectedMessage)
    {
        _downstreamStatus.Handler.Reset(
            (HttpStatusCode)downstream,
            $"{{\"timestamp\":\"2026-08-09T00:00:00Z\",\"status\":{downstream},\"code\":\"backend_code\","
            + "\"message\":\"下游訊息\",\"correlationId\":\"backend-trace-9\",\"detail\":\"internal-secret\"}");
        using var client = Authorized(_downstreamStatus);

        var response = await client.GetAsync($"/api/admin/triggers/{Id}");

        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (expectedMessage is null)
        {
            Assert.DoesNotContain("internal-secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain("下游訊息", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expectedMessage, body, StringComparison.Ordinal);
            Assert.Contains("backend-trace-9", body, StringComparison.Ordinal);
        }
    }

    private static HttpClient Authorized(TestWebAppFactory factory)
        => factory.CreateClient().WithToken(factory.IssueToken(capabilities: ["workflow.manage"]));

    private static StringContent Json()
        => new(
            """{"name":"nightly","orchestrator_id":"3fa85f64-5717-4562-b3fc-2c963f66afa6","orchestrator_revision":2,"fire_at":"2030-01-01T00:00:00Z","input_mapping":{"message":"go"}}""",
            Encoding.UTF8,
            "application/json");

    /// <summary>Keeps the real BackendClient and swaps only the outermost handler (default 200/"{}").</summary>
    public class ProxyFixture : TestWebAppFactory
    {
        public CapturingBackendHandler Handler { get; } = new();

        public ProxyFixture() : base(new() { ["AGENT_TRIGGERS_ENABLED"] = "true" })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackendClient>();
                services.AddHttpClient<BackendClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
            });
        }
    }

    /// <summary>Own handler: the DownstreamStatus_* rows rewrite the response and never restore it.</summary>
    public sealed class DownstreamStatusFixture : ProxyFixture
    {
    }
}
