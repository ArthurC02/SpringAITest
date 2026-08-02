using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class ConfigApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConfigApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task List_Returns401_WithoutToken()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_Returns200_AsAdmin()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Single(arr);
        Assert.Equal("chat_model", arr[0]!["key"]!.GetValue<string>());
    }

    // 讀取收成 ADMIN-only 後的另一半決策表:backend 403 → 對外 403 + 標準 ApiError 形狀
    // (授權在 backend,platform 透明轉發 —— 見 ConfigServiceTests.List_Backend403_ThrowsWorkflowForbidden)。
    [Fact]
    public async Task List_Returns403_AsNonAdmin_WithApiError()
    {
        var resp = await _factory.UserClient().GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("權限不足，無法讀取系統組態", body["message"]!.GetValue<string>());
        Assert.Equal(403, body["status"]!.GetValue<int>());
        Assert.NotNull(body["timestamp"]);
        Assert.NotNull(body["fieldErrors"]);
    }

    // 認證狀態 × 端點的另一格:類別層級單一 [Authorize] 也要罩到 PUT ——
    // 帶合法 body 但無 token 仍須在進入 action 前被 401 擋下(不會走到 FakeConfigService)。
    [Fact]
    public async Task Update_Returns401_WithoutToken()
    {
        var resp = await _factory.CreateClient().PutAsJsonAsync("/api/config/chat_model", new { value = "gpt-4o" });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("未認證或憑證無效", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_Returns200_AsAdmin()
    {
        var resp = await _factory.AdminClient().PutAsJsonAsync("/api/config/chat_model", new { value = "gpt-4o" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("chat_model", body["key"]!.GetValue<string>());
        Assert.Equal("gpt-4o", body["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_Returns403_AsNonAdmin_WithMessage()
    {
        var resp = await _factory.UserClient().PutAsJsonAsync("/api/config/chat_model", new { value = "gpt-4o" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("權限不足，無法修改系統組態", body["message"]!.GetValue<string>());
    }

    // NotBlank 的三個等價類:欄位缺漏(null)、空字串、全空白。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_Returns400_WhenValueBlank(string? value)
    {
        var payload = value is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["value"] = value };

        var resp = await _factory.AdminClient().PutAsJsonAsync("/api/config/chat_model", payload);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("value 不可為空", body["fieldErrors"]!["value"]!.GetValue<string>());
    }

    // 角色 × body 合法性的第四格(非 ADMIN + 空白 value):釘住優先序 ——
    // [ApiController] 的自動模型驗證是 action filter,早於 action 內才呼叫的 service 授權檢查,
    // 所以驗證先贏:回 400「輸入驗證失敗」,而不是 403(下游 FakeConfigService 根本沒被呼叫)。
    [Fact]
    public async Task Update_Returns400_NotForbidden_WhenNonAdminSendsBlankValue()
    {
        var resp = await _factory.UserClient().PutAsJsonAsync("/api/config/chat_model", new { value = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("value 不可為空", body["fieldErrors"]!["value"]!.GetValue<string>());
    }
}
