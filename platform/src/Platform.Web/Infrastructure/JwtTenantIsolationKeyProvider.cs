using Microsoft.Agents.AI.Hosting;
using Platform.Web.Auth;

namespace Platform.Web.Infrastructure;

/// <summary>
/// AG-UI hosted agent 的 session isolation key provider。
/// isolation key 只取 JWT 身分(租戶 + 使用者),絕不取 wire 上的 threadId / request body 任何欄位
/// (框架 XML doc 明確警告 ThreadId 不是授權憑證)。取不到 → 回 null,
/// 由 Strict=true 的 IsolationKeyScopedAgentSessionStore 拋 InvalidOperationException(fail-closed)。
/// P2(copilot-shared-core §5.3):key 含使用者維度(不只租戶),使同租戶不同使用者以同一 threadId
/// 提問也不互見(B-P1-05/B-P2-01 系列)——兩者都只源自 JWT claims,fail-closed 語意不變,只是粒度變細。
/// </summary>
public sealed class JwtTenantIsolationKeyProvider : SessionIsolationKeyProvider
{
    private readonly IHttpContextAccessor _http;

    public JwtTenantIsolationKeyProvider(IHttpContextAccessor http) => _http = http;

    public override ValueTask<string?> GetSessionIsolationKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_http.HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return new ValueTask<string?>((string?)null);
        }

        var ctx = _http.HttpContext.User.ToUserContext();
        return new ValueTask<string?>($"{ctx.TenantCode}:{ctx.UserId}");
    }
}
