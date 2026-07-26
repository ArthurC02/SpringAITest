using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

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

    private static object Body(string name = "quarterly-qa") => new { definition = Yaml(name) };

    private static object InvalidBody() => new { definition = "name: bad\nflow:\n  - loop: __invalid__\n" };

    // ---- AT4-12(前半):無 JWT → 401,且請求不得抵達下游 ----

    [Theory]
    [InlineData("GET", "/api/skills")]
    [InlineData("GET", "/api/skills/catalog")]
    [InlineData("GET", "/api/skills/echo-skill")]
    [InlineData("GET", "/api/skills/echo-skill/revisions")]
    [InlineData("POST", "/api/skills/echo-skill/revisions/1/restore")]
    [InlineData("GET", "/api/skills/echo-skill/export")]
    [InlineData("POST", "/api/skills")]
    [InlineData("POST", "/api/skills/echo-skill/import")]
    [InlineData("PUT", "/api/skills/echo-skill")]
    [InlineData("DELETE", "/api/skills/echo-skill")]
    [InlineData("POST", "/api/skills/validate")]
    [InlineData("POST", "/api/skills/echo-skill/invoke")]
    [InlineData("GET", "/api/nodes")]
    public async Task Endpoints_Return401_WithoutToken_AndNeverReachDownstream(string method, string path)
    {
        var beforeBackend = FakeSkillService.Calls.Count;
        var beforeEngine = FakeWorkflowService.EngineCalls.Count;

        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (path.EndsWith("/import", StringComparison.Ordinal))
        {
            // import 是 multipart/form-data 端點:送對應媒體型別,確保驗的是「無 JWT → 401」而非內容協商的 415。
            req.Content = Package();
        }
        else if (method is "POST" or "PUT")
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
        Assert.Equal("echo-skill", item["name"]!.GetValue<string>());
        Assert.Equal("USER", item["required_role"]!.GetValue<string>());
        Assert.True(item["enabled"]!.GetValue<bool>());
        Assert.Equal(1, item["current_revision"]!.GetValue<int>());
        Assert.Equal("2026-07-14T00:00:00Z", item["updated_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_Returns200_WithDefinition()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/quarterly-qa");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("quarterly-qa", body["name"]!.GetValue<string>());
        Assert.Contains("node: query_intake", body["definition"]!.GetValue<string>());
    }

    [Fact]
    public async Task Revisions_Returns200_DescendingSnakeCase()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/quarterly-qa/revisions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal(2, arr[0]!["revision"]!.GetValue<int>());
        Assert.Equal("sha2", arr[0]!["definition_sha256"]!.GetValue<string>());
        Assert.Equal("admin-a", arr[0]!["created_by"]!.GetValue<string>());
    }

    [Fact]
    public async Task RestoreRevision_Admin_Returns200AndNewRevision()
    {
        var response = await _factory.AdminClient().PostAsync(
            "/api/skills/quarterly-qa/revisions/1/restore", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("quarterly-qa", body["name"]!.GetValue<string>());
        Assert.Equal(3, body["current_revision"]!.GetValue<int>());
        Assert.Contains("restore:quarterly-qa:1", FakeSkillService.Calls);
    }

    // restore 的角色把關與 legacy-package 409 都是 backend 的規則(SkillController.RestoreRevision 刻意沒有
    // [AdminOnly]),platform 端只有 WorkflowForbiddenException/DownstreamConflictException 的狀態碼映射,
    // 已由 GlobalExceptionHandlerTests 與 Create_Returns409_WithBackendMessage_Unchanged 覆蓋。

    [Fact]
    public async Task Create_Returns201_WithSkill()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", Body());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("quarterly-qa", body["name"]!.GetValue<string>());
        Assert.Equal(1, body["current_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Update_Returns200_WithBumpedRevision()
    {
        var resp = await _factory.AdminClient().PutAsJsonAsync("/api/skills/quarterly-qa", Body());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.ReadJsonAsync())["current_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Delete_Returns204()
    {
        var resp = await _factory.AdminClient().DeleteAsync("/api/skills/quarterly-qa");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact] // 匯出:200、application/zip、Content-Disposition filename=<name>.zip、body bytes 一致。
    public async Task Export_Returns200_ZipBytes_WithAttachmentFilename()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/quarterly-qa/export");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/zip", resp.Content.Headers.ContentType!.MediaType);
        var disposition = resp.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("quarterly-qa.zip",
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

    // ---- Agent Skill 匯入代理(multipart zip → backend);角色把關在 backend、ApiError 穿透 ----

    /// <summary>一份 multipart package upload(package 檔位);內容不重要,代理層只轉送。</summary>
    private static MultipartFormDataContent Package(string filename = "sales-helper.zip")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("PKzip-bytes"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "package", filename);
        return content;
    }

    // ADMIN 匯入 → 2xx,回應原樣穿透 backend 的 Skill JSON(含 additive kind)。
    [Fact]
    public async Task Import_Admin_Returns200_PassesThroughSkillWithKind()
    {
        var resp = await _factory.AdminClient().PostAsync("/api/skills/sales-helper/import", Package());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("sales-helper", body["name"]!.GetValue<string>());
        Assert.Equal("agentic", body["kind"]!.GetValue<string>());
        Assert.Equal(1, body["current_revision"]!.GetValue<int>());
    }

    // USER 匯入 → 403(AdminOnlyAttribute),且 authorization filter 早於 body binding:
    // 送一份超過 RequestSizeLimit 的 multipart 仍是 403(不是 413,也不是先解析完才拒絕),
    // 並帶完整 ApiError 形狀。這同時吸收了「USER 匯入 → 403 + status:403」的等價類。
    [Fact]
    public async Task Import_UserWithOversizeMultipart_Returns403BeforeBodyBinding()
    {
        var user = _factory.CreateClient().WithToken(
            _factory.IssueToken("user-a", "USER", "demo-a"));
        var before = FakeSkillService.Calls.Count(c => c == "import:sales-helper");
        using var oversized = new MultipartFormDataContent();
        oversized.Add(
            new ByteArrayContent(new byte[18 * 1024 * 1024]),
            "package",
            "oversized.zip");

        var response = await user.PostAsync(
            "/api/skills/sales-helper/import", oversized);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(403, (await response.ReadJsonAsync())["status"]!.GetValue<int>());
        Assert.Equal(
            before,
            FakeSkillService.Calls.Count(c => c == "import:sales-helper"));
    }

    // ADMIN 超過 Web 層的 17 MiB 上限:請求在 model binding 之前就被擋掉,一個位元組都不會進到 service 層
    // (與 SkillServiceTests 的 16 MiB 轉送前檢查是不同的兩層)。對外必須是 413 + 完整 ApiError:
    // model binding 的自動 400「輸入驗證失敗」帶空 fieldErrors,前端無從分辨「檔案太大」與「欄位錯」。
    // 兩條匯入路由各自掛限制,兩格都測(漏掛其中一條不會被單一代表值抓到)。
    [Theory]
    [InlineData("/api/skills/sales-helper/import", "import:sales-helper")]
    [InlineData("/api/skills/import", "import:server-derived")]
    public async Task Import_OverRequestSizeLimit_Returns413_BeforeReachingService(string path, string call)
    {
        var before = FakeSkillService.Calls.Count(c => c == call);
        using var oversized = new MultipartFormDataContent();
        oversized.Add(
            new ByteArrayContent(new byte[17 * 1024 * 1024 + 4096]),
            "package",
            "oversized.zip");

        var response = await _factory.AdminClient().PostAsync(path, oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(413, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 套件超過上傳大小上限（17 MiB）", body["message"]!.GetValue<string>());
        Assert.NotNull(body["timestamp"]);
        Assert.Empty(body["fieldErrors"]!.AsObject());
        Assert.Equal(before, FakeSkillService.Calls.Count(c => c == call));
    }

    [Fact]
    public async Task ImportDerived_UserWithOversizeMultipart_Returns403BeforeBodyBinding()
    {
        var user = _factory.CreateClient().WithToken(
            _factory.IssueToken("user-a", "USER", "demo-a"));
        var before = FakeSkillService.Calls.Count(c => c == "import:server-derived");
        using var oversized = new MultipartFormDataContent();
        oversized.Add(
            new ByteArrayContent(new byte[18 * 1024 * 1024]),
            "package",
            "oversized.zip");

        var response = await user.PostAsync("/api/skills/import", oversized);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            before,
            FakeSkillService.Calls.Count(c => c == "import:server-derived"));
    }

    // backend 套件驗證失敗(422)→ 對外 422,fieldErrors(引擎錯誤碼)原樣穿過代理層。
    [Fact]
    public async Task Import_BackendValidationFailed_Returns422_WithFieldErrors()
    {
        var resp = await _factory.AdminClient().PostAsync("/api/skills/badpkg/import", Package());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(422, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 套件驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("腳本未通過 AST 掃描", body["fieldErrors"]!["forbidden_script"]!.GetValue<string>());
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

    [Fact]
    public async Task Create_Returns409_WithBackendMessage_Unchanged()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", Body("dup-skill"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(409, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 名稱已存在：dup-skill", body["message"]!.GetValue<string>());
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
        var resp = await _factory.AdminClient().PostAsJsonAsync("/api/skills", Body("bad-field"));

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
    public async Task Catalog_Returns200_MergedListWithSourceAndBindableMetadata()
    {
        var resp = await _factory.AdminClient().GetAsync("/api/skills/catalog");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = (await resp.ReadJsonAsync()).AsArray();
        Assert.Equal(2, arr.Count);
        Assert.Equal("builtin", arr[0]!["source"]!.GetValue<string>());
        Assert.False(arr[0]!["bindable"]!.GetValue<bool>());
        Assert.Equal("custom", arr[1]!["source"]!.GetValue<string>());
        Assert.True(arr[1]!["bindable"]!.GetValue<bool>());
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

        // valid:true 分支也是 200,且 skill metadata 原樣帶回(同一路由的另一個等價類)。
        var valid = await _factory.AdminClient().PostAsJsonAsync("/api/skills/validate", Body());
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var validBody = await valid.ReadJsonAsync();
        Assert.True(validBody["valid"]!.GetValue<bool>());
        Assert.Equal("quarterly-qa", validBody["skill"]!["name"]!.GetValue<string>());

        // 無副作用:驗證(不論結果)都不得寫入 backend。
        Assert.Equal(before, FakeSkillService.Calls.Count);
    }

    [Fact]
    public async Task Invoke_Returns200_WithEngineOutput()
    {
        var resp = await _factory.AdminClient().PostAsJsonAsync(
            "/api/skills/quarterly-qa/invoke", new { input = new { query = "2025Q3" } });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("quarterly-qa", body["skill"]!.GetValue<string>());
        Assert.Equal("42", body["output"]!["answer"]!.GetValue<string>());
    }

    // additive 欄位穿透由 WorkflowServiceTests(真 WorkflowService + stub handler)覆蓋;
    // 在這一層用 CatalogOverride 塞 JSON 再讀回來只驗到 JsonElement 序列化,不經任何 platform 分支。

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
