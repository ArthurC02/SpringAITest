using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

/// <summary>
/// Skill 代理端點(AT4-12)。CRUD → backend;catalog/validate/invoke/nodes → workflow 引擎。
/// 兩件事在這層驗:(a) 沒有 JWT 一律 401 且請求不得抵達任何下游;
/// (b) 下游的狀態碼與 ApiError(含 fieldErrors)原樣穿透,代理層不改寫。
/// 身分 header 的實際附加由 SkillServiceTests / WorkflowServiceTests 以 stub handler 驗(此處下游是 fake service)。
/// </summary>
[Collection("EngineCalls")]
public sealed class SkillApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillApiTests(TestWebAppFactory factory) => _factory = factory;

    private static string Yaml(string name) => $"name: {name}\ndescription: 季報問答\nflow:\n  - node: query_intake\n";

    private static object Body(string name = "quarterly_qa") => new { definition = Yaml(name) };

    private static object InvalidBody() => new { definition = "name: bad\nflow:\n  - loop: __invalid__\n" };

    // ---- AT4-12(前半):無 JWT → 401,且請求不得抵達下游 ----

    [Theory]
    [InlineData("GET", "/api/skills")]
    [InlineData("GET", "/api/skills/catalog")]
    [InlineData("GET", "/api/skills/echo_skill")]
    [InlineData("GET", "/api/skills/echo_skill/revisions")]
    [InlineData("GET", "/api/skills/echo_skill/export")]
    [InlineData("POST", "/api/skills")]
    [InlineData("PUT", "/api/skills/echo_skill")]
    [InlineData("DELETE", "/api/skills/echo_skill")]
    [InlineData("POST", "/api/skills/validate")]
    [InlineData("POST", "/api/skills/echo_skill/invoke")]
    [InlineData("GET", "/api/nodes")]
    public async Task Endpoints_Return401_WithoutToken_AndNeverReachDownstream(string method, string path)
    {
        var beforeBackend = FakeSkillService.Calls.Count;
        var beforeEngine = FakeWorkflowService.EngineCalls.Count;

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

        // 兩個下游都不得被碰到。
        Assert.Equal(beforeBackend, FakeSkillService.Calls.Count);
        Assert.Equal(beforeEngine, FakeWorkflowService.EngineCalls.Count);
    }

    // ---- AT4-12(後半):帶合法 JWT → 轉發並回傳下游內容 ----

    [Fact]
    public async Task List_Returns200_SnakeCaseFields()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var item = Assert.Single((await resp.ReadJsonAsync()).AsArray())!;
        Assert.Equal("echo_skill", item["name"]!.GetValue<string>());
        Assert.Equal("USER", item["required_role"]!.GetValue<string>());
        Assert.True(item["enabled"]!.GetValue<bool>());
        Assert.Equal(1, item["current_revision"]!.GetValue<int>());
        Assert.Equal("2026-07-14T00:00:00Z", item["updated_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_Returns200_WithDefinition()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/quarterly_qa");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("quarterly_qa", body["name"]!.GetValue<string>());
        Assert.Contains("node: query_intake", body["definition"]!.GetValue<string>());
    }

    [Fact]
    public async Task Revisions_Returns200_DescendingSnakeCase()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/quarterly_qa/revisions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal(2, arr[0]!["revision"]!.GetValue<int>());
        Assert.Equal("sha2", arr[0]!["definition_sha256"]!.GetValue<string>());
        Assert.Equal("admin-a", arr[0]!["created_by"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_Returns201_WithSkill()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", Body());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("quarterly_qa", body["name"]!.GetValue<string>());
        Assert.Equal(1, body["current_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Update_Returns200_WithBumpedRevision()
    {
        var resp = await _factory.AdminClient().PutAsJsonAsync("/api/skills/quarterly_qa", Body());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.ReadJsonAsync())["current_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Delete_Returns204()
    {
        var resp = await _factory.AdminClient().DeleteAsync("/api/skills/quarterly_qa");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact] // 匯出:200、application/zip、Content-Disposition filename=<name>.zip、body bytes 一致。
    public async Task Export_Returns200_ZipBytes_WithAttachmentFilename()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/quarterly_qa/export");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        var disposition = resp.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("quarterly_qa.zip",
            disposition.FileNameStar ?? disposition.FileName!.Trim('"'));
        Assert.Equal(FakeSkillService.ExportBytes, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Export_Returns404_WhenBackendNotFound()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/ghost/export");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.Equal("找不到 Skill：ghost", body["message"]!.GetValue<string>());
    }

    // ---- 下游錯誤原樣穿透 ----

    [Fact] // backend 422(定義未通過引擎驗證)→ 對外 422,fieldErrors 帶引擎錯誤碼(編輯器要指到規則與行號)。
    public async Task Create_BackendValidationFailed_Returns422_WithEngineCodes()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", InvalidBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(422, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 定義驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("loop 缺少 max_iterations（第 7 行）",
            body["fieldErrors"]!["unbounded_loop"]!.GetValue<string>());
        Assert.NotNull(body["timestamp"]);
    }

    [Fact] // PUT 也走同一條 422 路徑(不能只擋 POST)。
    public async Task Update_BackendValidationFailed_Returns422()
    {
        var resp = await _factory.AdminClient().PutAsJsonAsync("/api/skills/quarterly_qa", InvalidBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.NotNull((await resp.ReadJsonAsync())["fieldErrors"]!["unbounded_loop"]);
    }

    [Fact]
    public async Task Create_Returns409_WithBackendMessage_Unchanged()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", Body("dup_skill"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(409, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 名稱已存在：dup_skill", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_Returns404_WhenBackendNotFound()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/ghost");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.Equal("找不到 Skill：ghost", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
    }

    [Fact] // backend 400 的欄位級錯誤不得被代理層吞掉。
    public async Task Create_BackendBadInputWithFieldErrors_ForwardsFieldErrors()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", Body("bad_field"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("definition 不可為空", body["fieldErrors"]!["definition"]!.GetValue<string>());
    }

    // ---- body 驗證:definition 是唯一欄位且必填(platform 先擋掉明顯無效的請求)----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_Returns400_WhenDefinitionBlank_AndNeverReachesBackend(string definition)
    {
        var before = FakeSkillService.Calls.Count;

        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", new { definition });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("definition 不可為空",
            (await resp.ReadJsonAsync())["fieldErrors"]!["definition"]!.GetValue<string>());
        Assert.Equal(before, FakeSkillService.Calls.Count);
    }

    // ---- 引擎端點:catalog / nodes / validate / invoke ----

    [Fact]
    public async Task Catalog_Returns200_MergedListWithSourceBadge()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/catalog");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal("builtin", arr[0]!["source"]!.GetValue<string>());
        Assert.Equal("custom", arr[1]!["source"]!.GetValue<string>());
    }

    // 路由優先序:字面段 catalog 勝過參數段 {name}。
    // 就算租戶裡真的有一個名叫 "catalog" 的 skill,GET /api/skills/catalog 也必須走引擎目錄,
    // 不得被 CRUD 的 GET {name} 吃掉(否則 Tab1 的清單會突然變成一筆 skill 內容)。
    [Fact]
    public async Task Catalog_IsNotShadowedBy_SkillNamed_catalog()
    {
        var beforeGet = FakeSkillService.Calls.Count(c => c == "get:catalog");

        var resp = await _factory.AdminClient().GetAsync("/api/skills/catalog");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // 回的是目錄(陣列且帶 source),不是單一 skill 物件。
        var json = await resp.ReadJsonAsync();
        Assert.NotNull(json.AsArray());
        Assert.All(json.AsArray(), n => Assert.NotNull(n!["source"]));
        // CRUD 的 Get 完全沒被呼叫。
        Assert.Equal(beforeGet, FakeSkillService.Calls.Count(c => c == "get:catalog"));
    }

    // 同理:POST /api/skills/validate 不得被 POST /api/skills/{name}/invoke 或 Create 吃掉。
    [Fact]
    public async Task Validate_Returns200_EvenWhenDefinitionInvalid_AndDoesNotWriteToBackend()
    {
        var before = FakeSkillService.Calls.Count;

        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills/validate", InvalidBody());

        // 引擎契約:一律 200,valid/errors 在 body(不合法不是 HTTP 錯誤)。
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.False(body["valid"]!.GetValue<bool>());
        Assert.Equal("unbounded_loop", body["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(7, body["errors"]![0]!["line"]!.GetValue<int>());

        // 無副作用:驗證不得寫入 backend。
        Assert.Equal(before, FakeSkillService.Calls.Count);
    }

    [Fact]
    public async Task Validate_Returns200_WithSkillMetadata_WhenValid()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills/validate", Body());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.True(body["valid"]!.GetValue<bool>());
        Assert.Equal("quarterly_qa", body["skill"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_Returns200_WithEngineOutput()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync(
            "/api/skills/quarterly_qa/invoke", new { input = new { query = "2025Q3" } });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("quarterly_qa", body["skill"]!.GetValue<string>());
        Assert.Equal("42", body["output"]!["answer"]!.GetValue<string>());
    }

    // invoke 的下游狀態碼映射:404 → NotFound、403 → Forbidden、422 → BadInput(400)、其他 → 502。
    [Theory]
    [InlineData("ghost", HttpStatusCode.NotFound)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("badinput", HttpStatusCode.BadRequest)]
    [InlineData("boom", HttpStatusCode.BadGateway)]
    public async Task Invoke_DownstreamError_MapsToSameStatus(string name, HttpStatusCode expected)
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync(
            $"/api/skills/{name}/invoke", new { input = new { query = "x" } });

        Assert.Equal(expected, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal((int)expected, body["status"]!.GetValue<int>());
        Assert.NotNull(body["timestamp"]);
        Assert.NotNull(body["fieldErrors"]);
    }

    [Fact]
    public async Task Nodes_Returns200_WithNodeContracts()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/nodes");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var node = Assert.Single((await resp.ReadJsonAsync()).AsArray())!;
        Assert.Equal("query_intake", node["name"]!.GetValue<string>());
        Assert.Equal("1.0", node["version"]!.GetValue<string>());
        Assert.NotNull(node["reads"]!.AsArray());
        Assert.NotNull(node["writes"]!.AsArray());
        Assert.NotNull(node["requires_tools"]!.AsArray());
    }

    // USER 角色照樣能讀清單/目錄/節點(角色把關在下游;platform 只驗 JWT)。
    [Fact]
    public async Task User_CanReadCatalogAndNodes()
    {
        var user = _factory.CreateClient().WithToken(_factory.IssueToken("user-a", "USER", "demo-a"));

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/skills/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/skills")).StatusCode);
    }
}
