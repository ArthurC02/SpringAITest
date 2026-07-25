using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Api.OrchestratorRuns;
using Backend.Api.RuntimeDiscovery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;

namespace Backend.Api.Tests;

public sealed class RuntimeDiscoveryReplayRouteTests : IClassFixture<RuntimeDiscoveryReplayRouteTests.Factory>
{
    private readonly Factory _factory;
    public RuntimeDiscoveryReplayRouteTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Replay_IsPostOnly_AndPostReturnsMatchedRun()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Token", TestWebAppFactory.InternalToken);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "route-tenant");
        client.DefaultRequestHeaders.Add("X-User-Id", "route-user");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "route-attempt");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/chat-runs/replay")).StatusCode);
        var response = await client.PostAsJsonAsync("/api/chat-runs/replay", new
        {
            conversation_id = "route-conversation", message = "  details  ",
            candidates = new[] { new { operation = "resume", fingerprint = new string('a', 64) } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(Factory.Command, body.RootElement.GetProperty("command_id").GetGuid());
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        internal static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid Command = Guid.Parse("22222222-2222-2222-2222-222222222222");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AGENT_CHAT_ENABLED", "true");
            builder.UseSetting("MULTI_AGENT_DISPATCH_ENABLED", "true");
            builder.UseSetting("WORKFLOW_DESIGNER_ENABLED", "true");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGENT_CHAT_ENABLED"] = "true", ["MULTI_AGENT_DISPATCH_ENABLED"] = "true", ["WORKFLOW_DESIGNER_ENABLED"] = "true",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOrchestratorRunRepository>();
                services.AddSingleton<IOrchestratorRunRepository, ReplayOnlyRepository>();
            });
        }
    }

    private sealed class ReplayOnlyRepository : IOrchestratorRunRepository
    {
        private static readonly OrchestratorRunResponse Response = new(
            Factory.Run, Guid.Parse("33333333-3333-3333-3333-333333333333"), 1,
            "route-conversation", Guid.Parse("44444444-4444-4444-4444-444444444444"), 1,
            new string('b', 64), "completed", false, 1, DateTime.UtcNow.AddMinutes(1),
            JsonDocument.Parse("{}").RootElement.Clone(), DateTime.UtcNow, DateTime.UtcNow, Factory.Command,
            JsonDocument.Parse("""{"aggregate":{"answer":"ok"}}""").RootElement.Clone());
        public Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenant, string user, string key, OrchestratorRunReplayRequest replay, CancellationToken ct) =>
            Task.FromResult(tenant == "route-tenant" && user == "route-user" && key == "route-attempt" && replay.Message == "details"
                ? new OrchestratorRunActiveLookup(Response, Factory.Command) : new OrchestratorRunActiveLookup(null, IsMismatch: true));
        public Task<OrchestratorRunWriteResult> CreateAsync(string a,string b,string c,IReadOnlyCollection<string>d,IReadOnlyCollection<string>e,Guid f,string g,string h,string i,CancellationToken j)=>throw new NotSupportedException();
        public Task<OrchestratorRunResponse?> GetAsync(string a,string b,Guid c,CancellationToken d)=>throw new NotSupportedException();
        public Task<OrchestratorRunActiveLookup> FindActiveAsync(string a,string b,string c,CancellationToken d)=>throw new NotSupportedException();
        public Task<OrchestratorRunEventsResponse?> EventsAsync(string a,string b,Guid c,long d,int e,CancellationToken f)=>throw new NotSupportedException();
        public Task<OrchestratorRunWriteResult> CancelAsync(string a,string b,Guid c,string? d,string e,CancellationToken f)=>throw new NotSupportedException();
        public Task<OrchestratorRunWriteResult> ResumeAsync(string a,string b,Guid c,string d,string e,CancellationToken f)=>throw new NotSupportedException();
        public Task<string?> ExecutionArtifactAsync(string a,string b,Guid c,CancellationToken d)=>throw new NotSupportedException();
        public Task<OrchestratorRunCommandClaim?> ClaimCommandAsync(string a,string b,Guid c,Guid d,string e,int f,CancellationToken g)=>throw new NotSupportedException();
        public Task<OrchestratorRunCommandClaim?> RenewCommandAsync(string a,string b,Guid c,Guid d,string e,long f,int g,CancellationToken h)=>throw new NotSupportedException();
        public Task<OrchestratorRunDispatchCompleteStatus> CompleteDispatchAsync(string a,string b,Guid c,Guid d,string e,CancellationToken f)=>throw new NotSupportedException();
        public Task<OrchestratorRunRecoveryResponse> ClaimRecoveryAsync(string a,int b,int c,CancellationToken d)=>throw new NotSupportedException();
        public Task<OrchestratorChildResponse?> CreateChildAsync(string a,string b,Guid c,OrchestratorChildCreateRequest d,CancellationToken e)=>throw new NotSupportedException();
        public Task<OrchestratorChildStatusResponse?> GetChildAsync(string a,string b,Guid c,Guid d,CancellationToken e)=>throw new NotSupportedException();
        public Task<OrchestratorRunWriteResult> TransitionAsync(string a,string b,Guid c,OrchestratorRootTransitionRequest d,CancellationToken e)=>throw new NotSupportedException();
        public Task<OrchestratorContextAcquireResponse?> AcquireContextAsync(string a,string b,Guid c,OrchestratorContextAcquireRequest d,CancellationToken e)=>throw new NotSupportedException();
    }
}
