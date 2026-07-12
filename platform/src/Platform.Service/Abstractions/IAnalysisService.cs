using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>分析服務:代理 backend。轉發身分 header 並轉譯錯誤(比照 Document 模式)。</summary>
public interface IAnalysisService
{
    /// <summary>租戶文件統計摘要。</summary>
    Task<AnalysisSummary> SummaryAsync(UserContext ctx, CancellationToken ct = default);
}
