using Microsoft.AspNetCore.Mvc;
using Platform.Service.Abstractions;

namespace Platform.Web.Controllers;

/// <summary>
/// 透明代理端點共用的三件事:把 backend 的 status/body/ETag 原樣寫回,以及兩個前置條件 header 的讀取。
/// 刻意不帶 [Route] / [Authorize] / [ApiController] —— 路由與授權是各端點自己的決策(D1 ADMIN、
/// D4/D7 workflow.manage、D7 approvals 只要求已認證),絕不從共用基底隱式繼承。
/// </summary>
public abstract class ProxyControllerBase : ControllerBase
{
    /// <summary>本次請求的 Idempotency-Key(缺則 null);哪些命令要往下轉發由各端點決定。</summary>
    protected string? IdempotencyKey =>
        Request.Headers.TryGetValue("Idempotency-Key", out var value) ? value.ToString() : null;

    /// <summary>本次請求的 If-Match(樂觀鎖前置條件);缺則 null。原樣轉發給 backend。</summary>
    protected string? IfMatch =>
        Request.Headers.IfMatch.Count > 0 ? Request.Headers.IfMatch.ToString() : null;

    /// <summary>把透明代理回應原樣寫回:狀態碼、JSON body 與 ETag response header(若有)。</summary>
    protected IActionResult Write(AgentProxyResponse response)
    {
        if (response.ETag is not null)
        {
            Response.Headers.ETag = response.ETag;
        }

        return new ContentResult
        {
            StatusCode = response.Status,
            Content = response.Body,
            ContentType = "application/json; charset=utf-8",
        };
    }
}
