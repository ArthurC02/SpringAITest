using System.Net.Http.Headers;
using System.Text.Json;
using Platform.Service.Abstractions;
using Platform.Service.Dtos;
using Platform.Service.Exceptions;

namespace Platform.Service;

/// <summary>
/// Skill CRUD 服務:代理 backend /api/skills,不含任何商業邏輯(角色/唯一性/定義驗證全在 backend)。
/// backend 的錯誤原樣轉發:400/403/404/409/422 各自映射到對外同狀態碼的例外並沿用 backend 的 message;
/// 其餘(5xx、傳輸失敗)→ WorkflowInvocationException(對外 502)。
/// 400 與 422 的 fieldErrors 都要帶上來 — 前者是欄位驗證、後者是引擎錯誤碼,吞掉任一邊前端就顯示不了原因。
/// 讀取端點(list/get/revisions)原樣穿透 backend JSON(不套 DTO),避免 backend 新增欄位被靜默吃掉。
/// </summary>
public sealed class SkillService : ISkillService
{
    private const string FailurePrefix = "Skill 服務呼叫失敗：";

    // 匯入套件的傳輸層上限:Web 層 IFormFile 已把上傳有界緩衝(受 ASP.NET 表單長度限制),
    // 這裡在轉送前再擋一次明顯過大的 body(受控 400),不把巨大套件推給 backend。
    // 真正的 archive 限制(~4 MiB 解壓)在 workflow parser;這裡取其上方的寬鬆值。
    private const int MaxImportBytes = 16 * 1024 * 1024; // 16 MiB

    private readonly BackendClient _backend;

    public SkillService(BackendClient backend) => _backend = backend;

    private Exception WrapTransport(Exception ex) => new WorkflowInvocationException(FailurePrefix + ex.Message, ex);

    public Task<JsonElement> ListAsync(UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(HttpMethod.Get, "/api/skills", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<JsonElement> GetAsync(string name, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<JsonElement> GetRevisionsAsync(string name, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(HttpMethod.Get, $"/api/skills/{name}/revisions", ctx), WrapTransport, MapErrorAsync, ct);

    public Task<JsonElement> RestoreRevisionAsync(
        string name, int revision, UserContext ctx, CancellationToken ct = default)
        => _backend.SendForJsonElementAsync(
            _backend.BuildRequest(
                HttpMethod.Post, $"/api/skills/{name}/revisions/{revision}/restore", ctx),
            WrapTransport, MapErrorAsync, ct);

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

    public async Task<JsonElement> ImportAsync(
        string name, byte[] package, string fileName, UserContext ctx, CancellationToken ct = default)
        => await ImportCoreAsync(
            $"/api/skills/{name}/import", package, fileName, ctx, ct);

    public async Task<JsonElement> ImportAsync(
        byte[] package, string fileName, UserContext ctx, CancellationToken ct = default)
        => await ImportCoreAsync("/api/skills/import", package, fileName, ctx, ct);

    private async Task<JsonElement> ImportCoreAsync(
        string path, byte[] package, string fileName, UserContext ctx, CancellationToken ct)
    {
        if (package.Length > MaxImportBytes)
        {
            throw new WorkflowBadInputException(FailurePrefix + $"匯入套件超過上限 {MaxImportBytes} bytes");
        }

        var req = _backend.BuildRequest(HttpMethod.Post, path, ctx);

        // 用上傳的位元組重建乾淨的 multipart:MultipartFormDataContent 自帶 Content-Type(新 boundary)+ Content-Length
        //(非 chunked),backend 以 form-binding 讀 Request.Form.Files["package"]。以新 boundary 重新編碼是合法 HTTP —
        // backend 只需要一個有效的 package 檔位,不需要沿用來源 boundary。
        var multipart = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(package);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(filePart, "package", string.IsNullOrWhiteSpace(fileName) ? "package.zip" : fileName);
        req.Content = multipart;

        // 回應原樣穿透:import 回的是 Skill JSON(含 additive kind);錯誤走 export 同一條 MapErrorAsync。
        return await _backend.SendForJsonElementAsync(req, WrapTransport, MapErrorAsync, ct);
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
