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
/// FieldErrors:下游 400 body 若帶欄位級錯誤(backend ApiError.fieldErrors),原樣帶上來讓全域處理輸出;
/// null 表示沒有欄位級資訊(對外仍是空 map,維持 ApiError 形狀不變)。
/// </summary>
public sealed class WorkflowBadInputException : Exception
{
    public WorkflowBadInputException(string message) : base(message) { }

    public IReadOnlyDictionary<string, string>? FieldErrors { get; init; }
}

/// <summary>
/// A caller-controlled Workflow payload exceeded its bounded body/depth limits.
/// This remains HTTP 413 at the public boundary rather than being misclassified as an outage.
/// </summary>
public sealed class WorkflowPayloadTooLargeException : Exception
{
    public WorkflowPayloadTooLargeException(string message) : base(message) { }
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

/// <summary>
/// 下游回報資源衝突(下游 409,例如 skill 名稱已存在)。全域處理對應 HTTP 409。
/// 既有例外型別無法表達 409,又不能用 502 吞掉 — 代理層必須原樣轉發狀態碼。
/// </summary>
public sealed class DownstreamConflictException : Exception
{
    public DownstreamConflictException(string message) : base(message) { }
}

/// <summary>下游回報找不到文件(下游 404)。全域處理對應 HTTP 404。</summary>
public sealed class DocumentNotFoundException : Exception
{
    public DocumentNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Skill 定義未通過引擎的靜態驗證(backend 422)。全域處理對應 HTTP **422**(不是 400):
/// 422 是本契約中「語法正確但語意不合法」的專屬碼,前端編輯器靠它與欄位級的 400 區分開來。
/// FieldErrors:引擎錯誤碼清單(key = unknown_node/unbounded_loop/…),必須原樣穿過代理層 —
/// 錯誤碼被吞掉的話,編輯器就指不出是哪一條規則、哪一行出錯。
/// </summary>
public sealed class SkillValidationFailedException : Exception
{
    public SkillValidationFailedException(string message) : base(message) { }

    public IReadOnlyDictionary<string, string>? FieldErrors { get; init; }
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
