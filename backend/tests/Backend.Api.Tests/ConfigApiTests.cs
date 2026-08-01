using System.Net;
using System.Net.Http.Json;
using Backend.Api.Config;

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

    // 0 筆邊界:從未寫過任何 key 的全新租戶 → 200 + 空陣列(不是 404、不是 null)。
    // 每租戶各自獨立、DB 不種共用預設列,所以「空」才是新租戶的正常起點。
    [Fact]
    public async Task Get_TenantWithNoConfig_Returns200_WithEmptyArray()
    {
        var resp = await Admin("never-configured").GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await resp.ReadJsonAsync()).AsArray());
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

    // ---- P1 runtime read(/api/config/runtime/{key}):執行期單鍵讀取,非 ADMIN、非整包 ----

    [Fact]
    public async Task GetRuntime_AllowlistedKeyWithValue_Returns200_WithoutAdminRole()
    {
        var admin = Admin("runtime-ok");
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(
                "/api/config/" + ConfigController.PromptManifestRevisionKey, new { value = "7" })).StatusCode);

        // 執行期讀取刻意不帶 X-User-Role:比照 ConfigurationSetController 的 active 路由,只靠
        // X-Internal-Token + X-Tenant-Id 的信任邊界,不要求呼叫端的角色。
        var nonAdmin = _factory.CreateInternalClient().WithTenant("runtime-ok");
        var resp = await nonAdmin.GetAsync("/api/config/runtime/" + ConfigController.PromptManifestRevisionKey);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(ConfigController.PromptManifestRevisionKey, body["key"]!.GetValue<string>());
        Assert.Equal("7", body["value"]!.GetValue<string>());
    }

    // allowlist 外的 key(即使是完全合法、已寫入的組態值)一律 404 —— 不得成為讀任意 ADMIN 內容的後門。
    [Fact]
    public async Task GetRuntime_KeyOutsideAllowlist_Returns404()
    {
        var admin = Admin("runtime-outside-allowlist");
        await admin.PutAsJsonAsync("/api/config/theme", new { value = "dark" });

        var resp = await _factory.CreateInternalClient().WithTenant("runtime-outside-allowlist")
            .GetAsync("/api/config/runtime/theme");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // allowlist 內但本租戶尚未設定值 → 同樣 404(canary 未啟用,不是伺服器錯誤)。
    [Fact]
    public async Task GetRuntime_AllowlistedKeyWithoutValue_Returns404()
    {
        var resp = await _factory.CreateInternalClient().WithTenant("runtime-unset")
            .GetAsync("/api/config/runtime/" + ConfigController.PromptManifestRevisionKey);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetRuntime_MissingTenantHeader_Returns400()
    {
        var resp = await _factory.CreateInternalClient()
            .GetAsync("/api/config/runtime/" + ConfigController.PromptManifestRevisionKey);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
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
    // JSON 顯式 null 走的是 NotBlankAttribute 另一條分支(`value is null` 早於 IsNullOrWhiteSpace),
    // 對外結果必須完全相同 — 同一個 400 + 同一句 fieldErrors.value。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Put_Admin_BlankValue_Returns400(string? value)
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
