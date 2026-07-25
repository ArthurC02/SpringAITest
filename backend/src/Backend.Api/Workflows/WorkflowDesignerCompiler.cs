using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Api.Common;

namespace Backend.Api.Workflows;

/// <summary>Strict internal client for Workflow's sole Graph compiler authority.</summary>
public sealed class WorkflowDesignerCompiler(HttpClient client, string baseUrl, string token, IHttpContextAccessor context) : IWorkflowCompiler
{
    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    public async Task<object> CatalogAsync(string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Get, "/workflow-designer/catalog/nodes", tenantId);
        using var res = await Send(req, ct); Ensure(res); return await Read<JsonElement>(res, ct);
    }
    public async Task<IReadOnlyList<WorkflowToolInfo>> ToolsAsync(string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Get, "/tools", tenantId);
        using var res = await Send(req, ct); Ensure(res);
        return await Read<List<WorkflowToolInfo>>(res, ct) ?? throw Unavailable();
    }
    public async Task<CompilerValidationResult> ValidateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct)
    {
        using var req = Create(HttpMethod.Post, "/workflow-designer/validate", tenantId);
        req.Content = JsonContent.Create(new { definition = JsonDocument.Parse(definition).RootElement, ui_metadata = JsonDocument.Parse(uiMetadata).RootElement });
        using var res = await Send(req, ct); Ensure(res);
        var body = await Read<Body>(res, ct) ?? throw Unavailable();
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
        using var res = await Send(req, ct); Ensure(res); return await Read<JsonElement>(res, ct);
    }
    private HttpRequestMessage Create(HttpMethod method, string path, string tenant) { var request = context.HttpContext?.Request; var user = request?.UserIdOrEmpty() ?? string.Empty; var role = request?.UserRole() ?? string.Empty; if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(role)) throw Unavailable(); var r = new HttpRequestMessage(method, _baseUrl + path); r.Headers.Add("X-Internal-Token", token); r.Headers.Add(IdentityHeaders.TenantHeader, tenant); r.Headers.Add(IdentityHeaders.UserHeader, user); r.Headers.Add(IdentityHeaders.RoleHeader, role); return r; }
    private static void Ensure(HttpResponseMessage r) { if (!r.IsSuccessStatusCode) throw Unavailable(); }
    private static async Task<HttpResponseMessage> SendCore(HttpClient c, HttpRequestMessage r, CancellationToken ct) { try { return await c.SendAsync(r, ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { throw Unavailable(); } }
    private Task<HttpResponseMessage> Send(HttpRequestMessage r, CancellationToken ct) => SendCore(client, r, ct);
    private static async Task<T?> Read<T>(HttpResponseMessage response, CancellationToken ct) { try { return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct); } catch (Exception ex) when (ex is JsonException or NotSupportedException) { throw Unavailable(); } }
    private static ApiException Unavailable() => new(StatusCodes.Status502BadGateway, "Workflow designer compiler rejected or was unavailable");
    private sealed record Body([property: JsonPropertyName("valid")] bool Valid, [property: JsonPropertyName("canonicalDefinition")] JsonElement? CanonicalDefinition, [property: JsonPropertyName("canonicalUiMetadata")] JsonElement? CanonicalUiMetadata, [property: JsonPropertyName("compilerContractVersion")] string? CompilerContractVersion, [property: JsonPropertyName("errors")] IReadOnlyList<Error>? Errors);
    private sealed record Error([property: JsonPropertyName("path")] string? Path, [property: JsonPropertyName("code")] string? Code, [property: JsonPropertyName("message")] string? Message, [property: JsonPropertyName("nodeId")] string? NodeId, [property: JsonPropertyName("edgeId")] string? EdgeId);
}
