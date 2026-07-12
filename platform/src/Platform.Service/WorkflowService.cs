using System.Net.Http.Json;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;
using Platform.Service.Options;

namespace Platform.Service;

/// <summary>
/// 工作流服務:代理下游 Python。角色把關在 Python 端,本服務只轉發 X-User-Role 並轉譯狀態碼:
/// 404 → NotFound、403 → Forbidden、422 → BadInput(對外變 400)、其他 → Invocation(對外 502)。
/// </summary>
public sealed class WorkflowService : DownstreamServiceBase, IWorkflowService
{
    private const string FailurePrefix = "工作流服務呼叫失敗：";

    public WorkflowService(HttpClient http, WorkflowOptions options) : base(http, options)
    {
    }

    public async Task<IReadOnlyList<WorkflowInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"{BaseUrl}/workflows", ctx);
        using var resp = await SendAsync(req, FailurePrefix, ct);

        // list:任何失敗(含 4xx/5xx)都當成呼叫失敗。
        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await resp.Content.ReadFromJsonAsync<List<WorkflowInfo>>(JsonOpts, ct)
            ?? new List<WorkflowInfo>();
    }

    public async Task<WorkflowInvokeResponse> InvokeAsync(
        string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"{BaseUrl}/workflows/{name}/invoke", ctx, new { input });
        using var resp = await SendAsync(req, FailurePrefix, ct);

        if (!resp.IsSuccessStatusCode)
        {
            switch ((int)resp.StatusCode)
            {
                case 404:
                    throw new WorkflowNotFoundException("找不到工作流：" + name);
                case 403:
                    throw new WorkflowForbiddenException("權限不足，無法執行工作流：" + name);
                case 422:
                    // 下游 422 → 本服務 400;訊息帶上下游回應 body 字串。
                    var downstreamBody = await resp.Content.ReadAsStringAsync(ct);
                    throw new WorkflowBadInputException("工作流輸入不符合規範：" + downstreamBody);
                default:
                    throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
            }
        }

        return await resp.Content.ReadFromJsonAsync<WorkflowInvokeResponse>(JsonOpts, ct)
            ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
    }
}
