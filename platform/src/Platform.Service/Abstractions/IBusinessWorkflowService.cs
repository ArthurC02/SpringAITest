using System.Text.Json;
using Platform.Service.Dtos;

namespace Platform.Service.Abstractions;

/// <summary>Business Workflow 管理 API 的透明 backend 代理。</summary>
public interface IBusinessWorkflowService
{
    Task<JsonElement> ListAsync(UserContext context, CancellationToken ct = default);
    Task<JsonElement> GetAsync(string name, UserContext context, CancellationToken ct = default);
    Task<BusinessWorkflowCreated> CreateAsync(
        SkillUpsert request, UserContext context, CancellationToken ct = default);
    Task<Skill> UpdateAsync(string name, SkillUpsert request, UserContext context, CancellationToken ct = default);
    Task DeleteAsync(string name, UserContext context, CancellationToken ct = default);
    Task<SkillExport> ExportAsync(string name, UserContext context, CancellationToken ct = default);
}
