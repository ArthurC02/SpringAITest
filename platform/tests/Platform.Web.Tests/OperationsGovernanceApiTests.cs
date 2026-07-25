using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service;

namespace Platform.Web.Tests;

public sealed class OperationsGovernanceApiTests
{
    [Fact]
    public async Task FeatureOff_HidesOperationsBeforeAuthentication()
    {
        using var factory = new TestWebAppFactory(agentWriteToolsEnabled: false);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/api/admin/operations/metrics")).StatusCode);
    }

    [Fact]
    public async Task CapabilityAndFlagGate_ForwardOnlyAuthenticatedIdentityAndOverrideKey()
    {
        using var factory = new Factory();
        using var denied = factory.CreateClient().WithToken(factory.IssueToken(role: "ADMIN"));
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/admin/operations/metrics")).StatusCode);

        using var client = factory.CreateClient().WithToken(factory.IssueToken(username: "operator-a", tenantCode: "tenant-a", capabilities: ["workflow.manage"]));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/operations/regression-overrides") { Content = new StringContent("{\"reason\":\"break glass\"}", Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "override-logical-attempt");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/admin/operations/regression-overrides", factory.Handler.Path);
        Assert.Equal("override-logical-attempt", factory.Handler.Header("Idempotency-Key"));
        Assert.Equal("tenant-a", factory.Handler.Header("X-Tenant-Id"));
        Assert.Equal("operator-a", factory.Handler.Header("X-User-Id"));
        Assert.Equal("workflow.manage", factory.Handler.Header("X-User-Capabilities"));
    }

    private sealed class Factory : TestWebAppFactory
    {
        public Handler Handler { get; } = new();
        public Factory() : base(agentWriteToolsEnabled: true) { }
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

    private sealed class Handler : HttpMessageHandler
    {
        private HttpRequestMessage? _request;
        public string? Path { get; private set; }
        public string? Header(string name) => _request is not null && _request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _request = request; Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}
