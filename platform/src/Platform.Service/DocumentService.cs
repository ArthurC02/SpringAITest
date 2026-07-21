using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 文件服務:建立走 RabbitMQ 非同步(生成 id、發佈訊息、立即回 processing),
/// 讀取/刪除仍代理 backend /api/documents(HTTP)。傳輸層失敗 → WorkflowInvocationException
/// (List/Delete 前綴「文件服務呼叫失敗：」;發佈前綴「文件佇列服務呼叫失敗：」,對外皆 502)。
/// backend 的錯誤狀態碼走 <see cref="BackendErrorMapper"/>(與 Skill/ConfigurationSet 一致):
/// 4xx 各自對應同狀態碼、非預期狀態 → 502,不再一律壓成 502;delete 的 backend 404 另外映射成
/// DocumentNotFoundException(文件專屬訊息,對外仍 404)。
/// </summary>
public sealed class DocumentService : IDocumentService
{
    private const string FailurePrefix = "文件服務呼叫失敗：";
    private const string QueueFailurePrefix = "文件佇列服務呼叫失敗：";

    private readonly BackendClient _backend;
    private readonly IDocumentQueue _queue;

    public DocumentService(BackendClient backend, IDocumentQueue queue)
    {
        _backend = backend;
        _queue = queue;
    }

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public async Task<DocumentAccepted> CreateAsync(DocumentCreateRequest request, UserContext ctx, CancellationToken ct = default)
    {
        var documentId = Guid.NewGuid().ToString();
        var message = new DocumentMessage(documentId, ctx.TenantCode, ctx.UserId, request.Title!, request.Text!);

        try
        {
            await _queue.PublishAsync(message, ct);
        }
        catch (Exception ex)
        {
            throw new WorkflowInvocationException(QueueFailurePrefix + ex.Message, ex);
        }

        return new DocumentAccepted(documentId, request.Title!, "processing");
    }

    public Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(HttpMethod.Get, "/api/documents", ctx), WrapTransport, MapErrorAsync, ct);

    public Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default)
        => _backend.SendExpectSuccessAsync(
            _backend.BuildRequest(HttpMethod.Delete, $"/api/documents/{id}", ctx),
            WrapTransport,
            // backend 404 特別映射成 DocumentNotFoundException(文件專屬訊息);其餘走共用映射。
            (r, c) => (int)r.StatusCode == 404
                ? Task.FromResult<Exception>(new DocumentNotFoundException("找不到文件：" + id))
                : MapErrorAsync(r, c),
            ct);

    private Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(resp, _backend, FailurePrefix, ct);
}
