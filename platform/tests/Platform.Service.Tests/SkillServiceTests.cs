using System.Net;
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

    private const string Yaml = "name: quarterly_qa\ndescription: 季報問答\nflow:\n  - node: query_intake\n";

    private const string SkillJson =
        """{"name":"quarterly_qa","description":"季報問答","definition":"name: quarterly_qa","required_role":"USER","enabled":true,"current_revision":3,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}""";

    private static SkillService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    private static SkillUpsert Upsert() => new(Yaml);

    // A1:List 原樣穿透 backend JSON(snake_case),backend 新增欄位不被 DTO 靜默吃掉。
    [Fact]
    public async Task List_PassesThroughSnakeCase_ForwardsIdentityHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """[{"name":"quarterly_qa","description":"季報問答","required_role":"USER","enabled":true,"current_revision":3,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z","extra_new_field":"kept"}]"""));

        var json = await Build(stub).ListAsync(AdminCtx);

        var item = json.EnumerateArray().Single();
        Assert.Equal("quarterly_qa", item.GetProperty("name").GetString());
        Assert.Equal("USER", item.GetProperty("required_role").GetString());
        Assert.True(item.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, item.GetProperty("current_revision").GetInt32());
        Assert.Equal("2026-07-14T00:00:00Z", item.GetProperty("updated_at").GetString());
        // 穿透:backend 之後新增的欄位原樣保留。
        Assert.Equal("kept", item.GetProperty("extra_new_field").GetString());

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

        var skill = await Build(stub).GetAsync("quarterly_qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly_qa", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("name: quarterly_qa", skill.GetProperty("definition").GetString());
        Assert.Equal("USER", skill.GetProperty("required_role").GetString());
        Assert.Equal(3, skill.GetProperty("current_revision").GetInt32());
        Assert.Equal("2026-07-13T00:00:00Z", skill.GetProperty("created_at").GetString());
    }

    [Fact]
    public async Task GetRevisions_ForwardsPath_PassesThroughSnakeCaseRows()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """[{"revision":2,"definition":"name: q","definition_sha256":"abc","created_by":"admin-a","created_at":"2026-07-14T00:00:00Z"}]"""));

        var json = await Build(stub).GetRevisionsAsync("quarterly_qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly_qa/revisions", stub.LastRequest!.RequestUri!.ToString());
        var row = json.EnumerateArray().Single();
        Assert.Equal(2, row.GetProperty("revision").GetInt32());
        Assert.Equal("abc", row.GetProperty("definition_sha256").GetString());
        Assert.Equal("admin-a", row.GetProperty("created_by").GetString());
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
    }

    [Fact]
    public async Task GetRevisions_404_ThrowsNotFound()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.NotFound, "找不到 Skill：ghost"));

        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(stub).GetRevisionsAsync("ghost", AdminCtx));
        Assert.Equal("找不到 Skill：ghost", ex.Message);
    }

    [Fact] // body 只有 definition — name/description/required_role 都在 YAML 裡,不得另外送。
    public async Task Create_PostsDefinitionOnlyBody_MapsCreatedSkill()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, SkillJson));

        var created = await Build(stub).CreateAsync(Upsert(), AdminCtx);

        Assert.Equal("quarterly_qa", created.Name);
        Assert.Equal("http://backend/api/skills", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        // body 只有 definition 這一個欄位 — 不得再送 name/description/required_role(兩份事實來源)。
        using var doc = JsonDocument.Parse(stub.LastBody!);
        var property = Assert.Single(doc.RootElement.EnumerateObject().ToList());
        Assert.Equal("definition", property.Name);
        Assert.Equal(Yaml, property.Value.GetString());
    }

    [Fact]
    public async Task Update_PutsToNamedPath_SendsDefinition()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, SkillJson));

        await Build(stub).UpdateAsync("quarterly_qa", Upsert(), AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly_qa", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(Yaml, doc.RootElement.GetProperty("definition").GetString());
    }

    [Fact]
    public async Task Delete_SendsDelete_NoContentSucceeds()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Build(stub).DeleteAsync("quarterly_qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly_qa", stub.LastRequest!.RequestUri!.ToString());
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

        var export = await Build(stub).ExportAsync("quarterly_qa", AdminCtx);

        Assert.Equal("http://backend/api/skills/quarterly_qa/export", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        Assert.Equal(bytes, export.Content);
        Assert.Equal("application/zip", export.ContentType);
        Assert.Equal("quarterly_qa.zip", export.FileName);
    }

    [Fact] // backend 沒帶 content-type → fallback application/zip。
    public async Task Export_BackendMissingContentType_FallsBackToZip()
    {
        var stub = new StubHttpMessageHandler(_ => Zip(HttpStatusCode.OK, new byte[] { 1, 2, 3 }, contentType: null));

        var export = await Build(stub).ExportAsync("quarterly_qa", AdminCtx);

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
            () => Build(stub).ExportAsync("quarterly_qa", AdminCtx));
        Assert.Contains("500", ex.Message);
    }

    // backend 的 400/403/404/409/422 原樣轉發 —— 例外型別對應同狀態碼,message 不改寫。
    [Theory]
    [InlineData(400, typeof(WorkflowBadInputException), "輸入驗證失敗")]
    [InlineData(403, typeof(WorkflowForbiddenException), "權限不足，無法存取 Skill")]
    [InlineData(404, typeof(WorkflowNotFoundException), "找不到 Skill：ghost")]
    [InlineData(409, typeof(DownstreamConflictException), "Skill 名稱已存在：quarterly_qa")]
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

    [Fact] // PUT 的 name 不符也是 422(backend 判斷);訊息與 fieldErrors 一樣要穿過來。
    public async Task Update_Backend422_NameMismatch_KeepsFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"timestamp":"2026-07-14T00:00:00Z","status":422,"message":"Skill 定義的 name 與路由不符：定義為 b，路由為 a","fieldErrors":{"name":"定義的 name（b）必須與路由的 name（a）相同"}}"""));

        var ex = await Assert.ThrowsAsync<SkillValidationFailedException>(
            () => Build(stub).UpdateAsync("a", Upsert(), AdminCtx));

        Assert.Equal("Skill 定義的 name 與路由不符：定義為 b，路由為 a", ex.Message);
        Assert.NotNull(ex.FieldErrors!["name"]);
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

    [Fact] // backend 400 沒帶 fieldErrors → null(全域處理輸出空 map,ApiError 形狀不變)。
    public async Task Create_Backend400WithoutFieldErrors_HasNullFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.BadRequest,
            """{"timestamp":"2026-07-14T00:00:00Z","status":400,"message":"輸入驗證失敗"}"""));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.Null(ex.FieldErrors);
    }

    [Fact]
    public async Task Get_500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).GetAsync("quarterly_qa", AdminCtx));
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
