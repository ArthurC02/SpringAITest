namespace Backend.Api.Common;

/// <summary>
/// 帶 HTTP 狀態碼的商業例外。由 GlobalExceptionHandler 直接映射成對應狀態 + ApiError,
/// 訊息逐字沿用 platform 既有中文。相較 platform 每種錯誤各一個例外類別,這裡只有 backend
/// 自己會 throw 也自己會 map,故用單一類別攜帶狀態碼即可(不做 interface-per-error)。
/// </summary>
public sealed class ApiException : Exception
{
    public int Status { get; }

    /// <summary>
    /// 欄位級錯誤(選填)。Skill 存檔的 422 用它帶引擎回報的驗證錯誤碼:key = 錯誤碼、value = 人話訊息。
    /// null → GlobalExceptionHandler 輸出空 map(ApiError 形狀不變)。
    /// </summary>
    public IReadOnlyDictionary<string, string>? FieldErrors { get; init; }

    public ApiException(int status, string message) : base(message) => Status = status;
}
