using System.Net.Http.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// Skill CRUD 服務:代理 backend /api/skills,不含任何商業邏輯(角色/唯一性/定義驗證全在 backend)。
/// backend 的錯誤原樣轉發:400/403/404/409/422 各自映射到對外同狀態碼的例外並沿用 backend 的 message;
/// 其餘(5xx、傳輸失敗)→ WorkflowInvocationException(對外 502)。
/// 400 與 422 的 fieldErrors 都要帶上來 — 前者是欄位驗證、後者是引擎錯誤碼,吞掉任一邊前端就顯示不了原因。
/// </summary>
public sealed class SkillService : ISkillService
{
    private const string FailurePrefix = "Skill 服務呼叫失敗：";

    private readonly BackendClient _backend;

    public SkillService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public async Task<IReadOnlyList<SkillInfo>> ListAsync(UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, "/api/skills", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<List<SkillInfo>>(_backend.Json, ct)
            ?? new List<SkillInfo>();
    }

    public async Task<Skill> GetAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}", ctx);
        return await ReadSkillAsync(req, ct);
    }

    public async Task<IReadOnlyList<SkillRevisionInfo>> GetRevisionsAsync(
        string name, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}/revisions", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<List<SkillRevisionInfo>>(_backend.Json, ct)
            ?? new List<SkillRevisionInfo>();
    }

    public async Task<SkillExport> ExportAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}/export", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        // 原封取回 bytes — zip 不是 JSON,不反序列化。content-type 缺則預設 application/zip。
        var content = await resp.Content.ReadAsByteArrayAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/zip";
        return new SkillExport(content, contentType, $"{name}.zip");
    }

    public async Task<Skill> CreateAsync(SkillUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Post, "/api/skills", ctx, request);
        return await ReadSkillAsync(req, ct);
    }

    public async Task<Skill> UpdateAsync(
        string name, SkillUpsert request, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Put, $"/api/skills/{name}", ctx, request);
        return await ReadSkillAsync(req, ct);
    }

    public async Task DeleteAsync(string name, UserContext ctx, CancellationToken ct = default)
    {
        using var req = _backend.BuildRequest(HttpMethod.Delete, $"/api/skills/{name}", ctx);
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }
    }

    private async Task<Skill> ReadSkillAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _backend.SendAsync(req, WrapTransport, ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw await MapErrorAsync(resp, ct);
        }

        return await resp.Content.ReadFromJsonAsync<Skill>(_backend.Json, ct)
            ?? throw new WorkflowInvocationException(FailurePrefix + "回應內容為空");
    }

    private Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(resp, _backend, FailurePrefix, ct);
}
