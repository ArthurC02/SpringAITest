using System.Net;
using System.Net.Http.Json;

namespace Backend.Api.Tests;

public sealed class ConfigApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConfigApiTests(TestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_Returns200_List()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull((await resp.ReadJsonAsync()).AsArray());
    }

    // 非 ADMIN(USER)與缺角色 header(null)同屬「權限不足」等價類:PUT 一律 403 + 同一訊息。
    [Theory]
    [InlineData(null)]
    [InlineData("USER")]
    public async Task Put_NonAdmin_Returns403(string? role)
    {
        var client = _factory.CreateInternalClient();
        if (role is not null)
        {
            client.WithRole(role);
        }

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("權限不足，無法修改系統組態", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Put_Admin_Returns200_WithItem()
    {
        var client = _factory.CreateInternalClient().WithRole("ADMIN");

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("theme", body["key"]!.GetValue<string>());
        Assert.Equal("dark", body["value"]!.GetValue<string>());
        Assert.NotNull(body["updatedAt"]);
    }

    [Fact]
    public async Task Put_Admin_BlankValue_Returns400()
    {
        var client = _factory.CreateInternalClient().WithRole("ADMIN");

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("value 不可為空", (await resp.ReadJsonAsync())["fieldErrors"]!["value"]!.GetValue<string>());
    }

    // [AdminOnly] 是 authorization filter,早於模型驗證:非 ADMIN 送不合法 body 也是 403,
    // 不會先被 400 短路而洩漏欄位規則(探測防護的另一半 — 對比 Put_Admin_BlankValue_Returns400)。
    [Theory]
    [InlineData(null)]
    [InlineData("USER")]
    public async Task Put_NonAdmin_BlankValue_Returns403_NotBadRequest(string? role)
    {
        var client = _factory.CreateInternalClient();
        if (role is not null)
        {
            client.WithRole(role);
        }

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("權限不足，無法修改系統組態", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }
}
