using System.Net;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class ConfigServiceTests
{
    private static readonly UserContext AdminCtx = new("admin-a", "demo-a", "ADMIN");
    private static readonly UserContext UserCtx = new("user-a", "demo-a", "USER");

    private static ConfigService Build(StubHttpMessageHandler stub) => new(TestBackend.Client(stub));

    [Fact]
    public async Task List_MapsResponse_ForwardsHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "[{\"key\":\"a\",\"value\":\"1\",\"updatedAt\":\"2026-07-12T00:00:00Z\"}]"));
        var svc = Build(stub);

        var list = await svc.ListAsync(UserCtx);

        var item = Assert.Single(list);
        Assert.Equal("a", item.Key);
        Assert.Equal("1", item.Value);
        Assert.Equal("http://backend/api/config", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("USER", stub.Header("X-User-Role"));
        // app_config 是租戶隔離的:backend 的 RequireTenant() 缺這個 header 就 400。
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
    }

    [Fact]
    public async Task Update_Succeeds_SendsValue_ForwardsAdminRole_MapsResult()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "{\"key\":\"a\",\"value\":\"2\",\"updatedAt\":\"2026-07-12T00:00:00Z\"}"));
        var svc = Build(stub);

        var updated = await svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx);

        Assert.Equal("a", updated.Key);
        Assert.Equal("2", updated.Value);

        Assert.Equal("http://backend/api/config/a", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, stub.LastRequest!.Method);
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));

        using var doc = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal("2", doc.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Update_403_ThrowsWorkflowForbidden_WithBackendMessage()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足，無法修改系統組態")));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), UserCtx));
        Assert.Equal("權限不足，無法修改系統組態", ex.Message);
    }

    // BackendErrorMapper 其餘四個分支(403 見上、5xx 見下):各自對外同狀態碼,message 不改寫,
    // 不得被一律壓成 502。注意 404 在 Update/List 是例外,在 GetRuntime 才是 null(見下方)。
    [Theory]
    [InlineData(400, typeof(WorkflowBadInputException), "缺少租戶識別標頭：X-Tenant-Id")]
    [InlineData(404, typeof(WorkflowNotFoundException), "找不到組態鍵：a")]
    [InlineData(409, typeof(DownstreamConflictException), "組態已被其他人修改")]
    [InlineData(422, typeof(SkillValidationFailedException), "組態值不合法")]
    public async Task Update_BackendError_MapsToSameStatusException_KeepsMessage(
        int status, Type expected, string message)
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error((HttpStatusCode)status, message)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx));

        Assert.IsType(expected, ex);
        Assert.Equal(message, ex.Message);
    }

    // backend 400 的 fieldErrors 必須穿過代理層 —— 被吞成空 map 的話,前端只剩一句籠統訊息,指不出哪個欄位。
    [Fact]
    public async Task Update_Backend400WithFieldErrors_KeepsFieldErrors()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.BadRequest,
            "{\"timestamp\":\"2026-07-12T00:00:00Z\",\"status\":400,\"message\":\"輸入驗證失敗\","
            + "\"fieldErrors\":{\"value\":\"value 不可為空\"}}")));

        var ex = await Assert.ThrowsAsync<WorkflowBadInputException>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx));

        Assert.Equal("輸入驗證失敗", ex.Message);
        Assert.Equal("value 不可為空", ex.FieldErrors!["value"]);
    }

    [Fact]
    public async Task Update_500_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx));
    }

    // 「沒有回應」與「回了 500」是兩條不同的程式路徑(WrapTransport vs MapErrorAsync),對外同為 502。
    [Fact]
    public async Task Update_TransportFailure_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("backend 不可達")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            svc.UpdateAsync("a", new ConfigUpdateRequest("2"), AdminCtx));
    }

    // B2:List 改走 BackendErrorMapper —— backend 4xx 不再被一律壓成 502,403 對外仍是 403。
    [Fact]
    public async Task List_Backend403_ThrowsWorkflowForbidden()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足")));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() => svc.ListAsync(UserCtx));
        Assert.Equal("權限不足", ex.Message);
    }

    // ---- 中2:GetRuntimeAsync(/api/config/runtime/{key},不帶角色) ----

    [Fact]
    public async Task GetRuntime_Found_ReturnsItem_SendsEmptyRole_NoAdminSynthesis()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK, "{\"key\":\"prompt.manifest_revision\",\"value\":\"7\",\"updatedAt\":\"2026-07-12T00:00:00Z\"}"));
        var svc = Build(stub);

        var item = await svc.GetRuntimeAsync("prompt.manifest_revision", new UserContext(string.Empty, "demo-a", string.Empty));

        Assert.NotNull(item);
        Assert.Equal("7", item!.Value);
        Assert.Equal(
            "http://backend/api/config/runtime/prompt.manifest_revision", stub.LastRequest!.RequestUri!.ToString());
        // 執行期讀取不合成 ADMIN 角色:空角色才代表這不是「讀者的授權」而是伺服器端執行期讀取。
        Assert.Equal(string.Empty, stub.Header("X-User-Role"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
    }

    // backend allowlist 外的 key、或未設定值,一律 404 → 本方法回 null(不是例外)。
    [Fact]
    public async Task GetRuntime_404_ReturnsNull()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.NotFound, "找不到")));

        Assert.Null(await svc.GetRuntimeAsync("prompt.manifest_revision", UserCtx));
    }

    [Fact]
    public async Task GetRuntime_403_ThrowsWorkflowForbidden()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足")));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(
            () => svc.GetRuntimeAsync("prompt.manifest_revision", UserCtx));
        Assert.Equal("權限不足", ex.Message);
    }

    // 「沒有回應」與「404」是兩條不同的程式路徑(WrapTransport vs 直接判 StatusCode),對外分別是 502 例外與 null。
    [Fact]
    public async Task GetRuntime_TransportFailure_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => throw new HttpRequestException("backend 不可達")));

        await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => svc.GetRuntimeAsync("prompt.manifest_revision", UserCtx));
    }
}
