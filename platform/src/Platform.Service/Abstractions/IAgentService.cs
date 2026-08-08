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
    /// <summary>
    /// 送一次 <c>/api/agents</c> 代理請求(比照 <see cref="IWorkflowAdminService.SendAsync"/>)。
    /// 哪條路徑、要不要帶 If-Match 都是呼叫端(AgentController)的決策 —— 這一層沒有 per-action 語意,
    /// 每個 action 曾經各有一個一行委派方法,只是同一顆代理的十份複本。
    /// </summary>
    /// <param name="suffix">
    /// 接在 <c>/api/agents</c> 後面的路徑片段(空字串 = 集合本身)。呼叫端必須只用已解析過的
    /// route 值組出(Guid 以 <c>D</c> 格式、revision 是 int),不得讓原始 route 文字參與 URI normalization。
    /// </param>
    Task<AgentProxyResponse> SendAsync(
        HttpMethod method,
        string suffix,
        UserContext ctx,
        string? ifMatch = null,
        JsonElement? body = null,
        CancellationToken ct = default);
}

/// <summary>透明代理的一次回應:backend 的狀態碼、原始 JSON body 與 ETag(若有),由 controller 原樣寫回。</summary>
public sealed record AgentProxyResponse(int Status, string Body, string? ETag);
