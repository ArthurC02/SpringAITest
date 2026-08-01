using System.Net;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class BusinessWorkflowServiceTests
{
    private static readonly UserContext Admin = new("admin-a", "demo-a", "ADMIN");

    private static BusinessWorkflowService Build(StubHttpMessageHandler stub)
        => new(TestBackend.Client(stub));

    // 讀取端點原樣穿透 backend JSON(snake_case + 未來新增欄位),同時帶齊 X-Internal-Token 與三個身分 header。
    [Fact]
    public async Task List_PassesThroughSnakeCase_ForwardsIdentityHeaders()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK,
            """[{"name":"quarterly-flow","description":"flow","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","kind":"flow","extra_new_field":"kept"}]"""));

        var json = await Build(stub).ListAsync(Admin);

        Assert.Equal("http://backend/api/business-workflows", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        var row = json.EnumerateArray().Single();
        Assert.Equal("flow", row.GetProperty("kind").GetString());
        Assert.Equal(1, row.GetProperty("current_revision").GetInt32());
        Assert.Equal("kept", row.GetProperty("extra_new_field").GetString());
    }

    [Fact]
    public async Task Get_ForwardsNameInPath_PassesThroughFullWorkflow()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK,
            """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":2,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","kind":"flow"}"""));

        var workflow = await Build(stub).GetAsync("quarterly-flow", Admin);

        Assert.Equal(
            "http://backend/api/business-workflows/quarterly-flow",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("name: quarterly-flow", workflow.GetProperty("definition").GetString());
        Assert.Equal(2, workflow.GetProperty("current_revision").GetInt32());
    }

    [Fact]
    public async Task Delete_SendsDeleteToNamedPath_NoContentSucceeds()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Build(stub).DeleteAsync("quarterly-flow", Admin);

        Assert.Equal(
            "http://backend/api/business-workflows/quarterly-flow",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, stub.LastRequest!.Method);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
    }

    private static HttpResponseMessage Zip(byte[] bytes, string? contentType)
    {
        var content = new ByteArrayContent(bytes);
        if (contentType is not null)
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // 匯出是本類別自己實作的一條路徑(不與 SkillService 共用):bytes 原封取回;
    // content-type 有帶就沿用,沒帶才 fallback application/zip。
    [Theory]
    [InlineData("application/octet-stream", "application/octet-stream")]
    [InlineData(null, "application/zip")]
    public async Task Export_ReturnsBytesUnchanged_FallsBackToZipContentType(
        string? contentType, string expectedContentType)
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x7F, 0xFF };  // 含非文字位元組
        var stub = new StubHttpMessageHandler(_ => Zip(bytes, contentType));

        var export = await Build(stub).ExportAsync("quarterly-flow", Admin);

        Assert.Equal(
            "http://backend/api/business-workflows/quarterly-flow/export",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, stub.LastRequest!.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal(bytes, export.Content);
        Assert.Equal(expectedContentType, export.ContentType);
        Assert.Equal("quarterly-flow.zip", export.FileName);
    }

    // 匯出的錯誤半邊:非 2xx 不得被誤判成 zip;5xx 收斂成對外 502,訊息用 Business Workflow 自己的前綴。
    [Fact]
    public async Task Export_Backend500_ThrowsWorkflowInvocation()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(
            () => Build(stub).ExportAsync("quarterly-flow", Admin));

        Assert.Equal("Business Workflow 服務呼叫失敗：HTTP 500", ex.Message);
    }

    [Fact]
    public async Task Create_PreservesLocationKindAndSimpleForm()
    {
        var stub = new StubHttpMessageHandler(_ =>
        {
            var response = TestHttp.Json(
                HttpStatusCode.Created,
                """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","kind":"flow","simpleForm":{"templateId":"template-stats","form":{"topK":"5"}}}""");
            response.Headers.Location = new Uri("/api/business-workflows/quarterly-flow", UriKind.Relative);
            return response;
        });
        var requestForm = JsonSerializer.SerializeToElement(
            new { templateId = "template-stats", form = new { topK = "5" } });

        var result = await Build(stub).CreateAsync(
            new SkillUpsert("name: quarterly-flow", requestForm), Admin);

        Assert.Equal("http://backend/api/business-workflows", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("/api/business-workflows/quarterly-flow", result.Location);
        Assert.Equal("flow", result.Workflow.Kind);
        Assert.Equal(
            "template-stats",
            result.Workflow.SimpleForm!.Value.GetProperty("templateId").GetString());
        using var sent = JsonDocument.Parse(stub.LastBody!);
        Assert.Equal(
            "template-stats",
            sent.RootElement.GetProperty("simpleForm").GetProperty("templateId").GetString());
    }

    // Location 是可空的:backend 沒回 header 時 Location 為 null,建立本身仍成功(不得因缺 header 炸掉)。
    [Fact]
    public async Task Create_WithoutLocationHeader_YieldsNullLocation()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.Created,
            """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z","kind":"flow"}"""));

        var result = await Build(stub).CreateAsync(
            new SkillUpsert("name: quarterly-flow"), Admin);

        Assert.Null(result.Location);
        Assert.Equal("quarterly-flow", result.Workflow.Name);
    }

    [Fact]
    public async Task Update_PreservesKindAndSimpleForm()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(
            HttpStatusCode.OK,
            """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":2,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-02T00:00:00Z","kind":"flow","simpleForm":{"templateId":"template-compare"}}"""));

        var result = await Build(stub).UpdateAsync(
            "quarterly-flow", new SkillUpsert("name: quarterly-flow"), Admin);

        Assert.Equal("flow", result.Kind);
        Assert.Equal(
            "template-compare",
            result.SimpleForm!.Value.GetProperty("templateId").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"kind\":\"agentic\"")]
    [InlineData(",\"kind\":\"unknown\"")]
    public async Task Create_MissingOrNonFlowKind_ThrowsControlled502(string kindJson)
    {
        var body =
            """{"name":"quarterly-flow","description":"flow","definition":"name: quarterly-flow","required_role":"USER","enabled":true,"current_revision":1,"created_at":"2026-01-01T00:00:00Z","updated_at":"2026-01-01T00:00:00Z""" +
            kindJson + "}";
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.Created, body));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => Build(stub).CreateAsync(
            new SkillUpsert("name: quarterly-flow"), Admin));
    }
}
