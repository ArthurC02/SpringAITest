using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// 呼叫下游 Python 服務(工作流/文件)的共用基底:組請求(含 4 個 X-* header)、
/// 強制 HTTP/1.1、把網路/逾時錯誤統一包成 WorkflowInvocationException。
/// </summary>
public abstract class DownstreamServiceBase
{
    // 下游 JSON 用 Web 預設(camelCase、大小寫不敏感);snake_case 欄位靠 DTO 上的 JsonPropertyName 對應。
    protected static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected readonly HttpClient Http;
    private readonly WorkflowOptions _options;

    protected DownstreamServiceBase(HttpClient http, WorkflowOptions options)
    {
        Http = http;
        _options = options;
    }

    protected string BaseUrl => _options.BaseUrl.TrimEnd('/');

    /// <summary>組一個帶 4 個內部 header 的下游請求;可選 JSON body。</summary>
    protected HttpRequestMessage BuildRequest(HttpMethod method, string url, UserContext ctx, object? body = null)
    {
        var req = new HttpRequestMessage(method, url)
        {
            // 強制 HTTP/1.1(.NET 預設即 1.1,不開 h2c;避免下游 uvicorn 在 h2c 升級時掉 body)。
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        req.Headers.TryAddWithoutValidation("X-Internal-Token", _options.InternalToken);
        req.Headers.TryAddWithoutValidation("X-Tenant-Id", ctx.TenantCode);
        req.Headers.TryAddWithoutValidation("X-User-Id", ctx.UserId);
        req.Headers.TryAddWithoutValidation("X-User-Role", ctx.Role);

        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: JsonOpts);
        }

        return req;
    }

    /// <summary>送出請求;網路錯誤與逾時統一轉成 WorkflowInvocationException(前綴由呼叫端指定)。</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string failurePrefix, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 呼叫端主動取消,原樣拋出(不算下游失敗)。
            throw;
        }
        catch (Exception ex)
        {
            // 連線失敗、逾時(HttpClient.Timeout 觸發的 TaskCanceledException)等,都算下游呼叫失敗。
            throw new WorkflowInvocationException(failurePrefix + ex.Message, ex);
        }
    }
}
