using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class AuthApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public AuthApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_Returns201_WithAuthResult()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "reg-201", password = "password123", tenantCode = "demo-a", inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("reg-201", body["username"]!.GetValue<string>());
        Assert.Equal("USER", body["role"]!.GetValue<string>());
        Assert.Equal("demo-a", body["tenantCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns400_WhenPasswordTooShort()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "shorty", password = "abc", tenantCode = "demo-a", inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("password 長度至少 8 碼", body["fieldErrors"]!["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns400_WhenInviteCodeMissing()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "noinvite", password = "password123", tenantCode = "demo-a" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("inviteCode 不可為空", body["fieldErrors"]!["inviteCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns404_WhenTenantMissing()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "notenant", password = "password123", tenantCode = "nope", inviteCode = "x" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("找不到租戶：nope", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns403_WhenInviteWrong()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "badinvite", password = "password123", tenantCode = "demo-a", inviteCode = "wrong" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("邀請碼無效", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns409_WhenUsernameTaken()
    {
        var client = _factory.CreateClient();

        // user-a 是種子使用者,重複註冊必衝突。
        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "user-a", password = "password123", tenantCode = "demo-a", inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("使用者名稱已存在：user-a", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_Returns200_WithToken()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "user-a", password = "password123" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.False(string.IsNullOrWhiteSpace(body["token"]!.GetValue<string>()));
        Assert.Equal("user-a", body["username"]!.GetValue<string>());
        Assert.Equal("USER", body["role"]!.GetValue<string>());
        Assert.Equal("demo-a", body["tenantCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_Returns401_WhenPasswordWrong()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "user-a", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("帳號或密碼錯誤", body["message"]!.GetValue<string>());
    }
}
