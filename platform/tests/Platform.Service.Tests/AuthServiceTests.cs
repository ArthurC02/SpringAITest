using System.Net;
using System.Text.Json;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class AuthServiceTests
{
    private static AuthService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    // ---- register ----

    [Fact]
    public async Task Register_Succeeds_SendsHeadersAndBody_MapsResult()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.Created, "{\"username\":\"newbie\",\"role\":\"USER\",\"tenantCode\":\"demo-a\"}"));
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
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.NotFound, "找不到租戶：nope")));

        var ex = await Assert.ThrowsAsync<TenantNotFoundException>(() =>
            svc.RegisterAsync(new RegisterRequest("x", "password123", "nope", "x")));
        Assert.Equal("找不到租戶：nope", ex.Message);
    }

    [Fact]
    public async Task Register_403_ThrowsInvalidInviteCode_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "邀請碼無效")));

        var ex = await Assert.ThrowsAsync<InvalidInviteCodeException>(() =>
            svc.RegisterAsync(new RegisterRequest("x", "password123", "demo-a", "wrong")));
        Assert.Equal("邀請碼無效", ex.Message);
    }

    [Fact]
    public async Task Register_409_ThrowsUsernameTaken_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Conflict, "使用者名稱已存在：user-a")));

        var ex = await Assert.ThrowsAsync<UsernameTakenException>(() =>
            svc.RegisterAsync(new RegisterRequest("user-a", "password123", "demo-a", "demo-a-invite")));
        Assert.Equal("使用者名稱已存在：user-a", ex.Message);
    }

    // 2xx 但沒有可用 body 的等價類:backend 回 JSON null(走 onEmptyBody)或整包沒有 body(反序列化擲
    // JsonException → WrapTransport),兩者都必須收斂成帶「認證服務呼叫失敗：」前綴的 BackendCallException
    // (對外 500),不得被當成註冊成功而回一個欄位全空的 AuthResult。
    [Theory]
    [InlineData("null", "認證服務呼叫失敗：回應內容為空")]
    [InlineData("", "認證服務呼叫失敗：")]
    public async Task Register_Backend2xxWithoutUsableBody_ThrowsBackendCall(string body, string expectedPrefix)
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, body)));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() =>
            svc.RegisterAsync(new RegisterRequest("newbie", "password123", "demo-a", "demo-a-invite")));
        Assert.StartsWith(expectedPrefix, ex.Message);
    }

    // ---- login ----

    [Fact]
    public async Task Login_Succeeds_ReturnsBackendToken_AndSendsBody()
    {
        var stub = new StubHttpMessageHandler(_ =>
            TestHttp.Json(HttpStatusCode.OK, "{\"token\":\"backend.signed.jwt\",\"username\":\"user-a\",\"role\":\"USER\",\"tenantCode\":\"demo-a\"}"));
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
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Unauthorized, "帳號或密碼錯誤")));

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

    // 「backend 沒有回應」與「backend 回了 500」是兩條不同的程式路徑(WrapTransport vs MapErrorAsync),
    // 對外同為 BackendCallException(500):傳輸層例外必須被包起來(保留原例外),不得讓
    // HttpRequestException 逸出成未處理例外。
    [Fact]
    public async Task Login_TransportFailure_ThrowsBackendCall_WrappingOriginal()
    {
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("backend 不可達")));

        var ex = await Assert.ThrowsAsync<BackendCallException>(() =>
            svc.LoginAsync(new LoginRequest("user-a", "password123")));
        Assert.StartsWith("認證服務呼叫失敗：", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
