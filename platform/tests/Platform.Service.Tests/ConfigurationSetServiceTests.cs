using System.Net;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

/// <summary>
/// ConfigurationSetService(代理 backend /api/configuration-sets)。SSR-P4-011 的服務側,驗兩件事:
/// (a) 六個公開端點每個請求都帶 X-Internal-Token + 三個身分 header(取自已驗證的 JWT claims),
///     且 method/path/body 原樣穿透;
/// (b) backend 的狀態碼與 ApiError 原樣轉發,400/422 的 fieldErrors 不得被吞掉。
/// 另驗:沒有任何「讀取 active set」的方法 —— 介面根本不提供,故無捷徑可打(SSR-P4-011)。
/// </summary>
public sealed class ConfigurationSetServiceTests
{
    private static readonly UserContext AdminCtx = new("admin-a", "demo-a", "ADMIN");

    private const string SetId = "11111111-1111-1111-1111-111111111111";

    // 單筆(含 values):values 是 jsonb 物件,數字保持數字。
    private const string SetJson =
        """{"id":"11111111-1111-1111-1111-111111111111","name":"prod","is_active":true,"values":{"retrieval.top_k":8,"llm.model":"gpt-4o-mini"},"created_by":"admin-a","created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}""";

    private static ConfigurationSetService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    private static ConfigurationSetUpsert Upsert() => new("prod", new Dictionary<string, JsonElement>
    {
        ["retrieval.top_k"] = JsonSerializer.SerializeToElement(8),
        ["llm.model"] = JsonSerializer.SerializeToElement("gpt-4o-mini"),
    });

    private static void AssertIdentityHeaders(StubHttpMessageHandler stub)
    {
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
    }

