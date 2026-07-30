using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

// /api/skills/catalog 探針呼叫真的會打到 FakeWorkflowEngineClient.GetSkillCatalogAsync,累加靜態
// EngineCalls;需與 ChatApiTests/SkillApiTests 序列化,理由同 EngineCallsCollection 上的說明。
[Collection("EngineCalls")]
public sealed class SecurityIntegrationTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SecurityIntegrationTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401_ApiError()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/skills/catalog");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("未認證或憑證無效", body["message"]!.GetValue<string>());
        // fieldErrors 永遠存在(空物件)。
        Assert.NotNull(body["fieldErrors"]);
    }

    // AllowAnonymous 端點的契約是「空陣列,不是 401」——只驗 200 會漏掉「匿名讀到別人歷史」這個等價類。
    [Fact]
    public async Task ChatHistory_WithoutToken_Returns200_EmptyArray()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/chat/history");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await resp.ReadJsonAsync()).AsArray());
    }

    [Fact]
    public async Task RegisterThenLogin_ThenBearer_AccessesProtectedEndpoint()
    {
        var client = _factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "sec-flow", password = "password123", tenantCode = "demo-a", inviteCode = "demo-a-invite" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "sec-flow", password = "password123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.ReadJsonAsync())["token"]!.GetValue<string>();

        var authed = _factory.CreateClient().WithToken(token);
        var resp = await authed.GetAsync("/api/skills/catalog");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // 過期與竄改的 token 同屬「憑證無效」等價類:JwtBearer OnChallenge 一律回 401 + 同一 ApiError。
    [Theory]
    [InlineData("expired")]
    [InlineData("tampered")]
    public async Task ProtectedEndpoint_InvalidToken_Returns401_ApiError(string kind)
    {
        var now = DateTime.UtcNow;
        var token = kind == "expired"
            ? TestTokens.Mint(notBefore: now.AddMinutes(-10), expires: now.AddMinutes(-5))
            : TestTokens.Mint() + "x"; // 竄改簽章尾段。
        var client = _factory.CreateClient().WithToken(token);

        var resp = await client.GetAsync("/api/skills/catalog");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("未認證或憑證無效", body["message"]!.GetValue<string>());
        Assert.NotNull(body["fieldErrors"]);
    }
}
