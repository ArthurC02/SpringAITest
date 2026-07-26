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
        public CapturingBackendHandler Backend { get; } = new();

        public Factory(string allowlist)
            : base(agentChatEnabled: true, agentChatTenantAllowlist: allowlist)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackendClient>();
                services.AddHttpClient<BackendClient>().ConfigurePrimaryHttpMessageHandler(() => Backend);
            });
        }
    }
}
