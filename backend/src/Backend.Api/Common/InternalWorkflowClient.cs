using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Backend.Api.Common;

/// <summary>
/// Backend → workflow(:8001)出站呼叫的共用樣板:內部憑證 + 三個身分 header、強制 HTTP/1.1,
/// 以及「引擎不可達 = 502 fail closed」的統一失敗處理。呼叫端只保留自己的訊息前綴與 body 契約檢查
/// (各 validator 的 detail 文案是對外契約的一部分,不在此集中)。
/// </summary>
internal static class InternalWorkflowClient
{
    /// <summary>
    /// 帶上內部信任邊界所需的四個 header,並強制 HTTP/1.1
    /// (避免下游 uvicorn 在 h2c 升級時掉 body;與 platform 對 workflow 的呼叫一致)。
    /// </summary>
    public static void UseInternalIdentity(
        this HttpRequestMessage request, string internalToken, string tenantId, string? userId, string? role)
    {
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.TryAddWithoutValidation("X-Internal-Token", internalToken);
        request.Headers.TryAddWithoutValidation(IdentityHeaders.TenantHeader, tenantId);
        request.Headers.TryAddWithoutValidation(IdentityHeaders.UserHeader, userId ?? string.Empty);
        request.Headers.TryAddWithoutValidation(IdentityHeaders.RoleHeader, role ?? string.Empty);
    }

    /// <summary>
    /// 送出並解析 JSON body。傳輸失敗、非 2xx、無法解析、body 為字面 null 一律交給 <paramref name="failure"/>
    /// (一律 502):引擎的「驗證結果」只存在於 2xx body,任何其他形態都是服務故障,絕不放行未驗證的寫入。
    /// 呼叫端自己的取消(ct)不算故障,原樣往上拋。
    /// </summary>
    public static async Task<T> SendJsonAsync<T>(
        this HttpClient http,
        HttpRequestMessage request,
        Func<string, ApiException> failure,
        JsonSerializerOptions? options,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw failure(ex.Message);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw failure("HTTP " + (int)response.StatusCode);
            }

            T? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<T>(options, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw failure(ex.Message);
            }

            return body is null ? throw failure("回應內容為空") : body;
        }
    }
}
