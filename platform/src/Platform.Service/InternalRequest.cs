using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service;

/// <summary>
/// 呼叫內部下游(backend :8002 / workflow :8001)的共用請求樣板:內部憑證 + 身分 header 名稱常數、
/// 組請求(強制 HTTP/1.1、X-Internal-Token + 需要時的身分 headers、可選 JSON body)、傳輸層 catch。
/// BackendClient 與 WorkflowService 都走它 — 這四個 header 字串與 HTTP/1.1 強制只有這一份事實。
/// 狀態碼→例外的映射仍由各呼叫端自理(不同端點對外語意不同)。
/// </summary>
public static class InternalRequest
{
    /// <summary>內部信任邊界的憑證 header;後續是上游如實轉發的身分 header。</summary>
    public const string InternalTokenHeader = "X-Internal-Token";
    public const string TenantIdHeader = "X-Tenant-Id";
    public const string UserIdHeader = "X-User-Id";
    public const string UserRoleHeader = "X-User-Role";

    /// <summary>
    /// 使用者的 capability tags(取自 JWT capabilities claim,例如 workflow.manage);
    /// 空白分隔。無 capability 時「不帶」此 header(fail-closed:缺席即無授權,絕不代表全部)。
    /// </summary>
    public const string UserCapabilitiesHeader = "X-User-Capabilities";
    public const string UserGroupsHeader = "X-User-Groups";

    /// <summary>
    /// 組一個帶 X-Internal-Token 的下游請求;<paramref name="ctx"/> 非 null 時再帶 3 個身分 header;可選 JSON body。
    /// 強制 HTTP/1.1(不開 h2c;避免下游 uvicorn 在 h2c 升級時掉 body)。
    /// </summary>
    public static HttpRequestMessage Build(
        HttpMethod method, string url, string internalToken, UserContext? ctx, object? body, JsonSerializerOptions json)
    {
        var req = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        req.Headers.TryAddWithoutValidation(InternalTokenHeader, internalToken);
        if (ctx is not null)
        {
            req.Headers.TryAddWithoutValidation(TenantIdHeader, ctx.TenantCode);
            req.Headers.TryAddWithoutValidation(UserIdHeader, ctx.UserId);
            req.Headers.TryAddWithoutValidation(UserRoleHeader, ctx.Role);
            if (ctx.Capabilities is { Count: > 0 } capabilities)
            {
                req.Headers.TryAddWithoutValidation(UserCapabilitiesHeader, string.Join(' ', capabilities));
            }
            if (ctx.Groups is { Count: > 0 } groups)
            {
                if (!UserGroupContract.IsCanonicalGroupSet(groups))
                {
                    throw new InvalidOperationException(
                        "Authenticated group set exceeds the internal identity contract");
                }
                req.Headers.TryAddWithoutValidation(UserGroupsHeader, string.Join(' ', groups));
            }
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body, options: json);
        }

        return req;
    }

    /// <summary>
    /// 送出請求;傳輸層錯誤與逾時經 <paramref name="wrap"/> 轉成呼叫端要的例外(不同端點的失敗對外狀態碼不同)。
    /// 呼叫端主動取消(ct 已觸發)則原樣拋出,不算下游失敗。
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage req, Func<Exception, Exception> wrap, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw wrap(ex);
        }
    }
}
