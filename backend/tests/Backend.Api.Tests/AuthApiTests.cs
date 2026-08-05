using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using Backend.Api.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

public sealed class AuthApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public AuthApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_Returns201_WithAuthResult()
    {
        var client = _factory.CreateInternalClient();

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
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "shorty", password = "abc", tenantCode = "demo-a", inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("password 長度至少 8 碼", body["fieldErrors"]!["password"]!.GetValue<string>());
    }

    // NotBlank 與 MinLength/RegularExpression 是各自獨立的分支(fieldErrors 每欄只取第一個訊息);
    // 這裡每列都只讓 NotBlank 失敗:null 不會觸發 MinLength,空字串不會觸發 RegularExpression。
    [Theory]
    [InlineData("", "password123", "demo-a", "username", "username 不可為空")]
    [InlineData("blank-password", null, "demo-a", "password", "password 不可為空")]
    [InlineData("blank-tenant", "password123", "", "tenantCode", "tenantCode 不可為空")]
    public async Task Register_Returns400_WhenRequiredFieldBlank(
        string username, string? password, string tenantCode, string field, string message)
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username, password, tenantCode, inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal(message, body["fieldErrors"]![field]!.GetValue<string>());
    }

    // MinLength(8) 的相鄰邊界:等價類內側的 "abc"(3)/"password123"(11)證明不了門檻沒被打成 7 或 9。
    [Theory]
    [InlineData("pw-boundary-7", "1234567", HttpStatusCode.BadRequest)]
    [InlineData("pw-boundary-8", "12345678", HttpStatusCode.Created)]
    public async Task Register_EnforcesPasswordMinLengthBoundary(
        string username, string password, HttpStatusCode expected)
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username, password, tenantCode = "demo-a", inviteCode = "demo-a-invite" });

        Assert.Equal(expected, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Returns400_WhenInviteCodeMissing()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "noinvite", password = "password123", tenantCode = "demo-a" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("inviteCode 不可為空", body["fieldErrors"]!["inviteCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns404_WhenTenantMissing()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "notenant", password = "password123", tenantCode = "nope", inviteCode = "x" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("找不到租戶：nope", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns403_WhenInviteWrong()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "badinvite", password = "password123", tenantCode = "demo-a", inviteCode = "wrong" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("邀請碼無效", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_Returns409_WhenUsernameTaken()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "user-a", password = "password123", tenantCode = "demo-a", inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("使用者名稱已存在：user-a", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_Returns200_WithToken()
    {
        var client = _factory.CreateInternalClient();

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
    public async Task Login_SignsOnlyPersistedUserCapabilities_NotAdminRole()
    {
        var client = _factory.CreateInternalClient();

        var managerResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "admin-a", password = "password123" });
        Assert.Equal(HttpStatusCode.OK, managerResponse.StatusCode);
        var managerToken = (await managerResponse.ReadJsonAsync())["token"]!.GetValue<string>();
        var managerJwt = new JwtSecurityTokenHandler().ReadJwtToken(managerToken);
        Assert.Contains(
            managerJwt.Claims,
            c => c.Type == "capabilities" && c.Value == "workflow.manage");

        var repository = _factory.Services.GetRequiredService<IAuthRepository>();
        await repository.AddUserAsync(
            "plain-admin",
            BCrypt.Net.BCrypt.HashPassword("password123"),
            "ADMIN",
            tenantId: 1,
            default);

        var plainResponse = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "plain-admin", password = "password123" });
        Assert.Equal(HttpStatusCode.OK, plainResponse.StatusCode);
        var plainToken = (await plainResponse.ReadJsonAsync())["token"]!.GetValue<string>();
        var plainJwt = new JwtSecurityTokenHandler().ReadJwtToken(plainToken);
        Assert.Equal("ADMIN", plainJwt.Claims.Single(c => c.Type == "role").Value);
        Assert.DoesNotContain(plainJwt.Claims, c => c.Type == "capabilities");
    }

    [Fact]
    public async Task Login_SignsTenantScopedPersistedGroups()
    {
        var client = _factory.CreateInternalClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "admin-a", password = "password123" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.ReadJsonAsync())["token"]!.GetValue<string>();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(
            new[] { "operations" },
            jwt.Claims
                .Where(claim => claim.Type == "groups")
                .Select(claim => claim.Value)
                .ToArray());
    }

    // 一般 USER 這條路徑:groups 要簽進去,而沒有任何 capability 時該欄位必須整個不存在
    // (JwtService 對空集合不加 claim,舊 token 逐位元不變)。
    [Fact]
    public async Task Login_SignsPersistedGroups_AndOmitsCapabilities_ForPlainUser()
    {
        var client = _factory.CreateInternalClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "user-a", password = "password123" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.ReadJsonAsync())["token"]!.GetValue<string>();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("USER", jwt.Claims.Single(claim => claim.Type == "role").Value);
        Assert.Equal(
            new[] { "analysts" },
            jwt.Claims
                .Where(claim => claim.Type == "groups")
                .Select(claim => claim.Value)
                .ToArray());
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == "capabilities");
    }

    [Fact]
    public async Task Login_Returns401_WhenPasswordWrong()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "user-a", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("帳號或密碼錯誤", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_Returns401_WhenUserMissing()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "ghost", password = "password123" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("帳號或密碼錯誤", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // 空白身分是驗證失敗(400),不是「查無此帳號」(401)—— 兩者的等價類不同,不可互相取代。
    [Theory]
    [InlineData("", "password123", "username", "username 不可為空")]
    [InlineData("user-a", "", "password", "password 不可為空")]
    public async Task Login_Returns400_WhenRequiredFieldBlank(
        string username, string password, string field, string message)
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username, password });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal(message, body["fieldErrors"]![field]!.GetValue<string>());
    }

    // ---- 記憶鍵分隔字元:username / tenantCode 不得含 ':' ----
    // platform 的租戶隔離鍵是未逃逸的 `{tenantCode}:{userId}`,身分含 ':' 會讓兩組不同身分撞成同一把鍵
    // (tenant "t" + user "a:b" 與 tenant "t:a" + user "b" 同為 "t:a:b"),而短期記憶視窗與 mem0 uid 都用它
    // → 撞鍵即跨使用者記憶可見。鍵格式刻意不改(既有 mem0 uid 與進行中的視窗會全斷),改在產生這兩個值的
    // 邊界擋下;platform 衍生時另有第二道 fail-closed 守門。

    [Theory]
    [InlineData("bad:user", "demo-a", "username")]
    [InlineData("baduser", "demo:a", "tenantCode")]
    public async Task Register_Returns400_WhenIdentityContainsMemoryKeySeparator(
        string username, string tenantCode, string field)
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/register",
            new { username, password = "password123", tenantCode, inviteCode = "demo-a-invite" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal($"{field} 不可包含冒號", body["fieldErrors"]![field]!.GetValue<string>());
    }

    // login 是同一組值的另一個入口:即使某筆遺留資料真的帶 ':',也不得換到一張會撞鍵的 JWT。
    [Fact]
    public async Task Login_Returns400_WhenUsernameContainsMemoryKeySeparator()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "user:a", password = "password123" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("username 不可包含冒號", body["fieldErrors"]!["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task AuthIdentifiers_RejectLengthAbove128()
    {
        var client = _factory.CreateInternalClient();
        var oversized = new string('a', 129);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = oversized, password = "password123" });
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        Assert.NotNull((await login.ReadJsonAsync())["fieldErrors"]!["username"]);

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                username = "bounded-user",
                password = "password123",
                tenantCode = oversized,
                inviteCode = "invite",
            });
        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
        Assert.NotNull((await register.ReadJsonAsync())["fieldErrors"]!["tenantCode"]);
    }
}
