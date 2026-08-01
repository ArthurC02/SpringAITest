using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

/// <summary>
/// SkillService(代理 backend /api/skills)。這一層驗兩件事:
/// (a) AT4-12 的後半段 — 每個請求都帶 X-Internal-Token + 三個身分 header(取自已驗證的 JWT claims);
/// (b) backend 的狀態碼與 ApiError 原樣轉發,400/422 的 fieldErrors 不得被吞掉。
/// </summary>
public sealed class SkillServiceTests
{
    private static readonly UserContext AdminCtx = new("admin-a", "demo-a", "ADMIN");

    private const string Yaml = "name: quarterly-qa\ndescription: 季報問答\nflow:\n  - node: query_intake\n";

    private const string SkillJson =
        """{"name":"quarterly-qa","description":"季報問答","definition":"name: quarterly-qa","required_role":"USER","enabled":true,"current_revision":3,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z","kind":"flow"}""";

    private static SkillService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    private static SkillUpsert Upsert() => new(Yaml);

    // A1:List 原樣穿透 backend JSON(snake_case),backend 新增欄位不被 DTO 靜默吃掉。
    [Fact]
    public async Task List_PassesThroughSnakeCase_ForwardsIdentityHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """[{"name":"quarterly-flow","description":"季報問答","required_role":"USER","enabled":true,"current_revision":3,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z","kind":"flow","extra_new_field":"kept"},{"name":"quarterly-agent","description":"代理技能","required_role":"USER","enabled":true,"current_revision":4,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-15T00:00:00Z","kind":"agentic"}]"""));

        var json = await Build(stub).ListAsync(AdminCtx);

        var items = json.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var flow = items.Single(item => item.GetProperty("name").GetString() == "quarterly-flow");
        var agent = items.Single(item => item.GetProperty("name").GetString() == "quarterly-agent");
        Assert.Equal("USER", flow.GetProperty("required_role").GetString());
        Assert.True(flow.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, flow.GetProperty("current_revision").GetInt32());
        Assert.Equal("2026-07-14T00:00:00Z", flow.GetProperty("updated_at").GetString());
        Assert.Equal("flow", flow.GetProperty("kind").GetString());
        Assert.Equal("agentic", agent.GetProperty("kind").GetString());
        // 穿透:backend 之後新增的欄位原樣保留。
        Assert.Equal("kept", flow.GetProperty("extra_new_field").GetString());

        // AT4-12:4 個 header 皆取自 UserContext(由已驗證 JWT claims 組成)。
        Assert.Equal("http://backend/api/skills", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
    }

    [Fact]
    public async Task Get_ForwardsNameInPath_PassesThroughFullSkill()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, SkillJson));

        var skill = await Build(stub).GetAsync("quarterly-qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly-qa", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("name: quarterly-qa", skill.GetProperty("definition").GetString());
        Assert.Equal("USER", skill.GetProperty("required_role").GetString());
        Assert.Equal(3, skill.GetProperty("current_revision").GetInt32());
        Assert.Equal("2026-07-13T00:00:00Z", skill.GetProperty("created_at").GetString());
    }

    [Fact]
    public async Task GetRevisions_ForwardsPath_PassesThroughSnakeCaseRows()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """[{"revision":2,"definition":"name: q","definition_sha256":"abc","created_by":"admin-a","created_at":"2026-07-14T00:00:00Z"}]"""));

        var json = await Build(stub).GetRevisionsAsync("quarterly-qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly-qa/revisions", stub.LastRequest!.RequestUri!.ToString());
        var row = json.EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("revision").GetInt32());
        Assert.Equal("abc", row.GetProperty("definition_sha256").GetString());
        Assert.Equal("admin-a", row.GetProperty("created_by").GetString());
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
    }

    [Fact]
    public async Task RestoreRevision_ForwardsPostPathAndIdentity_PassesThroughKind()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"name":"quarterly-qa","current_revision":4,"kind":"agentic"}"""));

        var result = await Build(stub).RestoreRevisionAsync("quarterly-qa", 2, AdminCtx);

        Assert.Equal(
            "http://backend/api/skills/quarterly-qa/revisions/2/restore",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
        Assert.Equal(4, result.GetProperty("current_revision").GetInt32());
        Assert.Equal("agentic", result.GetProperty("kind").GetString());
    }

    // 所有 JSON 端點(list/get/revisions/restore/create/update/import)共用同一顆 BackendErrorMapper,
    // 狀態碼 → 例外型別 + 訊息不改寫的完整決策表由 Create_BackendError_MapsToSameStatusException_KeepsMessage
    // 一次覆蓋;per-method 再驗一次同一張表不提供新資訊。

    [Fact] // body 只有 definition — name/description/required_role 都在 YAML 裡,不得另外送。
    public async Task Create_PostsDefinitionOnlyBody_MapsCreatedSkill()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, SkillJson));

        var created = await Build(stub).CreateAsync(Upsert(), AdminCtx);

        Assert.Equal("quarterly-qa", created.Name);
        Assert.Equal("http://backend/api/skills", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        // body 只有 definition 這一個欄位 — 不得再送 name/description/required_role(兩份事實來源)。
        using var doc = JsonDocument.Parse(stub.LastBody!);
        var property = Assert.Single(doc.RootElement.EnumerateObject().ToList());
        Assert.Equal("definition", property.Name);
        Assert.Equal(Yaml, property.Value.GetString());
    }

    [Theory]
    [InlineData("", "missing")]
    [InlineData(",\"kind\":\"unknown\"", "invalid")]
    public async Task Create_BackendKindSchemaDrift_ThrowsControlled502(string kindJson, string _)
    {
        var body =
            """{"name":"quarterly-qa","description":"季報問答","definition":"name: quarterly-qa","required_role":"USER","enabled":true,"current_revision":3,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z""" +
            kindJson + "}";
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, body));

        await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));
    }

    [Fact] // B3:選填 simpleForm 必須穿透強型別 DTO,原樣轉發給 backend(否則簡單模式表單狀態被吃掉)。
    public async Task Create_ForwardsSimpleForm_WhenPresent()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, SkillJson));
        var simpleForm = JsonSerializer.Deserialize<JsonElement>(
            """{"templateId":"template-stats","form":{"topK":"50"}}""");

        await Build(stub).CreateAsync(new SkillUpsert(Yaml, simpleForm), AdminCtx);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(Yaml, doc.RootElement.GetProperty("definition").GetString());
        var form = doc.RootElement.GetProperty("simpleForm");
        Assert.Equal("template-stats", form.GetProperty("templateId").GetString());
        Assert.Equal("50", form.GetProperty("form").GetProperty("topK").GetString());
    }

    [Fact]
    public async Task Update_PutsToNamedPath_SendsDefinition()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, SkillJson));

        await Build(stub).UpdateAsync("quarterly-qa", Upsert(), AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly-qa", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(Yaml, doc.RootElement.GetProperty("definition").GetString());
    }

    [Fact]
    public async Task Delete_SendsDelete_NoContentSucceeds()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Build(stub).DeleteAsync("quarterly-qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly-qa", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, stub.LastRequest!.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
    }

    // ---- 匯出:zip bytes 原封取回,不反序列化 ----

    private static HttpResponseMessage Zip(HttpStatusCode status, byte[] bytes, string? contentType = "application/zip")
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task Export_ForwardsGetToExportPath_ReturnsBytesUnchanged()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x7F, 0xFF };  // 含非文字位元組
        var stub = new StubHttpMessageHandler(_ => Zip(HttpStatusCode.OK, bytes));

        var export = await Build(stub).ExportAsync("quarterly-qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly-qa/export", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        Assert.Equal(bytes, export.Content);
        Assert.Equal("application/zip", export.ContentType);
        Assert.Equal("quarterly-qa.zip", export.FileName);
    }

    [Fact] // backend 沒帶 content-type → fallback application/zip。
    public async Task Export_BackendMissingContentType_FallsBackToZip()
    {
        var stub = new StubHttpMessageHandler(_ => Zip(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, contentType: null));

        var export = await Build(stub).ExportAsync("quarterly-qa", AdminCtx);

        Assert.Equal("application/zip", export.ContentType);
    }

    [Fact] // backend 404 → WorkflowNotFoundException(對外 404),不誤判成 zip。
    public async Task Export_Backend404_ThrowsNotFound()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.NotFound, "找不到 Skill：ghost"));

        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(stub).ExportAsync("ghost", AdminCtx));
        Assert.Equal("找不到 Skill：ghost", ex.Message);
    }

    [Fact] // 其餘非 2xx → 對外 502。
    public async Task Export_Backend500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).ExportAsync("quarterly-qa", AdminCtx));
        Assert.Contains("500", ex.Message);
    }

    // backend 的 400/403/404/409/422 原樣轉發 —— 例外型別對應同狀態碼,message 不改寫。
    [Theory]
    [InlineData(400, typeof(WorkflowBadInputException), "輸入驗證失敗")]
    [InlineData(403, typeof(WorkflowForbiddenException), "權限不足，無法存取 Skill")]
    [InlineData(404, typeof(WorkflowNotFoundException), "找不到 Skill：ghost")]
    [InlineData(409, typeof(DownstreamConflictException), "Skill 名稱已存在：quarterly-qa")]
    [InlineData(422, typeof(SkillValidationFailedException), "Skill 定義驗證失敗")]
    public async Task Create_BackendError_MapsToSameStatusException_KeepsMessage(
        int status, Type expected, string message)
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error((HttpStatusCode)status, message));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.IsType(expected, ex);
        Assert.Equal(message, ex.Message);
    }

    // AT4-02 的代理側:422 的引擎錯誤碼必須穿過代理層(否則編輯器指不出哪條規則、哪一行)。
    [Fact]
    public async Task Create_Backend422_KeepsEngineErrorCodes()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"timestamp":"2026-07-14T00:00:00Z","status":422,"message":"Skill 定義驗證失敗","fieldErrors":{"unbounded_loop":"loop 缺少 max_iterations（第 7 行）","unknown_node":"節點不存在"}}"""));

        var ex = await Assert.ThrowsAsync<SkillValidationFailedException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.Equal("Skill 定義驗證失敗", ex.Message);
        Assert.Equal("loop 缺少 max_iterations（第 7 行）", ex.FieldErrors!["unbounded_loop"]);
        Assert.Equal("節點不存在", ex.FieldErrors!["unknown_node"]);
    }

    // backend 400 的 fieldErrors 必須穿過代理層(否則前端永遠看不到欄位級錯誤)。
    [Fact]
    public async Task Create_Backend400WithFieldErrors_KeepsFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.BadRequest,
            """{"timestamp":"2026-07-14T00:00:00Z","status":400,"message":"輸入驗證失敗","fieldErrors":{"definition":"definition 不可為空"}}"""));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.Equal("輸入驗證失敗", ex.Message);
        Assert.Equal("definition 不可為空", ex.FieldErrors!["definition"]);
    }

    // 2xx 但沒有可用 body 的等價類:backend 回 JSON null(走 onEmptyBody)或整包沒有 body(解析失敗),
    // 兩者都必須收斂成受控 502(帶 FailurePrefix),不得被當成建立成功而回一個半空的 Skill。
    [Theory]
    [InlineData("null", "Skill 服務呼叫失敗：回應內容為空")]
    [InlineData("", "Skill 服務呼叫失敗：")]
    public async Task Create_Backend2xxWithoutUsableBody_ThrowsControlled502(string body, string expectedPrefix)
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, body));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.StartsWith(expectedPrefix, ex.Message);
    }

    [Fact] // backend 400 沒帶 fieldErrors → null(全域處理輸出空 map,ApiError 形狀不變)。
    public async Task Create_Backend400WithoutFieldErrors_HasNullFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.BadRequest,
            """{"timestamp":"2026-07-14T00:00:00Z","status":400,"message":"輸入驗證失敗"}"""));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.Null(ex.FieldErrors);
    }

    // ---- Agent Skill 匯入代理:multipart 原封串流轉送、錯誤穿透、kind 透傳 ----

    private const string PackageFileName = "sales-helper.zip";

    private static byte[] PackageBytes() => Encoding.UTF8.GetBytes("PKzip-bytes-payload");

    // 匯入成功:POST 到 /import、帶四個身分 header、以上傳位元組重建乾淨 multipart(帶 Content-Length、非 chunked)、
    // 內含名為 package 的檔位且位元組與上傳一致、回應含 kind。
    [Fact]
    public async Task Import_RebuildsMultipart_ForwardsHeadersAndPackagePart_PassesThroughKind()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"name":"sales-helper","description":"銷售助理","definition":"kind: agentic\n","required_role":"USER","enabled":true,"current_revision":1,"kind":"agentic","created_at":"2026-07-14T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}"""));
        var payload = PackageBytes();

        var result = await Build(stub).ImportAsync("sales-helper", payload, PackageFileName, AdminCtx);

        Assert.Equal("http://backend/api/skills/sales-helper/import", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        // 重建成 multipart/form-data(新 boundary,合法 HTTP)。
        Assert.Equal("multipart/form-data", stub.LastRequest!.Content!.Headers.ContentType!.MediaType);

        // 關鍵回歸:轉送給 backend 的請求必須帶 Content-Length(> 0)而非 chunked —
        // backend 的 multipart reader 會拒收無 Content-Length 的 chunked 請求。
        Assert.True(stub.LastRequest!.Content!.Headers.ContentLength > 0);

        // multipart body 內含名為 package 的檔位,檔名帶上,位元組與上傳逐字一致(非空)。
        Assert.Contains("name=package", stub.LastBody);
        Assert.Contains(PackageFileName, stub.LastBody);
        Assert.Contains(Encoding.UTF8.GetString(payload), stub.LastBody);

        // 回應原樣穿透:additive kind 帶上來,既有欄位不變。
        Assert.Equal("sales-helper", result.GetProperty("name").GetString());
        Assert.Equal("agentic", result.GetProperty("kind").GetString());
        Assert.Equal(1, result.GetProperty("current_revision").GetInt32());
    }

    [Fact]
    public async Task ImportDerived_ForwardsToAdditiveRoute_WithSameMultipartAndIdentity()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"name":"server-derived","kind":"agentic","current_revision":1}"""));
        var payload = PackageBytes();

        var result = await Build(stub).ImportAsync(
            payload, PackageFileName, AdminCtx);

        Assert.Equal("http://backend/api/skills/import", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
        Assert.Equal("multipart/form-data", stub.LastRequest.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(Encoding.UTF8.GetString(payload), stub.LastBody);
        Assert.Equal("server-derived", result.GetProperty("name").GetString());
    }

    // 上傳檔名為空白的等價類:multipart 檔位仍要帶合法檔名 —— fallback 到字面值 package.zip,
    // 不得送出空檔名(backend 的 multipart reader 靠檔位檔名判斷這是檔案而非一般欄位)。
    [Fact]
    public async Task Import_BlankFileName_FallsBackToPackageZip()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"name":"sales-helper","kind":"agentic","current_revision":1}"""));

        await Build(stub).ImportAsync("sales-helper", PackageBytes(), "", AdminCtx);

        Assert.Contains("name=package", stub.LastBody);
        Assert.Contains("filename=package.zip", stub.LastBody);
    }

    // backend 422(套件驗證失敗)→ SkillValidationFailedException,fieldErrors(引擎錯誤碼)原樣穿過代理層。
    [Fact]
    public async Task Import_Backend422_PassesThroughFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"timestamp":"2026-07-14T00:00:00Z","status":422,"message":"Skill 套件驗證失敗","fieldErrors":{"forbidden_script":"腳本未通過 AST 掃描","unknown_tool":"工具未註冊"}}"""));

        var ex = await Assert.ThrowsAsync<SkillValidationFailedException>(
            () => Build(stub).ImportAsync("sales-helper", PackageBytes(), PackageFileName, AdminCtx));

        Assert.Equal("Skill 套件驗證失敗", ex.Message);
        Assert.Equal("腳本未通過 AST 掃描", ex.FieldErrors!["forbidden_script"]);
        Assert.Equal("工具未註冊", ex.FieldErrors!["unknown_tool"]);
    }

    // 另一條匯入入口(衍生路由,不帶名稱)碰上非 2xx:與具名路由共用同一條錯誤映射 ——
    // 422 一樣是 SkillValidationFailedException,fieldErrors 同樣原樣穿透(錯誤處理不隨入口而異)。
    [Fact]
    public async Task ImportDerived_Backend422_PassesThroughFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"timestamp":"2026-07-14T00:00:00Z","status":422,"message":"Skill 套件驗證失敗","fieldErrors":{"missing_skill_md":"套件缺少 SKILL.md"}}"""));

        var ex = await Assert.ThrowsAsync<SkillValidationFailedException>(
            () => Build(stub).ImportAsync(PackageBytes(), PackageFileName, AdminCtx));

        Assert.Equal("http://backend/api/skills/import", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Skill 套件驗證失敗", ex.Message);
        Assert.Equal("套件缺少 SKILL.md", ex.FieldErrors!["missing_skill_md"]);
    }

    // 傳輸上限(16 MiB)的邊界:on-point(剛好上限)照常轉送;off-point(上限 +1)快速失敗(對外 400),不打 backend。
    [Fact]
    public async Task Import_AtSizeLimit_Forwards()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """{"name":"sales-helper","kind":"agentic","current_revision":1}"""));

        await Build(stub).ImportAsync("sales-helper", new byte[16 * 1024 * 1024], PackageFileName, AdminCtx);

        Assert.NotNull(stub.LastRequest); // 剛好等於上限 → 照常轉送
    }

    [Fact]
    public async Task Import_OverSizeLimit_RejectsBeforeForwarding()
    {
        var stub = new StubHttpMessageHandler(_ => throw new InvalidOperationException("不該打到 backend"));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(
            () => Build(stub).ImportAsync("sales-helper", new byte[16 * 1024 * 1024 + 1], PackageFileName, AdminCtx));

        Assert.Contains("超過上限", ex.Message);
        Assert.Null(stub.LastRequest); // 上限檢查在轉送之前 → backend 從沒被呼叫
    }

    [Fact]
    public async Task Get_500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).GetAsync("quarterly-qa", AdminCtx));
        Assert.Contains("500", ex.Message);
    }

    [Fact] // 「沒有回應」的等價類:傳輸失敗 → 502(不是把它誤判成使用者的定義有問題)。
    public async Task List_TransportFailure_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒"));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() => Build(stub).ListAsync(AdminCtx));
        Assert.Contains("Skill 服務呼叫失敗", ex.Message);
    }
}
