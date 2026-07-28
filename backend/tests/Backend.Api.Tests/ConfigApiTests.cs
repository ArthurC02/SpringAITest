using System.Net;
using System.Net.Http.Json;

namespace Backend.Api.Tests;

public sealed class ConfigApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConfigApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant)
        => _factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant);

    [Fact]
    public async Task Get_Returns200_ListItemsCarryFullConfigItemShape()
    {
        // 先寫入一個已知 key,才有辦法驗 list 項目的欄位形狀(而不是只驗「陣列非 null」)。
        var admin = Admin("shape-tenant");
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync("/api/config/list-shape", new { value = "on" })).StatusCode);

        var resp = await admin.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var item = Assert.Single(
            (await resp.ReadJsonAsync()).AsArray(),
            n => n!["key"]!.GetValue<string>() == "list-shape")!.AsObject();
        // tenant_id 不得洩到 wire 契約:欄位就是 key/value/updatedAt 三個,不多不少。
        Assert.Equal(
            new[] { "key", "updatedAt", "value" },
            item.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("on", item["value"]!.GetValue<string>());
    }

    // 租戶隔離的 API 側:A 寫的 key 在 B 的 list 看不到,且兩租戶同名 key 值互不覆蓋。
    [Fact]
    public async Task CrossTenant_ConfigIsIsolated_SameKeyDoesNotOverwrite()
    {
        var a = Admin("iso-a");
        var b = Admin("iso-b");

        await a.PutAsJsonAsync("/api/config/only-a", new { value = "a-only" });
        Assert.Equal("prompt-a", (await (await a.PutAsJsonAsync(
            "/api/config/agent.defaults.system_prompt", new { value = "prompt-a" }))
            .ReadJsonAsync())["value"]!.GetValue<string>());
        Assert.Equal("prompt-b", (await (await b.PutAsJsonAsync(
            "/api/config/agent.defaults.system_prompt", new { value = "prompt-b" }))
            .ReadJsonAsync())["value"]!.GetValue<string>());

        var bList = (await (await b.GetAsync("/api/config")).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(bList, n => n!["key"]!.GetValue<string>() == "only-a");
        Assert.Equal("prompt-b", Assert.Single(
            bList, n => n!["key"]!.GetValue<string>() == "agent.defaults.system_prompt")!
            ["value"]!.GetValue<string>());

        // A 的值未被 B 覆寫。
        var aList = (await (await a.GetAsync("/api/config")).ReadJsonAsync()).AsArray();
        Assert.Equal("prompt-a", Assert.Single(
            aList, n => n!["key"]!.GetValue<string>() == "agent.defaults.system_prompt")!
            ["value"]!.GetValue<string>());
    }

    // 缺 X-Tenant-Id → RequireTenant 400(ADMIN 也一樣:租戶是查詢範圍,不是權限)。
    [Fact]
    public async Task Get_MissingTenantHeader_Returns400()
    {
        var resp = await _factory.CreateInternalClient().WithRole("ADMIN").GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Put_MissingTenantHeader_Returns400()
    {
        var resp = await _factory.CreateInternalClient().WithRole("ADMIN")
            .PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // 讀取也是 ADMIN-only:組態值(如 agent.defaults.system_prompt 全文)不得外洩給一般 USER。
    // 非 ADMIN(USER)與缺角色 header(null)同屬「權限不足」等價類。
    [Theory]
    [InlineData(null)]
    [InlineData("USER")]
    public async Task Get_NonAdmin_Returns403(string? role)
    {
        var client = _factory.CreateInternalClient();
        if (role is not null)
        {
            client.WithRole(role);
        }

        var resp = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("權限不足，無法讀取系統組態", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
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
        var client = Admin("put-tenant");

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("theme", body["key"]!.GetValue<string>());
        Assert.Equal("dark", body["value"]!.GetValue<string>());
        Assert.NotNull(body["updatedAt"]);
    }

    // NotBlank 對「空字串」與「只有空白」同屬一個等價類(比照 SecurityTests 的
    // InternalTokenResolver_EmptyOrWhitespace_Throws 慣例,兩個變體一起釘)。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Put_Admin_BlankValue_Returns400(string value)
    {
        var client = Admin("blank-tenant");

        var resp = await client.PutAsJsonAsync("/api/config/theme", new { value });

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
