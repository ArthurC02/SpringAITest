namespace Platform.Service.Exceptions;

/// <summary>下游回報找不到工作流(下游 404)。全域處理對應 HTTP 404。</summary>
public sealed class WorkflowNotFoundException : Exception
{
    public WorkflowNotFoundException(string message) : base(message) { }
}

/// <summary>下游拒絕執行工作流(下游 403,角色不足)。全域處理對應 HTTP 403。</summary>
public sealed class WorkflowForbiddenException : Exception
{
    public WorkflowForbiddenException(string message) : base(message) { }
}

/// <summary>
/// 工作流輸入不符規範(下游 422)。
/// 注意:下游的 422 在本服務對外映射成 HTTP 400。
/// </summary>
public sealed class WorkflowBadInputException : Exception
{
    public WorkflowBadInputException(string message) : base(message) { }
}

/// <summary>
/// 呼叫下游工作流/文件服務失敗(5xx、網路錯誤、逾時、回應無法解析等)。
/// 全域處理對應 HTTP 502。文件服務的失敗也復用此例外(僅訊息前綴不同)。
/// </summary>
public sealed class WorkflowInvocationException : Exception
{
    public WorkflowInvocationException(string message) : base(message) { }

    public WorkflowInvocationException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>下游回報找不到文件(下游 404)。全域處理對應 HTTP 404。</summary>
public sealed class DocumentNotFoundException : Exception
{
    public DocumentNotFoundException(string message) : base(message) { }
}

/// <summary>
/// 呼叫 backend 的認證/聊天歷史等端點失敗(網路錯誤、逾時、非預期狀態碼)。
/// 刻意「不」列入全域例外對照表 → 落到預設 HTTP 500,維持原本(本地 DB 失敗即 500)的對外行為,
/// 不引入 auth/chat 端點原先沒有的 502。
/// </summary>
public sealed class BackendCallException : Exception
{
    public BackendCallException(string message) : base(message) { }

    public BackendCallException(string message, Exception innerException)
        : base(message, innerException) { }
}
