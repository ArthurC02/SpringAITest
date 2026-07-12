namespace Platform.Service.Exceptions;

/// <summary>找不到租戶(register 時 code 對不到)。全域處理對應 HTTP 404。</summary>
public sealed class TenantNotFoundException : Exception
{
    public TenantNotFoundException(string message) : base(message) { }
}

/// <summary>邀請碼與租戶設定不符。全域處理對應 HTTP 403。</summary>
public sealed class InvalidInviteCodeException : Exception
{
    public InvalidInviteCodeException(string message) : base(message) { }
}

/// <summary>使用者名稱已被占用。全域處理對應 HTTP 409。</summary>
public sealed class UsernameTakenException : Exception
{
    public UsernameTakenException(string message) : base(message) { }
}

/// <summary>帳號或密碼錯誤(找不到人或密碼比對失敗)。全域處理對應 HTTP 401。</summary>
public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException(string message) : base(message) { }
}
