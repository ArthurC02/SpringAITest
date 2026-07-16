using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

/// <summary>
/// Configuration Set 代理端點(SSR-P4-011)。單一下游:backend CRUD。此層驗:
/// (a) 沒有 JWT 一律 401 且請求不得抵達下游;
/// (b) 六個公開端點的狀態碼與 ApiError(含 fieldErrors)原樣穿透,代理層不改寫;
/// (c) **無 active-set 讀取捷徑** —— GET /api/configuration-sets/active 不得被 {id} 參數段吞掉
///     而轉發到 backend 的 active 端點({id:guid} 約束擋下非 uuid 段)。
/// 身分 header 的實際附加由 ConfigurationSetServiceTests 以 stub handler 驗(此處下游是 fake service)。
/// </summary>
public sealed class ConfigurationSetApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConfigurationSetApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient AdminClient()
        => _factory.CreateClient().WithToken(_factory.IssueToken("admin-a", "ADMIN", "demo-a"));

    private static readonly string Id = FakeConfigurationSetService.ExistingId;
    private static readonly string GhostId = FakeConfigurationSetService.GhostId;

    private static object Body(string name = "prod") => new
    {
        name,
        values = new Dictionary<string, object> { ["retrieval.top_k"] = 8, ["llm.model"] = "gpt-4o-mini" },
    };

    // ---- 無 JWT → 401,且請求不得抵達下游 ----

    [Theory]
    [InlineData("GET", "/api/configuration-sets")]
    [InlineData("GET", "/api/configuration-sets/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/configuration-sets")]
    [InlineData("PUT", "/api/configuration-sets/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/api/configuration-sets/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/configuration-sets/11111111-1111-1111-1111-111111111111/activate")]
    public async Task Endpoints_Return401_WithoutToken_AndNeverReachDownstream(string method, string path)
    {
        var before = FakeConfigurationSetService.Calls.Count;

        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            req.Content = JsonContent.Create(Body());
        }

        var resp = await _factory.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(401, body["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        Assert.NotNull(body["timestamp"]);
        Assert.NotNull(body["fieldErrors"]);

        Assert.Equal(before, FakeConfigurationSetService.Calls.Count);
    }

    // ---- 六個公開端點:帶合法 JWT → 轉發並回傳下游內容 ----

    [Fact]
    public async Task List_Returns200_SnakeCase_OmitsValues()
    {
        var resp = await AdminClient().GetAsync("/api/configuration-sets");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var item = Assert.Single((await resp.ReadJsonAsync()).AsArray())!;
        Assert.Equal("prod", item["name"]!.GetValue<string>());
        Assert.True(item["is_active"]!.GetValue<bool>());
        Assert.Equal("2026-07-14T00:00:00Z", item["updated_at"]!.GetValue<string>());
        // 清單省略 values。
        Assert.Null(item["values"]);
    }

    [Fact]
    public async Task Get_Returns200_WithValues()
    {
        var resp = await AdminClient().GetAsync($"/api/configuration-sets/{Id}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(Id, body["id"]!.GetValue<string>());
        Assert.Equal(8, body["values"]!["retrieval.top_k"]!.GetValue<int>());
        Assert.Equal("gpt-4o-mini", body["values"]!["llm.model"]!.GetValue<string>());
        Assert.Equal("admin-a", body["created_by"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_Returns201_WithSet()
    {
        var resp = await AdminClient().PostAsJsonAsync("/api/configuration-sets", Body());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("prod", body["name"]!.GetValue<string>());
        Assert.False(body["is_active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Update_Returns200()
    {
        var resp = await AdminClient().PutAsJsonAsync($"/api/configuration-sets/{Id}", Body("renamed"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("renamed", (await resp.ReadJsonAsync())["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_Returns204()
    {
        var resp = await AdminClient().DeleteAsync($"/api/configuration-sets/{Id}");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Activate_Returns200_IsActiveTrue()
    {
        var resp = await AdminClient().PostAsJsonAsync($"/api/configuration-sets/{Id}/activate", new { });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await resp.ReadJsonAsync())["is_active"]!.GetValue<bool>());
    }

    // ---- 下游錯誤原樣穿透 ----

    [Fact] // 跨租戶 / 不存在 → backend 404,原樣轉發(租戶隔離也走此路徑)。
    public async Task Get_Returns404_WhenBackendNotFound()
    {
        var resp = await AdminClient().GetAsync($"/api/configuration-sets/{GhostId}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.Equal("找不到 Configuration Set", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
    }

    [Fact]
    public async Task Create_Returns409_WithBackendMessage_Unchanged()
    {
        var resp = await AdminClient().PostAsJsonAsync("/api/configuration-sets", Body("dup_set"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(409, body["status"]!.GetValue<int>());
        Assert.Equal("Configuration Set 名稱已存在：dup_set", body["message"]!.GetValue<string>());
    }

    [Fact] // values 越界 → backend 422,fieldErrors 帶越界鍵(前端才指得出哪個鍵)。
    public async Task Create_Returns422_WithValueRangeFieldErrors()
    {
        var resp = await AdminClient().PostAsJsonAsync("/api/configuration-sets", Body("bad_values"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(422, body["status"]!.GetValue<int>());
        Assert.Equal("Configuration Set 驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("必須介於 1 到 50", body["fieldErrors"]!["retrieval.top_k"]!.GetValue<string>());
    }

    [Fact] // PUT 也走同一條 422 路徑(不能只擋 POST)。
    public async Task Update_Returns422_ForBadValues()
    {
        var resp = await AdminClient().PutAsJsonAsync($"/api/configuration-sets/{Id}", Body("bad_values"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.NotNull((await resp.ReadJsonAsync())["fieldErrors"]!["retrieval.top_k"]);
    }

    // ---- SSR-P4-011 核心:無 active-set 讀取捷徑 ----

    // GET /api/configuration-sets/active 不是公開端點:literal "active" 非 uuid → 不匹配 {id:guid},
    // 不得被當成 Get(id="active") 轉發到 backend 的 active 端點(那是 workflow 執行期直連的取值路徑)。
    [Fact]
    public async Task ActiveRead_HasNoShortcut_NotProxiedToBackend()
    {
        var before = FakeConfigurationSetService.Calls.Count;

        var resp = await AdminClient().GetAsync("/api/configuration-sets/active");

        // 沒有對應路由 → 404,且絕不觸及下游(不存在 get:active 的呼叫)。
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(before, FakeConfigurationSetService.Calls.Count);
        Assert.DoesNotContain("get:active", FakeConfigurationSetService.Calls);
    }

    // POST /api/configuration-sets/active/activate 同理不得成立(非 uuid 段)。
    [Fact]
    public async Task ActivateWithNonGuidSegment_HasNoRoute()
    {
        var resp = await AdminClient().PostAsJsonAsync("/api/configuration-sets/active/activate", new { });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
