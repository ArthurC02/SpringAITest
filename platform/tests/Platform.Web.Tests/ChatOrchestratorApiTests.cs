using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service;

namespace Platform.Web.Tests;

/// <summary>
/// D6 的 USER-safe 探索端點 GET /api/chat/orchestrators。canary 資格是伺服器端決定的
/// (AGENT_CHAT_ENABLED + 租戶白名單);不合格的租戶連「端點存在」都不該看到 —— 回 404 且
/// 請求絕不抵達 backend(fail-closed,不靠前端不顯示)。
/// </summary>
public sealed class ChatOrchestratorApiTests
{
    private const string Path = "/api/chat/orchestrators";
    private const string Catalog =
        """[{"id":"11111111-1111-1111-1111-111111111111","name":"研究組","revision":2}]""";

    // 旗標開著、但租戶不在白名單:仍是 404,而且不得把租戶身分帶去 backend 探路。
    [Fact]
    public async Task NonCanaryTenant_Returns404_AndNeverReachesBackend()
    {
        using var factory = new Factory(allowlist: "tenant-canary");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await factory.CreateClient().GetAsync(Path)).StatusCode);

        var client = factory.CreateClient().WithToken(
            factory.IssueToken("user-a", "USER", "tenant-other"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path)).StatusCode);
        Assert.Null(factory.Backend.Path);
    }

    // 另一個 conjunct:AGENT_CHAT_ENABLED 關閉時,租戶在不在白名單上都一樣看不到端點 ——
    // 兩個 gate 各自獨立 fail-closed,白名單成員資格不得單獨開門(旗標即是回滾開關)。
    [Theory]
    [InlineData("tenant-canary")]
    [InlineData("tenant-other")]
    public async Task FlagOff_Returns404_ForAnyTenant_AndNeverReachesBackend(string tenant)
    {
        using var factory = new Factory(allowlist: "tenant-canary", enabled: false);
        var client = factory.CreateClient().WithToken(
            factory.IssueToken("user-a", "USER", tenant));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path)).StatusCode);
        Assert.Null(factory.Backend.Path);
    }

    // JWT 通過簽章驗證不代表身分 claims 完整:缺 subject 時 ToUsableChatUserContext 回 null,
    // 這是與「租戶不在白名單」互相獨立的 404 成因 —— 即使該 token 的 tenantCode 就在白名單上,
    // 也不得帶著空白 userId 去 backend 探路。
    [Fact]
    public async Task AuthenticatedWithoutSubjectClaim_Returns404_EvenWhenTenantIsAllowlisted()
    {
        using var factory = new Factory(allowlist: "demo-a");
        var client = factory.CreateClient().WithToken(
            TestTokens.MintMissingChatIdentityClaim(omitSubject: true));

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.Backend.Path);
    }

    // 決策表的另一半:合格租戶進到 backend 之後,下游失敗要收斂成什麼。
    // 「連不上(傳輸例外)」與「回非 2xx」走 SendForJsonElementAsync 兩個不同的 callback,
    // 對外都必須是 502 + 固定中文訊息的 ApiError,backend 的錯誤內文一律不外洩。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackendFailure_Returns502_WithoutLeakingDownstreamDetail(bool unreachable)
    {
        using var factory = new Factory(allowlist: "tenant-canary", backendUnreachable: unreachable);
        // 洩漏偵測用 ASCII 標記:ToJsonString() 會把非 ASCII 逸出成 \uXXXX,中文標記驗不到東西。
        factory.Backend.Reset(
            HttpStatusCode.InternalServerError,
            """{"message":"backend internal detail 10.0.0.7:8002"}""");
        var client = factory.CreateClient().WithToken(
            factory.IssueToken("user-a", "USER", "tenant-canary"));

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(502, body["status"]!.GetValue<int>());
        Assert.Equal("上游服務暫時無法使用，請稍後再試", body["message"]!.GetValue<string>());
        Assert.NotNull(body["timestamp"]);
        Assert.Empty(body["fieldErrors"]!.AsObject());
        Assert.DoesNotContain("10.0.0.7", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanaryTenant_ReturnsBackendCatalog()
    {
        using var factory = new Factory(allowlist: "tenant-canary");
        factory.Backend.Reset(HttpStatusCode.OK, Catalog);
        var client = factory.CreateClient().WithToken(
            factory.IssueToken("user-a", "USER", "tenant-canary"));

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/runtime-discovery/orchestrators", factory.Backend.Path);
        Assert.Equal("tenant-canary", factory.Backend.Header("X-Tenant-Id"));
        var body = await response.ReadJsonAsync();
        Assert.Equal("研究組", body[0]!["name"]!.GetValue<string>());
    }

    private sealed class Factory : TestWebAppFactory
    {
        private readonly HttpMessageHandler _handler;

        public CapturingBackendHandler Backend { get; } = new();

        public Factory(string allowlist, bool enabled = true, bool backendUnreachable = false)
            : base(agentChatEnabled: enabled, agentChatTenantAllowlist: allowlist)
        {
            _handler = backendUnreachable ? new UnreachableBackendHandler() : (HttpMessageHandler)Backend;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackendClient>();
                services.AddHttpClient<BackendClient>().ConfigurePrimaryHttpMessageHandler(() => _handler);
            });
        }
    }

    /// <summary>「根本沒有回應」的失敗等價類:backend 連不上,而不是回一個錯誤狀態碼。</summary>
    private sealed class UnreachableBackendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("backend 連線被拒");
    }
}
