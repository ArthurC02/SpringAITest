using System.Net;
using System.Text;

namespace Platform.Web.Tests;

/// <summary>
/// 攔截並緩存「platform → backend」轉送請求的共用 fake handler(方法、路徑、Content-Type、
/// Content-Length、body 位元組、request header),並回一個可腳本化的固定回應。
/// 用在需要保留真 service / 真 BackendClient、只把最下游換掉的整合測試。
/// </summary>
public sealed class CapturingBackendHandler : HttpMessageHandler
{
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _responseJson = "{}";
    private HttpRequestMessage? _last;

    public string? Path { get; private set; }

    /// <summary>Raw forwarded query string (leading '?', empty when absent) — proxies that pass a
    /// caller query through verbatim assert on this rather than on <see cref="Path"/>.</summary>
    public string? Query { get; private set; }

    public string? Method { get; private set; }
    public string? ContentType { get; private set; }
    public long? ContentLength { get; private set; }
    public byte[]? Body { get; private set; }

    /// <summary>設定下一個(以及之後每一個)回應的狀態碼與 JSON body。</summary>
    public void Reset(HttpStatusCode status, string responseJson)
    {
        _status = status;
        _responseJson = responseJson;
    }

    public string? Header(string name)
        => _last is not null && _last.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _last = request;
        Path = request.RequestUri!.AbsolutePath;
        Query = request.RequestUri.Query;
        Method = request.Method.Method;
        if (request.Content is not null)
        {
            // 先取 body(觸發序列化),再讀 Content-Length header(此時 MultipartFormDataContent 已算出長度)。
            Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType?.ToString();
            ContentLength = request.Content.Headers.ContentLength;
        }

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
        };
    }
}
