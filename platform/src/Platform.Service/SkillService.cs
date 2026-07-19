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
        => await _backend.SendForJsonListAsync<SkillInfo>(
            _backend.BuildRequest(HttpMethod.Get, "/api/skills", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<Skill> GetAsync(string name, UserContext ctx, CancellationToken ct = default)
        => ReadSkillAsync(_backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}", ctx), ct);

    public async Task<IReadOnlyList<SkillRevisionInfo>> GetRevisionsAsync(
        string name, UserContext ctx, CancellationToken ct = default)
        => await _backend.SendForJsonListAsync<SkillRevisionInfo>(
            _backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}/revisions", ctx), WrapTransport, MapErrorAsync, ct);

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

    public Task<Skill> CreateAsync(SkillUpsert request, UserContext ctx, CancellationToken ct = default)
        => ReadSkillAsync(_backend.BuildRequest(HttpMethod.Post, "/api/skills", ctx, request), ct);

    public Task<Skill> UpdateAsync(
        string name, SkillUpsert request, UserContext ctx, CancellationToken ct = default)
        => ReadSkillAsync(_backend.BuildRequest(HttpMethod.Put, $"/api/skills/{name}", ctx, request), ct);

    public Task DeleteAsync(string name, UserContext ctx, CancellationToken ct = default)
        => _backend.SendExpectSuccessAsync(
            _backend.BuildRequest(HttpMethod.Delete, $"/api/skills/{name}", ctx), WrapTransport, MapErrorAsync, ct);

    private Task<Skill> ReadSkillAsync(HttpRequestMessage req, CancellationToken ct)
        => _backend.SendForJsonAsync<Skill>(
            req, WrapTransport, MapErrorAsync,
            () => new WorkflowInvocationException(FailurePrefix + "回應內容為空"), ct);

    private Task<Exception> MapErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => BackendErrorMapper.MapErrorAsync(resp, _backend, FailurePrefix, ct);
}
