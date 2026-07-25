namespace Backend.Api.Workflows;

public interface IWorkflowRepository
{
    Task<IReadOnlyList<WorkflowInfo>> ListAsync(string tenantId, CancellationToken ct);
    Task<Workflow?> GetAsync(string tenantId, Guid id, CancellationToken ct);
    Task<WorkflowWriteResult> CreateAsync(string tenantId, string name, string kind, string definition, string uiMetadata, string createdBy, CancellationToken ct);
    Task<WorkflowWriteResult> UpdateDraftAsync(string tenantId, Guid id, long expectedVersion, string name, string definition, string uiMetadata, CancellationToken ct);
    Task<bool> MarkValidatedAsync(string tenantId, Guid id, long version, string definition, string uiMetadata, CancellationToken ct);
    Task<WorkflowWriteResult> PublishAsync(string tenantId, Guid id, long expectedVersion, string definition, string uiMetadata, string compilerContractVersion, string createdBy, CancellationToken ct);
    Task<IReadOnlyList<WorkflowRevisionInfo>> ListRevisionsAsync(string tenantId, Guid id, CancellationToken ct);
    Task<(string Definition, string UiMetadata)?> GetRevisionAsync(string tenantId, Guid id, int revision, CancellationToken ct);
    Task<WorkflowWriteResult> RestoreAsync(string tenantId, Guid id, int revision, string definition, string uiMetadata, string compilerContractVersion, string createdBy, CancellationToken ct);
    Task<bool> SetEnabledAsync(string tenantId, Guid id, bool enabled, CancellationToken ct);
}

public interface IWorkflowCompiler
{
    Task<object> CatalogAsync(string tenantId, CancellationToken ct);
    Task<IReadOnlyList<WorkflowToolInfo>> ToolsAsync(string tenantId, CancellationToken ct);
    Task<CompilerValidationResult> ValidateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct);
    Task<object> SimulateAsync(string kind, string definition, string uiMetadata, string tenantId, CancellationToken ct);
}
