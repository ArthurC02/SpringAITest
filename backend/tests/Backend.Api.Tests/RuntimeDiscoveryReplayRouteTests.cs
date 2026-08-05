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

    // 命中時 command_id 走 `prior.CommandId ?? run.CommandId`:查詢結果沒帶 command_id 的那一格
    // 必須退回 run 自己的 command_id(stub 讓兩者是不同 GUID,否則這條 ?? 換成任一邊都不會紅)。
    [Fact]
    public async Task Replay_WhenLookupCarriesNoCommandId_FallsBackToRunCommandId()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = "route-conversation", message = "fallback" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Factory.RunCommand, body.RootElement.GetProperty("command_id").GetGuid());
    }

    // conversation_id 的 400 等價類:欄位缺席(null)、空字串、全空白、含控制字元。
    // 這四個值都被 ValidateReplay 擋在 repository 之前,而且要回同一句對外訊息。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("route\u0001conversation")]
    public async Task Replay_RejectsInvalidConversationId(string? conversationId)
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = conversationId, message = "details" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Invalid chat replay request", body.RootElement.GetProperty("message").GetString());
    }

    // message 欄位整個缺席(→ null)是與「短字串」不同的等價類:ValidateReplay 只擋 null,不擋空字串。
    [Fact]
    public async Task Replay_RejectsOmittedMessage()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = "route-conversation" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Invalid chat replay request", body.RootElement.GetProperty("message").GetString());
    }

    // conversation_id 上限 128:on-point 必須穿過驗證直達 repository(stub 只看 message → 命中 200),
    // off-point 129 必須 400。用 "abc" 這種安全內部值測不出打錯的數字。
    [Theory]
    [InlineData(128, HttpStatusCode.OK)]
    [InlineData(129, HttpStatusCode.BadRequest)]
    public async Task Replay_EnforcesConversationIdLengthBoundary(int length, HttpStatusCode expected)
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = new string('c', length), message = "details" });

        Assert.Equal(expected, response.StatusCode);
    }

    // message 上限 16384,下界則沒有:空字串是合法值(只有 null 被擋)。
    // 通過驗證的兩列都會落到 stub 的 default 分支 → 409,正好證明它們沒有被驗證擋下。
    [Theory]
    [InlineData(0, HttpStatusCode.Conflict)]
    [InlineData(16_384, HttpStatusCode.Conflict)]
    [InlineData(16_385, HttpStatusCode.BadRequest)]
    public async Task Replay_EnforcesMessageLengthBoundary(int length, HttpStatusCode expected)
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = "route-conversation", message = new string('m', length) });

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

    // 只有一個 Idempotency-Key header,但內容本身不合法(trim 後為空 / 含控制字元):
    // header 數量那道門放行後還有一道內容檢查,兩者回同一個 400 訊息。
    [Theory]
    [InlineData("   ")]
    [InlineData("route\tattempt")]
    public async Task Replay_RejectsContentInvalidIdempotencyKey(string key)
    {
        using var client = Authenticated(key);

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = "route-conversation", message = "details" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Idempotency-Key is required", body.RootElement.GetProperty("message").GetString());
    }

    // Idempotency-Key 長度規格 1..128:兩個 on-point 都必須穿過驗證進到 repository
    // (key 不是 "route-attempt" → stub 判 mismatch → 409),129 才是 400。
    [Theory]
    [InlineData(1, HttpStatusCode.Conflict)]
    [InlineData(128, HttpStatusCode.Conflict)]
    [InlineData(129, HttpStatusCode.BadRequest)]
    public async Task Replay_EnforcesIdempotencyKeyLengthBoundary(int length, HttpStatusCode expected)
    {
        using var client = Authenticated(new string('k', length));

        var response = await client.PostAsJsonAsync(
            "/api/chat-runs/replay", new { conversation_id = "route-conversation", message = "details" });

        Assert.Equal(expected, response.StatusCode);
    }

    // fail-closed 串接:chat 需要 MULTI_AGENT_DISPATCH_ENABLED + AGENT_CHAT_ENABLED 兩環俱全,
    // 任一環不是 "true",整條 /api/chat-runs* 就必須在身分 header 與 body 驗證之前 404
    // (這個請求連 X-Tenant-Id 和 Idempotency-Key 都沒有)。
    // designer 兩個值都放進來:02-spec §8 之後它不再參與這條串鏈,開或關都不得改變結果。
    [Theory]
    [InlineData("true", "true", "false")]
    [InlineData("true", "false", "true")]
    [InlineData("false", "true", "false")]
    [InlineData("false", "false", "true")]
    public async Task Replay_WhenChatFlagCascadeIsOff_Is404BeforeIdentityCheck(string designer, string dispatch, string chat)
    {
        using var disabled = new FlagFactory(designer, dispatch, chat);
        using var client = disabled.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Token", TestWebAppFactory.InternalToken);

        var response = await client.PostAsJsonAsync("/api/chat-runs/replay", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Feature is unavailable", body.RootElement.GetProperty("message").GetString());
    }

    private HttpClient Authenticated(string key = "route-attempt")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Token", TestWebAppFactory.InternalToken);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", "route-tenant");
        client.DefaultRequestHeaders.Add("X-User-Id", "route-user");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Idempotency-Key", key);
        return client;
    }

    // 兩種設定機制都寫同樣的值,才不用管 UseSetting 與 AddInMemoryCollection 的優先權。
    private sealed class FlagFactory(string designer, string dispatch, string chat) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            TestWebAppFactory.ConfigureCredentials(builder);
            builder.UseSetting("WORKFLOW_DESIGNER_ENABLED", designer);
            builder.UseSetting("MULTI_AGENT_DISPATCH_ENABLED", dispatch);
            builder.UseSetting("AGENT_CHAT_ENABLED", chat);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WORKFLOW_DESIGNER_ENABLED"] = designer, ["MULTI_AGENT_DISPATCH_ENABLED"] = dispatch, ["AGENT_CHAT_ENABLED"] = chat,
            }));
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        internal static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid Command = Guid.Parse("22222222-2222-2222-2222-222222222222");
        // run 自己的 command_id 必須與查詢結果帶回的不同,`prior.CommandId ?? run.CommandId` 才驗得出來。
        internal static readonly Guid RunCommand = Guid.Parse("55555555-5555-5555-5555-555555555555");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            TestWebAppFactory.ConfigureCredentials(builder);
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
            JsonDocument.Parse("{}").RootElement.Clone(), DateTime.UtcNow, DateTime.UtcNow, Factory.RunCommand,
            JsonDocument.Parse("""{"aggregate":{"answer":"ok"}}""").RootElement.Clone());
        // 以 message 腳本化五種查詢結果,讓 controller 的整張映射表都能被驗到。
        public Task<OrchestratorRunActiveLookup> FindByIdempotencyKeyAsync(string tenant, string user, string key, OrchestratorRunReplayRequest replay, CancellationToken ct) =>
            Task.FromResult(tenant == "route-tenant" && user == "route-user" && key == "route-attempt"
                ? replay.Message switch
                {
                    "details" => new OrchestratorRunActiveLookup(Response, Factory.Command),
                    "fallback" => new OrchestratorRunActiveLookup(Response),
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
        public Task<OrchestratorContextRequestResponse?> GetOrCreateContextRequestAsync(string a,string b,Guid c,Guid d,CancellationToken e)=>throw new NotSupportedException();
        public Task<OrchestratorContextRequestResponse?> GetContextRequestAsync(string a,string b,Guid c,Guid d,Guid e,CancellationToken f)=>throw new NotSupportedException();
        public Task<OrchestratorContextDeltaResult> AppendContextDeltaAsync(string a,string b,Guid c,Guid d,Guid e,long f,OrchestratorContextDeltaRequest g,CancellationToken h)=>throw new NotSupportedException();
    }
}
