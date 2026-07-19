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
    public async Task List_Returns200_WithToken()
    {
        var resp = await _factory.UserClient().GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Single(arr);
        Assert.Equal("chat_model", arr[0]!["key"]!.GetValue<string>());
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

    [Fact]
    public async Task Update_Returns400_WhenValueMissing()
    {
        var resp = await _factory.AdminClient().PutAsJsonAsync("/api/config/chat_model", new { });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("value 不可為空", body["fieldErrors"]!["value"]!.GetValue<string>());
    }
}
