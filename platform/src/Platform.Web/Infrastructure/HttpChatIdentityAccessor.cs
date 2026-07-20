using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Web.Auth;

namespace Platform.Web.Infrastructure;

/// <summary>
/// <see cref="IChatIdentityAccessor"/> 的 Web 層實作:讀 <see cref="IHttpContextAccessor"/>。
/// Platform.Service 不能引用 ASP.NET Core,所以身分/記憶 key 推導的「讀取本次請求」這一半住在這裡。
/// 本類無狀態(請求狀態全存 HttpContext.Items,靠 <see cref="IHttpContextAccessor"/> 的 AsyncLocal
/// 傳遞,與 DI scope 無關),Scoped 或 Singleton 註冊皆功能正確;目前註冊為 Scoped(Program.cs),
/// agent 管線元件各自 CreateScope 解析到不同實例仍讀寫同一個 HttpContext.Items。
/// </summary>
public sealed class HttpChatIdentityAccessor : IChatIdentityAccessor
{
    private const string BodyUserIdKey = "chat.request.userId";
    private const string BodyConversationIdKey = "chat.request.conversationId";
    private const string PersistFailureKey = "chat.persist.failure";
    private const string PersistedResponseKey = "chat.persist.response";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpChatIdentityAccessor(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    public UserContext? CurrentUser
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true ? user.ToUserContext() : null;
        }
    }

    public (string Uid, string Cid) DeriveMemoryKeys()
    {
        var items = _httpContextAccessor.HttpContext?.Items;
        var userId = items?[BodyUserIdKey] as string;
        var conversationId = items?[BodyConversationIdKey] as string;
        return ChatMemoryKeyDerivation.Derive(userId, conversationId, CurrentUser);
    }

    public Exception? PersistFailure
    {
        get => _httpContextAccessor.HttpContext?.Items[PersistFailureKey] as Exception;
        set => SetItem(PersistFailureKey, value);
    }

    public ChatResponse? PersistedResponse
    {
        get => _httpContextAccessor.HttpContext?.Items[PersistedResponseKey] as ChatResponse;
        set => SetItem(PersistedResponseKey, value);
    }

    public void SetRequestKeys(string? userId, string? conversationId, UserContext? userCtx)
    {
        // userCtx 參數在 Web 實作刻意不使用:CurrentUser 恆以 HttpContext.User 為準(唯一信任來源),
        // 這裡傳入的值只是同一份已驗證身分的重複傳遞(controller 已從同一個 HttpContext.User 算出),
        // 不構成第二個信任來源,也絕不允許 body 覆寫身分。
        SetItem(BodyUserIdKey, userId);
        SetItem(BodyConversationIdKey, conversationId);
        SetItem(PersistFailureKey, null);
        SetItem(PersistedResponseKey, null);
    }

    private void SetItem(string key, object? value)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is not null)
        {
            context.Items[key] = value;
        }
    }
}
