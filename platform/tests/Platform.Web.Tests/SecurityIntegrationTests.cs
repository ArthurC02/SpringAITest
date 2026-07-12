using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class SecurityIntegrationTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SecurityIntegrationTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401_ApiError()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/workflows");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("未認證或憑證無效", body["message"]!.GetValue<string>());
        // fieldErrors 永遠存在(空物件)。
        Assert.NotNull(body["fieldErrors"]);
    }

    [Fact]
    public async Task ChatHistory_WithoutToken_Returns200()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/chat/history");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
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
        var workflows = await authed.GetAsync("/api/workflows");

        Assert.Equal(HttpStatusCode.OK, workflows.StatusCode);
    }

    [Fact]
    public async Task Register_WrongInvite_Returns403_WithMessage()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "sec-badinvite", password = "password123", tenantCode = "demo-a", inviteCode = "nope" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("邀請碼無效", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401_WithMessage()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "user-b", password = "totally-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("帳號或密碼錯誤", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }
}
