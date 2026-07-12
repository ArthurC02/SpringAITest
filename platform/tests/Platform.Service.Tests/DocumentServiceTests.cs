using System.Net;
using System.Text;
using Platform.Service;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service.Tests;

public sealed class DocumentServiceTests
{
    private static readonly UserContext Ctx = new("alice", "demo-a", "USER");

    private static DocumentService Build(StubHttpMessageHandler stub, FakeDocumentQueue? queue = null)
        => new(TestBackend.Client(stub), queue ?? new FakeDocumentQueue());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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

    [Fact]
    public async Task List_MapsResponse()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            "[{\"id\":\"d1\",\"title\":\"T\",\"chunk_count\":2,\"created_at\":\"2026-07-11T00:00:00Z\",\"status\":\"ready\"}]"));
        var svc = Build(stub);

        var list = await svc.ListAsync(Ctx);

        var item = Assert.Single(list);
        Assert.Equal("d1", item.Id);
        Assert.Equal(2, item.ChunkCount);
        Assert.Equal("2026-07-11T00:00:00Z", item.CreatedAt);
        Assert.Equal("ready", item.Status);
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
}
