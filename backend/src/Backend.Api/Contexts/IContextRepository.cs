namespace Backend.Api.Contexts;

public interface IContextRepository
{
    Task<ContextPolicyResponse?> GetActivePolicyAsync(string tenantId, CancellationToken ct);
    Task<ContextStoredRevision> CreateRevisionAsync(string tenantId, string userId, Guid contextId, ContextRevisionSubmitRequest request, CancellationToken ct);
    Task<ContextRevisionResponse?> GetRevisionAsync(string tenantId, Guid contextId, int revision, CancellationToken ct);
    Task<ContextViewResponse?> GetViewAsync(string tenantId, Guid viewId, CancellationToken ct);
    Task<ContextStoredRevision?> GetLatestReadyForRunAsync(string tenantId, string userId, Guid rootRunId, CancellationToken ct);
}

public interface IContextAuthorityRegistry
{
    void RegisterRoot(string tenantId, string userId, Guid rootRunId, string canonicalSnapshot);
}

/// <summary>Separates root-ready context from task-local revision streams in lite mode.</summary>
public interface IContextTaskLocalRegistry
{
    void RegisterTaskContext(string tenantId, Guid rootRunId, Guid contextId);
}
