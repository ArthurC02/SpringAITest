using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Workflows;

/// <summary>
/// Strict internal client for Workflow's sole Graph compiler authority.
/// 出站樣板(身分 header、HTTP/1.1 pin、任何故障 → 502)沿用 <see cref="InternalWorkflowClient"/>:
/// 編譯器不可達永遠 fail closed,不會有例外逃逸成 500。
/// </summary>
public sealed class WorkflowDesignerCompiler(HttpClient client, string baseUrl, string token, IHttpContextAccessor context) : IWorkflowCompiler
{
    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    public async Task<object> CatalogAsync(string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Get, "/workflow-designer/catalog/nodes", tenantId);
        return await Send<JsonElement>(req, ct);
    }
    public async Task<IReadOnlyList<WorkflowToolInfo>> ToolsAsync(string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Get, "/tools", tenantId);
        return await Send<List<WorkflowToolInfo>>(req, ct);
    }
    public async Task<CompilerValidationResult> ValidateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Post, "/workflow-designer/validate", tenantId);
        req.Content = JsonContent.Create(new { definition = JsonDocument.Parse(definition).RootElement, ui_metadata = JsonDocument.Parse(uiMetadata).RootElement });
        var body = await Send<Body>(req, ct);
        var errors = body.Errors?.Select(x => new WorkflowValidationError(x.Path ?? "definition", x.Message ?? "validation failed", x.NodeId, x.EdgeId)).ToArray() ?? [];
        if (body.CompilerContractVersion != WorkflowCompilerContracts.Current) throw Unavailable();
        if (!body.Valid) return new(definition, uiMetadata, body.CompilerContractVersion!, errors);
        if (body.CanonicalDefinition is not JsonElement graph || body.CanonicalUiMetadata is not JsonElement ui) throw Unavailable();
        return new(graph.GetRawText(), ui.GetRawText(), body.CompilerContractVersion, errors);
    }
    public async Task<object> SimulateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Post, "/workflow-designer/simulate", tenantId);
        req.Content = JsonContent.Create(new { definition = JsonDocument.Parse(definition).RootElement, ui_metadata = JsonDocument.Parse(uiMetadata).RootElement });
        return await Send<JsonElement>(req, ct);
    }
    private HttpRequestMessage Create(HttpMethod method, string path, string tenant) { var request = context.HttpContext?.Request; var user = request?.UserIdOrEmpty() ?? string.Empty; var role = request?.UserRole() ?? string.Empty; if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(role)) throw Unavailable(); var r = new HttpRequestMessage(method, _baseUrl + path); r.UseInternalIdentity(token, tenant, user, role); return r; }
    private Task<T> Send<T>(HttpRequestMessage r, CancellationToken ct) => client.SendJsonAsync<T>(r, _ => Unavailable(), null, ct);
    private static ApiException Unavailable() => new(StatusCodes.Status502BadGateway, "Workflow designer compiler rejected or was unavailable");
    private sealed record Body([property: JsonPropertyName("valid")] bool Valid, [property: JsonPropertyName("canonicalDefinition")] JsonElement? CanonicalDefinition, [property: JsonPropertyName("canonicalUiMetadata")] JsonElement? CanonicalUiMetadata, [property: JsonPropertyName("compilerContractVersion")] string? CompilerContractVersion, [property: JsonPropertyName("errors")] IReadOnlyList<Error>? Errors);
    private sealed record Error([property: JsonPropertyName("path")] string? Path, [property: JsonPropertyName("message")] string? Message, [property: JsonPropertyName("nodeId")] string? NodeId, [property: JsonPropertyName("edgeId")] string? EdgeId);
}
