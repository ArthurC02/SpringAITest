using System.Net.Http.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 文件服務:建立走 RabbitMQ 非同步(生成 id、發佈訊息、立即回 processing),
/// 讀取/刪除仍代理 backend /api/documents(HTTP)。失敗一律包成 WorkflowInvocationException
/// (List/Delete 前綴「文件服務呼叫失敗：」;發佈前綴「文件佇列服務呼叫失敗：」,對外皆 502),
/// 但 delete 的 backend 404 特別映射成 DocumentNotFoundException。
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

    public async Task<IReadOnlyList<DocumentInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, "/api/documents", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await resp.Content.ReadFromJsonAsync<List<DocumentInfo>>(_backend.Json, ct)
            ?? new List<DocumentInfo>();
    }

    public async Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Delete, $"/api/documents/{id}", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        if ((int)resp.StatusCode == 404)
        {
            throw new DocumentNotFoundException("找不到文件：" + id);
        }

        throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
    }
}
