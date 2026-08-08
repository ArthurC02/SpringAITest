using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;

namespace Platform.Service;

/// <summary>
/// Agent Registry 透明代理(見 <see cref="IAgentService"/>)。組 backend 請求(帶 X-Internal-Token +
/// 身分 headers + 選擇性 If-Match + JSON body),送出後把 backend 的狀態碼、原始 body 與 ETag 原樣回傳;
/// 只有 5xx 與傳輸失敗才收斂成對外 502。
/// </summary>
public sealed class AgentService : IAgentService
{
    private const string FailurePrefix = "Agent 服務呼叫失敗：";

    private readonly BackendClient _backend;

    public AgentService(BackendClient backend) => _backend = backend;

    /// <summary>
    /// 2xx 與 4xx(含 draft concurrency 的 409/428)一律原樣穿透:body 直接是 backend 的 JSON
    /// (domain snake_case 或 ApiError)，ETag 若有則帶回;5xx 與傳輸失敗由
    /// <see cref="BackendClient.SendForAgentProxyAsync"/> 收斂成對外 502。
    /// </summary>
    public Task<AgentProxyResponse> SendAsync(
        HttpMethod method,
        string suffix,
        UserContext ctx,
        string? ifMatch = null,
        JsonElement? body = null,
        CancellationToken ct = default)
        => _backend.SendForAgentProxyAsync(
            method,
            string.IsNullOrEmpty(suffix) ? "/api/agents" : "/api/agents/" + suffix,
            ctx,
            body.HasValue ? (object)body.Value : null,
            FailurePrefix,
            string.IsNullOrEmpty(ifMatch) ? null : ("If-Match", ifMatch),
            ct);
}
