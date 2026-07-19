using System.Net.Http.Json;
using System.Text.Json;
using Platform.Service.Dtos;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// 呼叫核心 backend(:8002)的共用 HttpClient 包裝:組請求與傳輸層 catch 委由 <see cref="InternalRequest"/>
/// (X-Internal-Token + 需要時的身分 headers、強制 HTTP/1.1),另提供「送出→驗狀態碼→反序列化」的泛型收斂。
/// 狀態碼→例外的映射由各服務以 Func 傳入(不同端點對外語意不同)。
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
        => InternalRequest.Build(method, _options.BaseUrl.TrimEnd('/') + path, _options.InternalToken, ctx, body, JsonOpts);

    /// <summary>
    /// 送出請求;傳輸層錯誤與逾時經 <paramref name="wrapTransportError"/> 轉成呼叫端要的例外
    /// (不同端點的失敗對外狀態碼不同)。呼叫端主動取消則原樣拋出。
    /// </summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, Func<Exception, Exception> wrapTransportError, CancellationToken ct)
        => InternalRequest.SendAsync(_http, req, wrapTransportError, ct);

    /// <summary>
    /// 送出→若非 2xx 以 <paramref name="mapError"/> 轉例外拋出→反序列化 JSON body;body 為空則拋 <paramref name="onEmptyBody"/>。
    /// req 與 response 皆由本方法負責釋放。
    /// </summary>
    public async Task<T> SendForJsonAsync<T>(
        HttpRequestMessage req,
        Func<Exception, Exception> wrap,
        Func<HttpResponseMessage, CancellationToken, Task<Exception>> mapError,
        Func<Exception> onEmptyBody,
        CancellationToken ct)
    {
        using var resp = await SendCheckedAsync(req, wrap, mapError, ct);
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct) ?? throw onEmptyBody();
    }

    /// <summary>同 <see cref="SendForJsonAsync{T}"/> 但反序列化為 List;body 為空回空 List(清單端點不視為錯誤)。</summary>
    public async Task<List<T>> SendForJsonListAsync<T>(
        HttpRequestMessage req,
        Func<Exception, Exception> wrap,
        Func<HttpResponseMessage, CancellationToken, Task<Exception>> mapError,
        CancellationToken ct)
    {
        using var resp = await SendCheckedAsync(req, wrap, mapError, ct);
        return await resp.Content.ReadFromJsonAsync<List<T>>(JsonOpts, ct) ?? new List<T>();
    }

    /// <summary>送出並只確認成功(不讀 body,如 DELETE);非 2xx 以 <paramref name="mapError"/> 轉例外拋出。</summary>
    public async Task SendExpectSuccessAsync(
        HttpRequestMessage req,
        Func<Exception, Exception> wrap,
        Func<HttpResponseMessage, CancellationToken, Task<Exception>> mapError,
        CancellationToken ct)
    {
        using (await SendCheckedAsync(req, wrap, mapError, ct)) { }
    }

    /// <summary>組合 req 送出並驗狀態碼:成功回傳仍開啟的 response(呼叫端負責釋放),失敗則映射成例外拋出。</summary>
    private async Task<HttpResponseMessage> SendCheckedAsync(
        HttpRequestMessage req,
        Func<Exception, Exception> wrap,
        Func<HttpResponseMessage, CancellationToken, Task<Exception>> mapError,
        CancellationToken ct)
    {
        using (req)
        {
            var resp = await SendAsync(req, wrap, ct);
            if (resp.IsSuccessStatusCode)
            {
                return resp;
            }

            using (resp)
            {
                throw await mapError(resp, ct);
            }
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
