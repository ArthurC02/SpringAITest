using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>
/// Configuration Set CRUD 服務:純代理 backend /api/configuration-sets。ADMIN 把關全在 backend
/// (backend 403 → WorkflowForbiddenException → 對外 403,同 Config PUT / Skill CRUD 模式);
/// values 的型別/範圍驗證由 backend 負責(backend 422 → SkillValidationFailedException → 對外 422)。
/// **刻意不含**「讀取 active set」—— 那是 workflow → backend 直連(執行期取值),
/// platform 不得提供 invoke active-set 讀取捷徑(SSR-P4-011)。
/// 讀取端點(list/get)原樣穿透 backend JSON(snake_case),不套 DTO 以免吞掉 backend 新增欄位。
/// </summary>
public interface IConfigurationSetService
{
    /// <summary>列出本租戶的 Configuration Set(backend 清單不含 values 內容);原樣穿透 backend JSON。</summary>
    Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default);

    /// <summary>取單一 Configuration Set(含 values);原樣穿透 backend JSON;backend 404 → WorkflowNotFoundException(對外 404)。</summary>
    Task<JsonElement> GetAsync(string id, UserContext ctx, CancellationToken ct = default);

    /// <summary>建立 Configuration Set;backend 409(同名) → DownstreamConflictException(對外 409)。</summary>
    Task<ConfigurationSet> CreateAsync(ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default);

    /// <summary>更新 Configuration Set。</summary>
    Task<ConfigurationSet> UpdateAsync(string id, ConfigurationSetUpsert request, UserContext ctx, CancellationToken ct = default);

    /// <summary>刪除 Configuration Set(對外 204)。</summary>
    Task DeleteAsync(string id, UserContext ctx, CancellationToken ct = default);

    /// <summary>啟用指定 Configuration Set(同租戶其餘自動停用,原子性由 backend 保證)。</summary>
    Task<ConfigurationSet> ActivateAsync(string id, UserContext ctx, CancellationToken ct = default);
}
