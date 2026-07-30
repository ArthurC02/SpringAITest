namespace Backend.Api.PromptArtifacts;

/// <summary>
/// Durable prompt component/manifest authority (P1). Every query is tenant-scoped — tenant
/// isolation is this interface's contract, so a cross-tenant reference is simply "not found" and
/// fails closed at the caller. Nothing here ever mutates or deletes a written revision.
/// </summary>
public interface IPromptArtifactRepository
{
    /// <summary>
    /// Publish a component revision. Identical content to the kind's current revision is an
    /// idempotent no-op that returns that revision; different content always creates the next one.
    /// </summary>
    Task<PromptComponentRecord> PublishComponentAsync(
        string tenantId, string kind, string content, string createdBy, CancellationToken ct);

    Task<IReadOnlyList<PromptComponentRecord>> ListComponentsAsync(string tenantId, CancellationToken ct);

    /// <summary>Not found (including cross-tenant) → null.</summary>
    Task<PromptComponentRecord?> GetComponentAsync(
        string tenantId, string kind, int revision, CancellationToken ct);

    /// <summary>
    /// Raw component text plus its stored digest. The sole caller is the resolved-manifest route
    /// (service-to-service prompt composition) — every other read path in this repository
    /// deliberately omits content. The caller re-verifies <c>ContentSha256</c> against the returned
    /// <c>Content</c> before use (fail closed on a mismatch — the one artifact-read path in this
    /// service that used to skip that check). Not found (including cross-tenant) → null.
    /// </summary>
    Task<(string Content, string ContentSha256)?> GetComponentContentAsync(
        string tenantId, string kind, int revision, CancellationToken ct);

    /// <summary>
    /// Create a manifest revision from already-canonicalized text (same idempotency rule as
    /// components). Callers resolve and verify every referenced component revision first.
    /// </summary>
    Task<PromptManifestRecord> CreateManifestAsync(
        string tenantId, string manifestCanonical, string createdBy, CancellationToken ct);

    Task<IReadOnlyList<PromptManifestRecord>> ListManifestsAsync(string tenantId, CancellationToken ct);

    /// <summary>Not found (including cross-tenant) → null.</summary>
    Task<PromptManifestRecord?> GetManifestAsync(string tenantId, int revision, CancellationToken ct);

    /// <summary>Lookup by canonical SHA (shadow-comparison reconciliation). Not found → null.</summary>
    Task<PromptManifestRecord?> FindManifestBySha256Async(
        string tenantId, string manifestSha256, CancellationToken ct);
}
