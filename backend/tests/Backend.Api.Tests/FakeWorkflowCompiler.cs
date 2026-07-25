using Backend.Api.Workflows;

namespace Backend.Api.Tests;

public sealed class FakeWorkflowCompiler : IWorkflowCompiler
{
    public IReadOnlyList<WorkflowToolInfo> Tools { get; set; } =
    [
        new("search_documents","read"),
        new("delete_document","privileged")
    ];
    public Task<object> CatalogAsync(string tenantId, CancellationToken ct) => Task.FromResult<object>(new { nodes = Array.Empty<object>() });
    public Task<IReadOnlyList<WorkflowToolInfo>> ToolsAsync(string tenantId, CancellationToken ct) => Task.FromResult(Tools);
    public Task<CompilerValidationResult> ValidateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct) => Task.FromResult(new CompilerValidationResult(WorkflowCanonicalizer.Canonicalize(definition), WorkflowCanonicalizer.Canonicalize(uiMetadata), WorkflowCompilerContracts.Current, []));
    public Task<object> SimulateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct) => Task.FromResult<object>(new { valid = true, trace = Array.Empty<object>() });
}
