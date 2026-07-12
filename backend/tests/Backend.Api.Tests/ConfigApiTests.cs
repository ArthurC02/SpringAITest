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

    [Fact]
    public async Task Put_NonAdmin_Returns403()
    {
        var client = _factory.CreateInternalClient().WithRole("USER");

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("權限不足，無法修改系統組態", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Put_NoRoleHeader_Returns403()
    {
        var client = _factory.CreateInternalClient();

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
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
}
