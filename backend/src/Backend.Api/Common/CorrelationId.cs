namespace Backend.Api.Common;

/// <summary>
/// 跨服務追蹤鏈路的 backend 這一段:接收上游(platform/workflow)的 <c>X-Correlation-Id</c>,
/// 用它取代本請求的 <see cref="HttpContext.TraceIdentifier"/>,再往 workflow 出站時原樣轉發。
///
/// **為什麼是覆寫 TraceIdentifier 而不是另開一個 Items key**:TraceIdentifier 已經是
/// <see cref="ApiErrorWriter"/>、<see cref="ValidationErrorResponse"/>、<see cref="ApiErrors.VersionConflict"/>
/// 與 <see cref="GlobalExceptionHandler"/> 日誌四處共用的 correlationId 來源 —— 換掉源頭,四處自動同號,
/// 不必逐處改。已知上限:ASP.NET Core 內建的 request log scope(<c>RequestId</c>)在中介軟體之前就把
/// 舊值抓走了,那一欄仍是自動產生的 connection id;需要它同步時再加自己的 log scope。
///
/// 信任模型:此 header 只可能來自 <see cref="InternalTokenMiddleware"/> 保護後的 platform/workflow,
/// 屬內部可信輸入 —— 但仍走邊界檢查(見 <see cref="IdentityHeaders.SingleBoundedValue"/>):
/// 值會被 <c>TryAddWithoutValidation</c> 放進出站 header,含 CR/LF 就是 header 注入。
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        // 不合格(多值/空白/超長/含控制字元)一律當作沒帶,沿用自動產生的 TraceIdentifier —— 不回錯:
        // 追蹤編號壞掉不是呼叫端的請求本身有問題,拒絕它只會把可觀測性問題升級成功能故障。
        if (context.Request.SingleBoundedValue(HeaderName) is { } correlationId)
        {
            context.TraceIdentifier = correlationId;
        }

        return _next(context);
    }
}

/// <summary>
/// backend → workflow(:8001)出站呼叫的 correlation 轉發。掛在五顆 workflow client 的 handler 鏈上,
/// 所以 <see cref="InternalWorkflowClient.UseInternalIdentity"/> 的每個呼叫端都自動涵蓋,
/// 不必逐個 validator 多接一個參數。無 HttpContext 的呼叫(背景工作)不帶此 header,不造假值。
/// </summary>
public sealed class CorrelationIdForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _http;

    public CorrelationIdForwardingHandler(IHttpContextAccessor http) => _http = http;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_http.HttpContext?.TraceIdentifier is { Length: > 0 } correlationId)
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
