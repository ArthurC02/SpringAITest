using System.Net;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class DocumentServiceTests
{
    private static readonly UserContext Ctx = new("alice", "demo-a", "USER");

    private static DocumentService Build(StubHttpMessageHandler stub, FakeDocumentQueue? queue = null)
        => new(TestBackend.Client(stub), queue ?? new FakeDocumentQueue());

    [Fact]
    public async Task Create_PublishesMessage_ReturnsProcessing()
    {
        // 建立不再打 backend HTTP;stub 若被呼叫即為錯誤(回 500 讓測試容易發現)。
        var queue = new FakeDocumentQueue();
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)), queue);

        var accepted = await svc.CreateAsync(new DocumentCreateRequest("標題", "內容"), Ctx);

        Assert.Equal("processing", accepted.Status);
        Assert.Equal("標題", accepted.Title);
        Assert.False(string.IsNullOrWhiteSpace(accepted.Id));
        Assert.True(Guid.TryParse(accepted.Id, out _));

        // 訊息內容:documentId 與回傳 id 一致;身分/內容原樣帶入。
        var msg = queue.Last!;
        Assert.Equal(accepted.Id, msg.DocumentId);
        Assert.Equal("demo-a", msg.TenantId);
        Assert.Equal("alice", msg.UserId);
        Assert.Equal("標題", msg.Title);
        Assert.Equal("內容", msg.Text);
    }

    [Fact]
    public async Task Create_PublishFails_ThrowsWorkflowInvocation_QueuePrefix()
    {
        var queue = new FakeDocumentQueue { ThrowOnPublish = true };
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), queue);

        var ex = await Assert.ThrowsAsync<WorkflowInvocationException>(() =>
            svc.CreateAsync(new DocumentCreateRequest("標題", "內容"), Ctx));
        Assert.StartsWith("文件佇列服務呼叫失敗：", ex.Message);
    }

    // A1:List 原樣穿透 backend JSON(snake_case),backend 新增欄位不被 DTO 靜默吃掉。
    [Fact]
    public async Task List_PassesThroughBackendJson_IncludingUnknownFields()
    {
        var stub = new StubHttpMessageHandler(_ => TestHttp.Json(HttpStatusCode.OK,
            "[{\"id\":\"d1\",\"title\":\"T\",\"chunk_count\":2,\"created_at\":\"2026-07-11T00:00:00Z\",\"status\":\"ready\",\"extra_new_field\":\"kept\"}]"));
        var svc = Build(stub);

        var json = await svc.ListAsync(Ctx);

        var item = json.EnumerateArray().Single();
        Assert.Equal("d1", item.GetProperty("id").GetString());
        Assert.Equal(2, item.GetProperty("chunk_count").GetInt32());
        Assert.Equal("2026-07-11T00:00:00Z", item.GetProperty("created_at").GetString());
        Assert.Equal("ready", item.GetProperty("status").GetString());
        // 穿透:backend 之後新增的欄位原樣保留(舊 DocumentInfo round-trip 會丟掉)。
        Assert.Equal("kept", item.GetProperty("extra_new_field").GetString());
        Assert.Equal("http://backend/api/documents", stub.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Delete_Succeeds_OnNoContent()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var svc = Build(stub);

        await svc.DeleteAsync("d1", Ctx);

        Assert.Equal("http://backend/api/documents/d1", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, stub.LastRequest!.Method);
    }

    [Fact]
    public async Task Delete_404_ThrowsDocumentNotFound()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var ex = await Assert.ThrowsAsync<DocumentNotFoundException>(() => svc.DeleteAsync("d1", Ctx));
        Assert.Equal("找不到文件：d1", ex.Message);
    }

    // backend 非 404 的可映射 4xx 走共用映射(不是被文件專屬的 404 訊息蓋掉,也不是被壓成 502)。
    [Fact]
    public async Task Delete_Backend403_ThrowsWorkflowForbidden()
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error(HttpStatusCode.Forbidden, "權限不足")));

        var ex = await Assert.ThrowsAsync<WorkflowForbiddenException>(() => svc.DeleteAsync("d1", Ctx));
        Assert.Equal("權限不足", ex.Message);
    }

    // B2:List 改走 BackendErrorMapper —— backend 4xx 不再被一律壓成 502,各自對應同狀態碼且 message 不改寫。
    // 404 在此是 WorkflowNotFoundException(文件專屬的 DocumentNotFoundException 只在 delete 分支)。
    [Theory]
    [InlineData(400, typeof(WorkflowBadInputException), "輸入驗證失敗")]
    [InlineData(403, typeof(WorkflowForbiddenException), "權限不足")]
    [InlineData(404, typeof(WorkflowNotFoundException), "找不到文件")]
    [InlineData(409, typeof(DownstreamConflictException), "文件狀態衝突")]
    [InlineData(422, typeof(SkillValidationFailedException), "文件內容驗證失敗")]
    public async Task List_BackendError_MapsToSameStatusException_KeepsMessage(
        int status, Type expected, string message)
    {
        var svc = Build(new StubHttpMessageHandler(_ => TestHttp.Error((HttpStatusCode)status, message)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => svc.ListAsync(Ctx));

        Assert.IsType(expected, ex);
        Assert.Equal(message, ex.Message);
    }

    // 非預期狀態(5xx)統一包成 502(backend 是上游)。
    [Fact]
    public async Task List_Backend500_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.ListAsync(Ctx));
    }

    // Delete 的「非 404」分支也走共用映射(不是被吞成 502 就是各自對應)——5xx → 502。
    [Fact]
    public async Task Delete_Backend500_ThrowsWorkflowInvocation()
    {
        var svc = Build(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<WorkflowInvocationException>(() => svc.DeleteAsync("d1", Ctx));
    }
}
