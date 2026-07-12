using System.Net.Http.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// 分析服務:代理 backend /api/analysis/summary(需 X-Tenant-Id,由 ctx 轉發)。
/// 錯誤映射比照 Document 模式:任何失敗都包成 WorkflowInvocationException(對外 502)。
/// </summary>
public sealed class AnalysisService : IAnalysisService
{
    private const string FailurePrefix = "分析服務呼叫失敗：";

    private readonly BackendClient _backend;

    public AnalysisService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public async Task<AnalysisSummary> SummaryAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, "/api/analysis/summary", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new WorkflowInvocationException(FailurePrefix + "HTTP " + (int)resp.StatusCode);
        }

        return await resp.Content.ReadFromJsonAsync<AnalysisSummary>(_backend.Json, ct)
            ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
    }
}
