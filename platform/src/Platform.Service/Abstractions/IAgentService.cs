using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// Agent Registry 服務:透明代理 backend /api/agents(D1)。不含任何商業邏輯 —— 角色把關、slug 唯一性、
/// draft 樂觀鎖(draftVersion/ETag)、validate/publish 語意全在 backend。
///
/// 刻意「不」走 Skill/Config 那套「狀態碼→例外→GlobalExceptionHandler」的映射:Agent 契約要求原樣穿透
/// ETag/If-Match 與 backend 的狀態碼(含 412 Precondition Failed —— 既有例外對照表沒有這一碼),
/// 用透明代理(回傳 status + body + ETag)比逐碼補例外型別更貼近「穿透」語意,也避免吞掉 ETag response header。
/// backend 的 4xx(400/403/404/409/422/428)body 已是 ApiError 形狀,原樣轉回;
/// 5xx 與傳輸失敗才收斂成 WorkflowInvocationException(對外 502,隱藏 backend 內部細節)。
/// </summary>
public interface IAgentService
{
    Task<AgentProxyResponse> ListAsync(UserContext ctx, CancellationToken ct = default);

    Task<AgentProxyResponse> CreateAsync(UserContext ctx, JsonElement? body, CancellationToken ct = default);

    Task<AgentProxyResponse> GetAsync(Guid id, UserContext ctx, CancellationToken ct = default);

    /// <summary>更新 draft;帶 If-Match 做樂觀鎖。backend:缺 If-Match → 428、版本過期 → 409(原樣穿透)。</summary>
    Task<AgentProxyResponse> UpdateDraftAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default);

    /// <summary>軟停用(deactivate)。</summary>
    Task<AgentProxyResponse> DeactivateAsync(Guid id, UserContext ctx, CancellationToken ct = default);

    /// <summary>重新啟用(enable);ADMIN 由 backend 判。</summary>
    Task<AgentProxyResponse> EnableAsync(Guid id, UserContext ctx, CancellationToken ct = default);

    Task<AgentProxyResponse> ValidateAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default);

    Task<AgentProxyResponse> PublishAsync(
        Guid id, UserContext ctx, string? ifMatch, JsonElement? body, CancellationToken ct = default);

    Task<AgentProxyResponse> RevisionsAsync(Guid id, UserContext ctx, CancellationToken ct = default);

    /// <summary>把舊 revision 重新發布為一個新 revision(不改寫歷史)。</summary>
    Task<AgentProxyResponse> RestoreRevisionAsync(
        Guid id, int revision, UserContext ctx, CancellationToken ct = default);
}

/// <summary>透明代理的一次回應:backend 的狀態碼、原始 JSON body 與 ETag(若有),由 controller 原樣寫回。</summary>
public sealed record AgentProxyResponse(int Status, string Body, string? ETag);
