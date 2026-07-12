using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class AuthServiceTests
{
    private static AuthService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        Json(status, $"{{\"timestamp\":\"2026-07-12T00:00:00Z\",\"status\":{(int)status},\"message\":{JsonSerializer.Serialize(message)},\"fieldErrors\":{{}}}}");

    // ---- register ----

    [Fact]
    public async Task Register_Succeeds_SendsHeadersAndBody_MapsResult()
    {
        var stub = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.Created, "{\"username\":\"newbie\",\"role\":\"USER\",\"tenantCode\":\"demo-a\"}"));
        var svc = Build(stub);

        var result = await svc.RegisterAsync(new RegisterRequest("newbie", "password123", "demo-a", "demo-a-invite"));

        Assert.Equal("newbie", result.Username);
        Assert.Equal("USER", result.Role);
        Assert.Equal("demo-a", result.TenantCode);

        Assert.Equal("http://backend/api/auth/register", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        // auth 是登入前呼叫,不帶身分 header。
        Assert.False(stub.HasHeader("X-User-Id"));

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("newbie", doc.RootElement.GetProperty("username").GetString());
        Assert.Equal("password123", doc.RootElement.GetProperty("password").GetString());
        Assert.Equal("demo-a", doc.RootElement.GetProperty("tenantCode").GetString());
        Assert.Equal("demo-a-invite", doc.RootElement.GetProperty("inviteCode").GetString());
    }

    [Fact]
    public async Task Register_404_ThrowsTenantNotFound_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => Error(HttpStatusCode.NotFound, "找不到租戶：nope")));

        var ex = await Assert.ThrowsAsync<TenantNotFoundException>(() =>
            svc.RegisterAsync(new RegisterRequest("x", "password123", "nope", "x")));
        Assert.Equal("找不到租戶：nope", ex.Message);
    }

    [Fact]
    public async Task Register_403_ThrowsInvalidInviteCode_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => Error(HttpStatusCode.Forbidden, "邀請碼無效")));

        var ex = await Assert.ThrowsAsync<InvalidInviteCodeException>(() =>
            svc.RegisterAsync(new RegisterRequest("x", "password123", "demo-a", "wrong")));
        Assert.Equal("邀請碼無效", ex.Message);
    }

    [Fact]
    public async Task Register_409_ThrowsUsernameTaken_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => Error(HttpStatusCode.Conflict, "使用者名稱已存在：user-a")));

        var ex = await Assert.ThrowsAsync<UsernameTakenException>(() =>
            svc.RegisterAsync(new RegisterRequest("user-a", "password123", "demo-a", "demo-a-invite")));
        Assert.Equal("使用者名稱已存在：user-a", ex.Message);
    }

    // ---- login ----

    [Fact]
    public async Task Login_Succeeds_ReturnsBackendToken_AndSendsBody()
    {
        var stub = new StubHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, "{\"token\":\"backend.signed.jwt\",\"username\":\"user-a\",\"role\":\"USER\",\"tenantCode\":\"demo-a\"}"));
        var svc = Build(stub);

        var result = await svc.LoginAsync(new LoginRequest("user-a", "password123"));

        Assert.Equal("backend.signed.jwt", result.Token);
        Assert.Equal("user-a", result.Username);
        Assert.Equal("USER", result.Role);
        Assert.Equal("demo-a", result.TenantCode);

        Assert.Equal("http://backend/api/auth/login", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("user-a", doc.RootElement.GetProperty("username").GetString());
        Assert.Equal("password123", doc.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Login_401_ThrowsInvalidCredentials_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => Error(HttpStatusCode.Unauthorized, "帳號或密碼錯誤")));

        var ex = await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            svc.LoginAsync(new LoginRequest("user-a", "wrong")));
        Assert.Equal("帳號或密碼錯誤", ex.Message);
    }

    [Fact]
    public async Task Login_500_ThrowsBackendCall_NotMappedTo502()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<BackendCallException>(() => svc.LoginAsync(new LoginRequest("user-a", "password123")));
    }
}
