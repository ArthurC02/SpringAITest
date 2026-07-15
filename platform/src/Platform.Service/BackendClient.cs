using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// 呼叫核心 backend(:8002)的共用 HttpClient 包裝:組請求(X-Internal-Token + 需要時的身分 headers)、
/// 強制 HTTP/1.1、把傳輸層錯誤(連線失敗/逾時)交由呼叫端指定的工廠轉成合適例外。
/// 狀態碼→例外的映射由各服務自行處理(不同端點對外語意不同)。
/// </summary>
public sealed class BackendClient
{
    // backend JSON 用 Web 預設(camelCase、大小寫不敏感);snake_case 欄位靠 DTO 上的 JsonPropertyName 對應。
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly BackendOptions _options;

    public BackendClient(HttpClient http, BackendOptions options)
    {
        _http = http;
        _options = options;
    }

    /// <summary>供各服務反序列化 backend 回應用的 JSON 設定。</summary>
    public JsonSerializerOptions Json => JsonOpts;

    /// <summary>組一個帶 X-Internal-Token 的 backend 請求;ctx 非 null 時再帶 3 個身分 header;可選 JSON body。</summary>
    public HttpRequestMessage BuildRequest(HttpMethod method, string path, UserContext? ctx = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, _options.BaseUrl.TrimEnd('/') + path)
        {
            // 強制 HTTP/1.1(避免下游在 h2c 升級時掉 body;與 workflow 呼叫一致)。
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        req.Headers.TryAddWithoutValidation("X-Internal-Token", _options.InternalToken);
        if (ctx is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Tenant-Id", ctx.TenantCode);
            req.Headers.TryAddWithoutValidation("X-User-Id", ctx.UserId);
            req.Headers.TryAddWithoutValidation("X-User-Role", ctx.Role);
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: JsonOpts);
        }

        return req;
    }

    /// <summary>
    /// 送出請求;傳輸層錯誤與逾時經 <paramref name="wrapTransportError"/> 轉成呼叫端要的例外
    /// (不同端點的失敗對外狀態碼不同)。呼叫端主動取消則原樣拋出。
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, Func<Exception, Exception> wrapTransportError, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw wrapTransportError(ex);
        }
    }

    /// <summary>讀取 backend ApiError body 的 message 欄位(讀不到/解析失敗回空字串)。</summary>
    public async Task<string> ReadErrorMessageAsync(HttpResponseMessage resp, CancellationToken ct)
        => (await ReadErrorAsync(resp, ct)).Message ?? string.Empty;

    /// <summary>讀取 backend ApiError body(message + fieldErrors);讀不到/解析失敗回全 null 的空殼。</summary>
    public async Task<BackendErrorBody> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<BackendErrorBody>(JsonOpts, ct) ?? EmptyError;
        }
        catch
        {
            return EmptyError;
        }
    }

    private static readonly BackendErrorBody EmptyError = new(null, null);
}

/// <summary>backend 的 ApiError body(只取 platform 會用到的兩個欄位)。</summary>
public sealed record BackendErrorBody(string? Message, Dictionary<string, string>? FieldErrors);
