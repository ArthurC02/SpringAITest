using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>系統組態服務:代理 backend。PUT 的 ADMIN 把關在 backend,backend 403 → 對外 403。</summary>
public interface IConfigService
{
    /// <summary>列出所有組態。</summary>
    Task<IReadOnlyList<ConfigItem>> ListAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>更新組態;backend 對非 ADMIN 回 403 → WorkflowForbiddenException(對外 403)。</summary>
    Task<ConfigItem> UpdateAsync(string key, ConfigUpdateRequest request, UserContext ctx, CancellationToken ct = default);
}
