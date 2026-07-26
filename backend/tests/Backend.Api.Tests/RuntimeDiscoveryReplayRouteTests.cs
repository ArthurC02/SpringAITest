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
        // message 前後空白由 controller 正規化後才交給 repository 比對(stub 只認 "details")。
        var response = await client.PostAsJsonAsync("/api/chat-runs/replay", new
        {
            conversation_id = "route-conversation", message = "  details  ",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(Factory.Command, body.RootElement.GetProperty("command_id").GetGuid());
    }

    // 決策表的另外三格:repository 回 mismatch / ambiguous / 查無 → 409 / 409 / 404。
    // 只測 200 的話,controller 把三種結果都當成 200 也不會有測試變紅。
    [Theory]
    [InlineData("mismatched", HttpStatusCode.Conflict)]
    [InlineData("ambiguous", HttpStatusCode.Conflict)]
    [InlineData("absent", HttpStatusCode.NotFound)]
    public async Task Replay_MapsLookupOutcomesToStatusCodes(string message, HttpStatusCode expected)
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = "route-conversation", message });

        Assert.Equal(expected, response.StatusCode);
    }

    // Idempotency-Key 是這條 replay 路由的邏輯嘗試識別:重複、缺少、空白都必須 400,
    // 否則兩個不同嘗試會被當成同一個 replay。
    [Fact]
    public async Task Replay_RequiresExactlyOneIdempotencyKey()
    {
        using var noKey = _factory.CreateClient();
        noKey.DefaultRequestHeaders.Add("X-Internal-Token", TestWebAppFactory.InternalToken);
        noKey.DefaultRequestHeaders.Add("X-Tenant-Id", "route-tenant");
        noKey.DefaultRequestHeaders.Add("X-User-Id", "route-user");
        var body = new { conversation_id = "route-conversation", message = "details" };
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await noKey.PostAsJsonAsync("/api/chat-runs/replay", body)).StatusCode);

        using var duplicated = Authenticated();
        duplicated.DefaultRequestHeaders.Add("Idempotency-Key", "route-attempt-2");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await duplicated.PostAsJsonAsync("/api/chat-runs/replay", body)).StatusCode);
    }

    private HttpClient Authenticated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Token", TestWebAppFactory.InternalToken);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "route-tenant");
        client.DefaultRequestHeaders.Add("X-User-Id", "route-user");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "route-attempt");
        return client;
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
        // 以 message 腳本化四種查詢結果,讓 controller 的整張映射表都能被驗到。
        public Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenant, string user, string key, OrchestratorRunReplayRequest replay, CancellationToken ct) =>
            Task.FromResult(tenant == "route-tenant" && user == "route-user" && key == "route-attempt"
                ? replay.Message switch
                {
                    "details" => new OrchestratorRunActiveLookup(Response, Factory.Command),
                    "ambiguous" => new OrchestratorRunActiveLookup(null, IsAmbiguous: true),
                    "absent" => new OrchestratorRunActiveLookup(null),
                    _ => new OrchestratorRunActiveLookup(null, IsMismatch: true),
                }
                : new OrchestratorRunActiveLookup(null, IsMismatch: true));
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
