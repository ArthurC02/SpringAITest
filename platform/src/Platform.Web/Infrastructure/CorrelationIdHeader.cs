namespace Platform.Web.Infrastructure;

/// <summary>
/// 跨服務追蹤鏈路的起點。Platform 是 correlationId 的**權威來源**:一律用本請求的
/// <see cref="HttpContext.TraceIdentifier"/>(同時也是 ApiError body 的 <c>correlationId</c>),
/// 絕不採信瀏覽器帶進來的值 —— 公開端點的入站 header 是不可信輸入,反射它等於送對方一條
/// 把任意字串寫進三個服務日誌的通道。往內部下游(backend :8002 / workflow :8001)才轉發。
/// </summary>
internal static class CorrelationIdHeader
{
    internal const string Name = "X-Correlation-Id";
}

/// <summary>
/// 掛在五顆內部下游 HttpClient 的 handler 鏈上(見 Program.cs 的 <c>WithInternalDownstreamDefaults</c>),
/// 所以 <c>InternalRequest.Build</c> 的每個呼叫端 —— 含 best-effort kick —— 都自動涵蓋,
/// Platform.Service 不必為此依賴 ASP.NET Core。無 HttpContext 的呼叫(啟動探針、背景工作)
/// 不帶此 header,不造假值。
/// </summary>
internal sealed class CorrelationIdForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _http;

    public CorrelationIdForwardingHandler(IHttpContextAccessor http) => _http = http;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_http.HttpContext?.TraceIdentifier is { Length: > 0 } correlationId)
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdHeader.Name, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