    // A1:List 原樣穿透 backend JSON(snake_case)。清單省略 values;backend 的 created_at 不再被舊 DTO 丟掉。
    [Fact]
    public async Task List_PassesThroughSnakeCase_KeepsCreatedAt_OmitsValues_ForwardsIdentityHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            """[{"id":"11111111-1111-1111-1111-111111111111","name":"prod","is_active":true,"created_at":"2026-07-13T00:00:00Z","updated_at":"2026-07-14T00:00:00Z"}]"""));

        var json = await Build(stub).ListAsync(AdminCtx);

        var item = json.EnumerateArray().Single();
        Assert.Equal(SetId, item.GetProperty("id").GetString());
        Assert.Equal("prod", item.GetProperty("name").GetString());
        Assert.True(item.GetProperty("is_active").GetBoolean());
        Assert.Equal("2026-07-14T00:00:00Z", item.GetProperty("updated_at").GetString());
        // 穿透修正:舊 ConfigurationSetInfo DTO 會丟掉 created_at,穿透後補回。
        Assert.Equal("2026-07-13T00:00:00Z", item.GetProperty("created_at").GetString());
        // 清單仍省略 values(backend 清單本就不含)。
        Assert.False(item.TryGetProperty("values", out _));

        Assert.Equal("http://backend/api/configuration-sets", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        AssertIdentityHeaders(stub);
    }

    [Fact]
    public async Task Get_ForwardsIdInPath_PassesThroughValues()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, SetJson));

        var set = await Build(stub).GetAsync(SetId, AdminCtx);

        Assert.Equal($"http://backend/api/configuration-sets/{SetId}", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("prod", set.GetProperty("name").GetString());
        Assert.True(set.GetProperty("is_active").GetBoolean());
        Assert.Equal("admin-a", set.GetProperty("created_by").GetString());
        // values jsonb 原封保留:數字仍是數字,字串仍是字串。
        Assert.Equal(8, set.GetProperty("values").GetProperty("retrieval.top_k").GetInt32());
        Assert.Equal("gpt-4o-mini", set.GetProperty("values").GetProperty("llm.model").GetString());
        AssertIdentityHeaders(stub);
    }

    [Fact] // body 只有 name + values —— is_active 不得經 upsert(啟用走 activate 端點)。
    public async Task Create_PostsNameAndValuesOnly_NoIsActive_MapsCreated()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, SetJson));

        var created = await Build(stub).CreateAsync(Upsert(), AdminCtx);

        Assert.Equal("prod", created.Name);
        Assert.Equal("http://backend/api/configuration-sets", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        AssertIdentityHeaders(stub);

        using var doc = JsonDocument.Parse(stub.LastBody!);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "name", "values" }, names);
        Assert.Equal("prod", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(8, doc.RootElement.GetProperty("values").GetProperty("retrieval.top_k").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("is_active", out _));
    }

    [Fact]
    public async Task Update_PutsToIdPath_SendsBody()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, SetJson));

        await Build(stub).UpdateAsync(SetId, Upsert(), AdminCtx);

        Assert.Equal($"http://backend/api/configuration-sets/{SetId}", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);
        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("prod", doc.RootElement.GetProperty("name").GetString());
        AssertIdentityHeaders(stub);
    }

    [Fact]
    public async Task Delete_SendsDelete_NoContentSucceeds()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Build(stub).DeleteAsync(SetId, AdminCtx);

        Assert.Equal($"http://backend/api/configuration-sets/{SetId}", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, stub.LastRequest!.Method);
        AssertIdentityHeaders(stub);
    }

    [Fact] // activate 是 POST {id}/activate —— 專屬子路徑,非 upsert。
    public async Task Activate_PostsToActivatePath_MapsSet()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK, SetJson));

        var set = await Build(stub).ActivateAsync(SetId, AdminCtx);

        Assert.Equal($"http://backend/api/configuration-sets/{SetId}/activate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.True(set.IsActive);
        AssertIdentityHeaders(stub);
    }

    // backend 的 400/403/404/409/422 原樣轉發 —— 例外型別對應同狀態碼,message 不改寫。
    [Theory]
    [InlineData(400, typeof(WorkflowBadInputException), "輸入驗證失敗")]
    [InlineData(403, typeof(WorkflowForbiddenException), "權限不足，無法存取 Configuration Set")]
    [InlineData(404, typeof(WorkflowNotFoundException), "找不到 Configuration Set")]
    [InlineData(409, typeof(DownstreamConflictException), "Configuration Set 名稱已存在：prod")]
    [InlineData(422, typeof(SkillValidationFailedException), "Configuration Set 驗證失敗")]
    public async Task Create_BackendError_MapsToSameStatusException_KeepsMessage(
        int status, Type expected, string message)
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error((HttpStatusCode)status, message));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.IsType(expected, ex);
        Assert.Equal(message, ex.Message);
    }

    [Fact] // values 越界 → backend 422 + fieldErrors,錯誤碼必須穿過代理層(前端才指得出哪個鍵越界)。
    public async Task Create_Backend422_KeepsValueRangeFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json((HttpStatusCode)422,
            """{"timestamp":"2026-07-14T00:00:00Z","status":422,"message":"Configuration Set 驗證失敗","fieldErrors":{"retrieval.top_k":"必須介於 1 到 50","llm.model":"不在允許清單"}}"""));

        var ex = await Assert.ThrowsAsync<SkillValidationFailedException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));

        Assert.Equal("Configuration Set 驗證失敗", ex.Message);
        Assert.Equal("必須介於 1 到 50", ex.FieldErrors!["retrieval.top_k"]);
        Assert.Equal("不在允許清單", ex.FieldErrors!["llm.model"]);
    }

    [Fact] // backend 400 的 fieldErrors 必須穿過代理層。
    public async Task Update_Backend400WithFieldErrors_KeepsFieldErrors()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.BadRequest,
            """{"timestamp":"2026-07-14T00:00:00Z","status":400,"message":"輸入驗證失敗","fieldErrors":{"name":"name 不可為空"}}"""));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(
            () => Build(stub).UpdateAsync(SetId, Upsert(), AdminCtx));

        Assert.Equal("輸入驗證失敗", ex.Message);
        Assert.Equal("name 不可為空", ex.FieldErrors!["name"]);
    }

    [Fact] // 跨租戶 GET {id} → backend 404(租戶隔離),原樣轉發。
    public async Task Get_Backend404_ThrowsNotFound()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.NotFound, "找不到 Configuration Set"));

        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => Build(stub).GetAsync(SetId, AdminCtx));
        Assert.Equal("找不到 Configuration Set", ex.Message);
    }

    [Fact] // List 走 SendForJsonElementAsync,錯誤映射與 Get/Create 是不同呼叫路徑 —— 非 ADMIN → backend 403 原樣轉發。
    public async Task List_Backend403_ThrowsForbidden()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足，需要管理員權限"));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() => Build(stub).ListAsync(AdminCtx));
        Assert.Equal("權限不足，需要管理員權限", ex.Message);
    }

    [Fact] // Delete 走 SendExpectSuccessAsync(不讀 body)—— 仍須接上同一套錯誤映射,不可把 404 當成功吞掉。
    public async Task Delete_Backend404_ThrowsNotFound()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.NotFound, $"找不到 Configuration Set：{SetId}"));

        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(() => Build(stub).DeleteAsync(SetId, AdminCtx));
        Assert.Equal($"找不到 Configuration Set：{SetId}", ex.Message);
    }

    [Fact] // 非 2xx 且非映射狀態(5xx)→ 對外 502。
    public async Task Get_500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).GetAsync(SetId, AdminCtx));
        Assert.Contains("500", ex.Message);
    }

    [Fact] // 狀態碼成功但 body 反序列化成 null 的等價類 → 502「回應內容為空」,不得 NPE 或回 null set。
    public async Task Create_SuccessWithNullBody_ThrowsEmptyResponse()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, "null"));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).CreateAsync(Upsert(), AdminCtx));
        Assert.Equal("Configuration Set 服務呼叫失敗：回應內容為空", ex.Message);
    }

    [Fact] // 「沒有回應」的等價類:傳輸失敗 → 502(不誤判成使用者設定有問題)。
    public async Task Activate_TransportFailure_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("連線被拒"));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).ActivateAsync(SetId, AdminCtx));
        Assert.Contains("Configuration Set 服務呼叫失敗", ex.Message);
    }
}
