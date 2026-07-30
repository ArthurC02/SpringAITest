using Backend.Api.Skills;
using Dapper;
using Npgsql;

namespace Backend.Api.PromptArtifacts;

/// <summary>
/// PostgreSQL implementation. Reads never project the protected <c>content</c> column — only its
/// digest and length, so no query path can accidentally hand raw prompt text to a DTO.
/// </summary>
public sealed class PromptArtifactRepository(NpgsqlDataSource dataSource) : IPromptArtifactRepository
{
    private const string ComponentSelect =
        "SELECT kind AS Kind, revision AS Revision, content_sha256 AS ContentSha256,"
        + " length(content) AS ContentLength, created_by AS CreatedBy, created_at AS CreatedAt"
        + " FROM prompt_component_revision";

    private const string ManifestSelect =
        "SELECT revision AS Revision, manifest_canonical AS ManifestCanonical,"
        + " manifest_sha256 AS ManifestSha256, created_by AS CreatedBy, created_at AS CreatedAt"
        + " FROM prompt_manifest_revision";

    public async Task<PromptComponentRecord> PublishComponentAsync(
        string tenantId, string kind, string content, string createdBy, CancellationToken ct)
    {
        var sha = SkillHash.Sha256(content);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // ponytail: FOR UPDATE locks existing rows only, so two concurrent *first* publishes for the
        // same (tenant, kind) can both read "no current row" and race to revision 1 -- the unique
        // constraint on (tenant_id, kind, revision) backstops that (one INSERT wins, the other gets
        // a 500, never two silently-diverging rows). Upgrade path if that first-publish race window
        // needs a clean 409 instead of a 500: an advisory lock keyed on (tenant, kind) before the read.
        var current = await conn.QuerySingleOrDefaultAsync<PromptComponentRecord>(new CommandDefinition(
            ComponentSelect + " WHERE tenant_id=@tenantId AND kind=@kind"
            + " ORDER BY revision DESC LIMIT 1 FOR UPDATE",
            new { tenantId, kind }, tx, cancellationToken: ct));
        if (current is not null && string.Equals(current.ContentSha256, sha, StringComparison.Ordinal))
        {
            // Identical content republished: idempotent no-op, no new revision.
            await tx.CommitAsync(ct);
            return current;
        }

        var revision = (current?.Revision ?? 0) + 1;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO prompt_component_revision(tenant_id, kind, revision, content, content_sha256, created_by)"
            + " VALUES(@tenantId, @kind, @revision, @content, @sha, @createdBy)",
            new { tenantId, kind, revision, content, sha, createdBy }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new PromptComponentRecord(kind, revision, sha, content.Length, createdBy, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<PromptComponentRecord>> ListComponentsAsync(
        string tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PromptComponentRecord>(new CommandDefinition(
            ComponentSelect + " WHERE tenant_id=@tenantId ORDER BY kind, revision DESC",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<PromptComponentRecord?> GetComponentAsync(
        string tenantId, string kind, int revision, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PromptComponentRecord>(new CommandDefinition(
            ComponentSelect + " WHERE tenant_id=@tenantId AND kind=@kind AND revision=@revision",
            new { tenantId, kind, revision }, cancellationToken: ct));
    }

    public async Task<(string Content, string ContentSha256)?> GetComponentContentAsync(
        string tenantId, string kind, int revision, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ComponentContentRow>(new CommandDefinition(
            "SELECT content AS Content, content_sha256 AS ContentSha256 FROM prompt_component_revision"
            + " WHERE tenant_id=@tenantId AND kind=@kind AND revision=@revision",
            new { tenantId, kind, revision }, cancellationToken: ct));
        return row is null ? null : (row.Content, row.ContentSha256);
    }

    private sealed record ComponentContentRow(string Content, string ContentSha256);

    public async Task<PromptManifestRecord> CreateManifestAsync(
        string tenantId, string manifestCanonical, string createdBy, CancellationToken ct)
    {
        var sha = SkillHash.Sha256(manifestCanonical);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // ponytail: same first-publish race window as PublishComponentAsync above -- FOR UPDATE has
        // no existing row to lock on a tenant's very first manifest, so two concurrent first
        // publishes can both race to revision 1; uq_prompt_manifest_revision(tenant_id, revision)
        // backstops it (500 on the loser, never a silent duplicate/divergent row).
        var current = await conn.QuerySingleOrDefaultAsync<PromptManifestRecord>(new CommandDefinition(
            ManifestSelect + " WHERE tenant_id=@tenantId ORDER BY revision DESC LIMIT 1 FOR UPDATE",
            new { tenantId }, tx, cancellationToken: ct));
        if (current is not null && string.Equals(current.ManifestSha256, sha, StringComparison.Ordinal))
        {
            await tx.CommitAsync(ct);
            return current;
        }

        var revision = (current?.Revision ?? 0) + 1;
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO prompt_manifest_revision(tenant_id, revision, manifest_canonical, manifest_sha256, created_by)"
            + " VALUES(@tenantId, @revision, @manifestCanonical, @sha, @createdBy)",
            new { tenantId, revision, manifestCanonical, sha, createdBy }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new PromptManifestRecord(revision, manifestCanonical, sha, createdBy, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<PromptManifestRecord>> ListManifestsAsync(
        string tenantId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<PromptManifestRecord>(new CommandDefinition(
            ManifestSelect + " WHERE tenant_id=@tenantId ORDER BY revision DESC",
            new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<PromptManifestRecord?> GetManifestAsync(
        string tenantId, int revision, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PromptManifestRecord>(new CommandDefinition(
            ManifestSelect + " WHERE tenant_id=@tenantId AND revision=@revision",
            new { tenantId, revision }, cancellationToken: ct));
    }

    public async Task<PromptManifestRecord?> FindManifestBySha256Async(
        string tenantId, string manifestSha256, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        // Same content can only ever be stored once per tenant while it stays the current revision,
        // but a rollback can re-create it as a later revision -- the newest match is the live one.
        return await conn.QuerySingleOrDefaultAsync<PromptManifestRecord>(new CommandDefinition(
            ManifestSelect + " WHERE tenant_id=@tenantId AND manifest_sha256=@manifestSha256"
            + " ORDER BY revision DESC LIMIT 1",
            new { tenantId, manifestSha256 }, cancellationToken: ct));
    }
}
