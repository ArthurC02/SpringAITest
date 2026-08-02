using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Service;

/// <summary>
/// Agent Registry 透明代理(見 <see cref="IAgentService"/>)。每個公開方法對映一條 backend /api/agents 路徑,
/// 共用一顆 <see cref="ProxyAsync"/>:組 backend 請求(帶 X-Internal-Token + 身分 headers + 選擇性 If-Match + JSON body),
/// 送出後把 backend 的狀態碼、原始 body 與 ETag 原樣回傳;只有 5xx 與傳輸失敗才收斂成對外 502。
/// </summary>
public sealed class AgentService : IAgentService
{
    private const string FailurePrefix = "Agent 服務呼叫失敗：";

    private readonly BackendClient _backend;

    public AgentService(BackendClient backend) => _backend = backend;

    public Task<AgentProxyResponse> ListAsync(UserContext ctx, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Get, "/api/agents", ctx, null, null, ct);

    public Task<AgentProxyResponse> CreateAsync(UserContext ctx, JsonElement? body, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Post, "/api/agents", ctx, null, body, ct);

    public Task<AgentProxyResponse> GetAsync(Guid id, UserContext ctx, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Get, AgentPath(id), ctx, null, null, ct);

    public Task<AgentProxyResponse> UpdateDraftAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Put, AgentPath(id, "draft"), ctx, ifMatch, body, ct);

    public Task<AgentProxyResponse> DeactivateAsync(Guid id, UserContext ctx, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Delete, AgentPath(id), ctx, null, null, ct);

    public Task<AgentProxyResponse> EnableAsync(Guid id, UserContext ctx, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Post, AgentPath(id, "enable"), ctx, null, null, ct);

    public Task<AgentProxyResponse> ValidateAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Post, AgentPath(id, "validate"), ctx, ifMatch, body, ct);

    public Task<AgentProxyResponse> PublishAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Post, AgentPath(id, "publish"), ctx, ifMatch, body, ct);

    public Task<AgentProxyResponse> RevisionsAsync(Guid id, UserContext ctx, CancellationToken ct = default)
        => ProxyAsync(HttpMethod.Get, AgentPath(id, "revisions"), ctx, null, null, ct);

    public Task<AgentProxyResponse> RestoreRevisionAsync(
        Guid id, int revision, UserContext ctx, CancellationToken ct = default)
        => ProxyAsync(
            HttpMethod.Post,
            AgentPath(id, $"revisions/{revision}/restore"),
            ctx,
            null,
            null,
            ct);

    /// <summary>
    /// Agent id 先在 Web boundary 解析成 Guid，再以固定 D 格式放入已知路徑片段；
    /// 不讓 caller-controlled route text 參與 URI normalization。
    /// </summary>
    private static string AgentPath(Guid id, string? suffix = null)
        => suffix is null
            ? $"/api/agents/{id:D}"
            : $"/api/agents/{id:D}/{suffix}";

    /// <summary>
    /// 2xx 與 4xx(含 draft concurrency 的 409/428)一律原樣穿透:body 直接是 backend 的 JSON
    /// (domain snake_case 或 ApiError)，ETag 若有則帶回;5xx 與傳輸失敗由
    /// <see cref="BackendClient.SendForAgentProxyAsync"/> 收斂成對外 502。
    /// </summary>
    private Task<AgentProxyResponse> ProxyAsync(
        HttpMethod method, string path, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct)
        => _backend.SendForAgentProxyAsync(
            method,
            path,
            ctx,
            body.HasValue ? (object)body.Value : null,
            FailurePrefix,
            string.IsNullOrEmpty(ifMatch) ? null : ("If-Match", ifMatch),
            ct);
}
