using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>工作流服務:代理下游 Python。轉發 4 個 X-* header 並轉譯下游狀態碼。</summary>
public interface IWorkflowService
{
    /// <summary>列出可用工作流。</summary>
    Task<IReadOnlyList<WorkflowInfo>> ListAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>執行指定工作流。</summary>
    Task<WorkflowInvokeResponse> InvokeAsync(string name, Dictionary<string, JsonElement> input, UserContext ctx, CancellationToken ct = default);
}
