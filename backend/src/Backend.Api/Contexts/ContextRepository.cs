using System.Text.Json;
using Backend.Api.Skills;
using Dapper;
using Npgsql;

namespace Backend.Api.Contexts;

public sealed class ContextRepository(NpgsqlDataSource dataSource) : IContextRepository
{
    public async Task<ContextPolicyResponse?> GetActivePolicyAsync(string tenantId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<PolicyRow>(new CommandDefinition(
            "SELECT id Id,name Name,values::text Values,created_at CreatedAt,updated_at UpdatedAt FROM context_policy WHERE tenant_id=@tenantId AND is_active",
            new { tenantId }, cancellationToken: ct));
        if (row is null) return null;
        var sources = (await connection.QueryAsync<SourceRow>(new CommandDefinition(
            SourceSelect + " WHERE enabled AND (tenant_id IS NULL OR tenant_id=@tenantId) ORDER BY source_id",
            new { tenantId }, cancellationToken: ct))).Select(ToSource).ToList();
        var result = row.ToDto() with { Sources = sources };
        ReadinessEvaluator.ValidatePolicy(result.Values);
        return result;
    }

    public async Task<ContextStoredRevision> CreateRevisionAsync(string tenantId, string userId, Guid contextId, ContextRevisionSubmitRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var result = await CreateRevisionAsync(connection, transaction, tenantId, userId, contextId, request, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Shared E1/E3 write core.  The caller owns the transaction so an E3 delta,
    /// immutable revision, cursor event, and request version either commit together or not at all.</summary>
    internal async Task<ContextStoredRevision> CreateRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, string userId, Guid contextId, ContextRevisionSubmitRequest request, CancellationToken ct)
    {
        var candidateCanonical = ContextCanonicalizer.CanonicalizeDefinition(request.Definition);
        var evidence = request.Evidence ?? Array.Empty<ContextEvidenceInput>();
        ContextCanonicalizer.ValidateEvidence(evidence);
        var views = request.Views ?? Array.Empty<ContextViewInput>();
        if (views.GroupBy(x => x.ViewType, StringComparer.Ordinal).Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Count() != 1)) throw new ArgumentException("context views are invalid");

        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext(@key))", new { key = $"context:{contextId:D}" }, transaction, cancellationToken: ct));
        var existingTenant = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT tenant_id FROM context_revision WHERE context_id=@contextId LIMIT 1 FOR SHARE", new { contextId }, transaction, cancellationToken: ct));
        if (existingTenant is not null && !string.Equals(existingTenant, tenantId, StringComparison.Ordinal)) throw new ArgumentException("context_id belongs to another tenant");
        var policy = await connection.QuerySingleOrDefaultAsync<PolicyRow>(new CommandDefinition(
            "SELECT id Id,name Name,values::text Values,created_at CreatedAt,updated_at UpdatedAt FROM context_policy WHERE tenant_id=@tenantId AND is_active FOR SHARE",
            new { tenantId }, transaction, cancellationToken: ct));
        if (policy is null) throw new ContextPolicyUnavailableException();
        var policyDto = policy.ToDto();
        ReadinessEvaluator.ValidatePolicy(policyDto.Values);
        var rootSnapshot = await RootSnapshotAsync(connection, transaction, tenantId, userId, request.RootRunId, ct);
        var selected = await SelectSourceAsync(connection, transaction, tenantId, policyDto.Values, rootSnapshot, ct);
        await ValidateEvidenceAuthorityAsync(connection, transaction, tenantId, evidence, rootSnapshot, selected, ct);
        var decision = ReadinessEvaluator.Evaluate(evidence, policyDto.Values, request.Measurements, selected?.SourceId);
        if (decision.Status == ContextStatuses.ReadyWithAssumptions) ContextCanonicalizer.ValidateAssumptions(candidateCanonical, views, request.Measurements!.AssumptionsCount);
        var canonical = ContextCanonicalizer.PinAuthority(candidateCanonical, selected?.SourceId, selected?.AdapterId);
        var definition = JsonDocument.Parse(canonical).RootElement;
        var revision = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(revision),0)+1 FROM context_revision WHERE context_id=@contextId", new { contextId }, transaction, cancellationToken: ct));
        var now = DateTime.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO context_revision(context_id,revision,tenant_id,root_run_id,status,canonical,sha256,definition,as_of,readiness,policy_id,selected_source_id,adapter_id,unmet_requirements,expires_at) VALUES(@contextId,@revision,@tenantId,@rootRunId,@status,convert_to(@canonical,'UTF8'),@sha,@definition::jsonb,@asOf,@readiness,@policyId,@sourceId,@adapterId,@unmet::jsonb,@expiresAt)",
            new { contextId, revision, tenantId, rootRunId = request.RootRunId, status = decision.Status, canonical, sha = ContextCanonicalizer.Hash(canonical), definition = canonical, asOf = request.AsOf ?? now, readiness = decision.Readiness, policyId = policyDto.Id, sourceId = selected?.SourceId, adapterId = selected?.AdapterId, unmet = JsonSerializer.Serialize(decision.Unmet), expiresAt = request.ExpiresAt }, transaction, cancellationToken: ct));
        foreach (var item in evidence)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO context_evidence(evidence_id,context_id,revision,tenant_id,evidence_type,source_id,snapshot_id,content_ref,content_hash,scope,observations,acl_decision_id,observed_at,lineage) VALUES(@id,@contextId,@revision,@tenantId,@type,@source,@snapshot,@contentRef,@contentHash,@scope::jsonb,@observations::jsonb,@acl,@observedAt,@lineage::jsonb)",
                new { id = Guid.NewGuid(), contextId, revision, tenantId, type = item.EvidenceType?.Trim() ?? "document", source = item.SourceId!.Trim(), snapshot = item.SnapshotId!.Trim(), contentRef = item.ContentRef!.Trim(), contentHash = item.ContentHash!.Trim(), scope = Json(item.Scope), observations = Json(item.Observations), acl = item.AclDecisionId?.Trim(), observedAt = item.ObservedAt ?? now, lineage = Json(item.Lineage) }, transaction, cancellationToken: ct));
        }
        var storedViews = new List<ContextViewResponse>();
        foreach (var view in views)
        {
            var viewCanonical = ContextCanonicalizer.CanonicalizeView(view.Definition);
            var viewId = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO context_view(view_id,context_id,revision,tenant_id,view_type,canonical,sha256,definition) VALUES(@viewId,@contextId,@revision,@tenantId,@viewType,convert_to(@canonical,'UTF8'),@sha,@definition::jsonb)",
                new { viewId, contextId, revision, tenantId, viewType = view.ViewType!.Trim(), canonical = viewCanonical, sha = SkillHash.Sha256(viewCanonical), definition = viewCanonical }, transaction, cancellationToken: ct));
            storedViews.Add(new(viewId, contextId, revision, view.ViewType!.Trim(), Parse(viewCanonical)));
        }
        var contextRef = ContextStatuses.IsReady(decision.Status) ? ViewRef(contextId, revision, storedViews) : null;
        var response = new ContextRevisionResponse(contextId, revision, request.RootRunId, decision.Status, decision.Readiness, decision.Unmet, policyDto.Id, definition.Clone(), request.AsOf ?? now, now, request.ExpiresAt, contextRef, selected?.SourceId, selected?.AdapterId);
        return new(response, evidence, storedViews, selected?.AdapterId);
    }

    public async Task<ContextRevisionResponse?> GetRevisionAsync(string tenantId, Guid contextId, int revision, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ContextRevisionRow>(new CommandDefinition(RevisionSql + " WHERE tenant_id=@tenantId AND context_id=@contextId AND revision=@revision", new { tenantId, contextId, revision }, cancellationToken: ct));
        if (row is null) return null;
        var views = (await connection.QueryAsync<ViewRow>(new CommandDefinition(ViewSql + " WHERE tenant_id=@tenantId AND context_id=@contextId AND revision=@revision", new { tenantId, contextId, revision }, cancellationToken: ct))).Select(ToView).ToList();
        return await ToResponseAsync(connection, row, ct, views);
    }

    public async Task<ContextViewResponse?> GetViewAsync(string tenantId, Guid viewId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ViewRow>(new CommandDefinition(ViewSql + " WHERE tenant_id=@tenantId AND view_id=@viewId", new { tenantId, viewId }, cancellationToken: ct));
        return row is null ? null : ToView(row);
    }

    public async Task<ContextStoredRevision?> GetLatestReadyForRunAsync(string tenantId, string userId, Guid rootRunId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ContextRevisionRow>(new CommandDefinition(RevisionSql + " WHERE tenant_id=@tenantId AND root_run_id=@rootRunId AND status IN ('READY','READY_WITH_ASSUMPTIONS') AND EXISTS(SELECT 1 FROM orchestrator_run r WHERE r.id=context_revision.root_run_id AND r.tenant_id=@tenantId AND r.user_id=@userId) AND NOT EXISTS(SELECT 1 FROM context_request cr WHERE cr.context_id=context_revision.context_id) ORDER BY created_at DESC LIMIT 1", new { tenantId, userId, rootRunId }, cancellationToken: ct));
        if (row is null) return null;
        var views = (await connection.QueryAsync<ViewRow>(new CommandDefinition(ViewSql + " WHERE tenant_id=@tenantId AND context_id=@contextId AND revision=@revision", new { tenantId, contextId = row.ContextId, revision = row.Revision }, cancellationToken: ct))).Select(ToView).ToList();
        var evidence = (await connection.QueryAsync<EvidenceRow>(new CommandDefinition("SELECT evidence_type EvidenceType,source_id SourceId,snapshot_id SnapshotId,content_ref ContentRef,content_hash ContentHash,scope::text Scope,observations::text Observations,acl_decision_id AclDecisionId,observed_at ObservedAt,lineage::text Lineage FROM context_evidence WHERE tenant_id=@tenantId AND context_id=@contextId AND revision=@revision", new { tenantId, contextId = row.ContextId, revision = row.Revision }, cancellationToken: ct))).Select(x => x.ToInput()).ToList();
        var response = await ToResponseAsync(connection, row, ct, views);
        return new(response, evidence, views, response.AdapterId);
    }

    private static async Task<ContextRevisionResponse> ToResponseAsync(NpgsqlConnection connection, ContextRevisionRow row, CancellationToken ct, IReadOnlyList<ContextViewResponse>? loadedViews = null)
    {
        var unmet = JsonSerializer.Deserialize<List<string>>(row.UnmetRequirements ?? "[]") ?? [];
        var views = loadedViews ?? Array.Empty<ContextViewResponse>();
        var canonical = ContextCanonicalizer.ReadAuthoritative(row.Canonical, row.Sha, $"Context {row.ContextId:D} revision {row.Revision}");
        using var authorityDocument = JsonDocument.Parse(canonical);
        if (!authorityDocument.RootElement.TryGetProperty("context_authority", out var authority)
            || (authority.TryGetProperty("selected_source_id", out var source) ? source.GetString() : null) != row.SelectedSourceId
            || (authority.TryGetProperty("adapter_id", out var adapter) ? adapter.GetString() : null) != row.AdapterId)
            throw new InvalidOperationException($"Context {row.ContextId:D} revision {row.Revision} authority pin mismatch");
        return new(row.ContextId, row.Revision, row.RootRunId, row.Status, row.Readiness, unmet, row.PolicyId, Parse(canonical), row.AsOf, row.CreatedAt, row.ExpiresAt, ContextStatuses.IsReady(row.Status) ? ViewRef(row.ContextId, row.Revision, views) : null, row.SelectedSourceId, row.AdapterId);
    }

    /// <summary>Root ownership is tenant **and** caller scoped: a same-tenant peer must not be able to
    /// attach a revision to, or read authority from, another operator's root run.</summary>
    private static async Task<string?> RootSnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, string userId, Guid? rootRunId, CancellationToken ct)
    {
        if (rootRunId is null) return null;
        var row = await connection.QuerySingleOrDefaultAsync<RootAuthorityRow>(new CommandDefinition("SELECT execution_snapshot_canonical Canonical,snapshot_sha256 Sha FROM orchestrator_run WHERE id=@rootRunId AND tenant_id=@tenantId AND user_id=@userId FOR SHARE", new { rootRunId, tenantId, userId }, transaction, cancellationToken: ct));
        if (row is null) throw new ArgumentException("root_run_id is not an owned root run");
        return ContextCanonicalizer.ReadAuthoritative(row.Canonical, row.Sha, $"Root run {rootRunId:D}");
    }

    private static async Task ValidateEvidenceAuthorityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, IEnumerable<ContextEvidenceInput> evidence, string? rootSnapshot, SelectedSource? selected, CancellationToken ct)
    {
        if (!evidence.Any()) return;
        if (rootSnapshot is null) throw new ArgumentException("context evidence requires an owned root run");
        using var document = JsonDocument.Parse(rootSnapshot);
        var authority = document.RootElement.GetProperty("authority");
        var allowed = authority.GetProperty("knowledge_sources").EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        if (selected is null || selected.EvidenceType != "document") throw new ArgumentException("Context evidence adapter is unavailable or unauthorized");
        foreach (var item in evidence)
        {
            ContextCanonicalizer.ValidateEvidencePin(item, selected.SourceId, selected.AdapterId, selected.EvidenceType);
            if (!Guid.TryParseExact(item.SourceId, "D", out var documentId) || !allowed.Contains(item.SourceId!)) throw new ArgumentException("Context evidence source exceeds root snapshot authority");
            if (!TryContentRef(item.ContentRef!, out var refDocument, out var chunkId) || refDocument != documentId) throw new ArgumentException("Context evidence content_ref is invalid");
            var content = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT content FROM rag_chunks WHERE id=@chunkId AND document_id=@documentId AND tenant_id=@tenantId FOR SHARE", new { chunkId, documentId, tenantId }, transaction, cancellationToken: ct));
            if (content is null || !string.Equals(SkillHash.Sha256(content), item.ContentHash, StringComparison.Ordinal)) throw new ArgumentException("Context evidence content does not match an authorized chunk");
        }
    }

    private static async Task<SelectedSource?> SelectSourceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string tenantId, JsonElement policy, string? rootSnapshot, CancellationToken ct)
    {
        if (rootSnapshot is null) return null;
        using var root = JsonDocument.Parse(rootSnapshot);
        var tools = root.RootElement.GetProperty("authority").GetProperty("context_tools").EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = (await connection.QueryAsync<SourceRow>(new CommandDefinition(SourceSelect + " WHERE enabled AND (tenant_id IS NULL OR tenant_id=@tenantId)", new { tenantId }, transaction, cancellationToken: ct))).ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        if (!policy.TryGetProperty("source_precedence", out var precedence) || precedence.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in precedence.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String))
            if (rows.TryGetValue(item.GetString()!, out var source) && source.Enabled && tools.Contains(source.AdapterId)) return new(source.SourceId, source.EvidenceType, source.AdapterId);
        return null;
    }

    private static bool TryContentRef(string value, out Guid documentId, out Guid chunkId)
    {
        documentId = default; chunkId = default;
        const string prefix = "document://"; const string marker = "#chunk/";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var split = value.IndexOf(marker, StringComparison.Ordinal);
        return split > prefix.Length && Guid.TryParseExact(value[prefix.Length..split], "D", out documentId) && Guid.TryParseExact(value[(split + marker.Length)..], "D", out chunkId);
    }

    private static ContextRef? ViewRef(Guid contextId, int revision, IReadOnlyList<ContextViewResponse> views)
    {
        var view = views.FirstOrDefault(x => x.ViewType == "planner") ?? views.FirstOrDefault();
        return view is null ? null : new(contextId, revision, view.ViewId);
    }
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static ContextViewResponse ToView(ViewRow row) => new(row.ViewId, row.ContextId, row.Revision, row.ViewType, Parse(ContextCanonicalizer.ReadAuthoritative(row.Canonical, row.Sha, $"Context view {row.ViewId:D}")));
    private static string Json(JsonElement? element) => element is null or { ValueKind: JsonValueKind.Undefined } ? "null" : element.Value.GetRawText();
    private const string RevisionSql = "SELECT context_id ContextId,revision Revision,root_run_id RootRunId,status Status,readiness Readiness,policy_id PolicyId,selected_source_id SelectedSourceId,adapter_id AdapterId,canonical Canonical,sha256 Sha,unmet_requirements::text UnmetRequirements,as_of AsOf,created_at CreatedAt,expires_at ExpiresAt FROM context_revision";
    private const string ViewSql = "SELECT view_id ViewId,context_id ContextId,revision Revision,view_type ViewType,canonical Canonical,sha256 Sha FROM context_view";
    private sealed record PolicyRow(Guid Id, string Name, string Values, DateTime CreatedAt, DateTime UpdatedAt) { public ContextPolicyResponse ToDto() => new(Id, Name, Parse(Values), CreatedAt, UpdatedAt); }
    private const string SourceSelect = "SELECT source_id SourceId,evidence_type EvidenceType,source_type SourceType,authority_class AuthorityClass,adapter_id AdapterId,enabled Enabled,required Required,timeout_seconds TimeoutSeconds,minimum_deadline_seconds MinimumDeadlineSeconds FROM source_catalog";
    private static ContextSourceCatalogEntry ToSource(SourceRow x)
    { if (x.TimeoutSeconds < 1 || x.MinimumDeadlineSeconds < 0) throw new ContextPolicyInvalidException("Context source timing policy is invalid"); return new(x.SourceId, x.EvidenceType, x.SourceType, x.AuthorityClass, x.AdapterId, x.Enabled, x.Required, x.TimeoutSeconds, x.MinimumDeadlineSeconds); }
    private sealed record SourceRow(string SourceId, string EvidenceType, string SourceType, string AuthorityClass, string AdapterId, bool Enabled, bool Required, int TimeoutSeconds, int MinimumDeadlineSeconds);
    private sealed record ViewRow(Guid ViewId, Guid ContextId, int Revision, string ViewType, byte[]? Canonical, string? Sha);
    private sealed record EvidenceRow(string? EvidenceType, string? SourceId, string? SnapshotId, string? ContentRef, string? ContentHash, string? Scope, string? Observations, string? AclDecisionId, DateTime? ObservedAt, string? Lineage)
    { public ContextEvidenceInput ToInput() => new(EvidenceType, SourceId, SnapshotId, ContentRef, ContentHash, ParseNullable(Scope), ParseNullable(Observations), AclDecisionId, ObservedAt, ParseNullable(Lineage)); }
    private static JsonElement? ParseNullable(string? json) => string.IsNullOrWhiteSpace(json) || json == "null" ? null : Parse(json);
    private sealed record RootAuthorityRow(byte[]? Canonical, string? Sha);
    private sealed record SelectedSource(string SourceId, string EvidenceType, string AdapterId);
}

public sealed class ContextPolicyUnavailableException : Exception;

/// <summary>Property mapping avoids Dapper constructor matching nullable PostgreSQL UUID/timestamp columns as non-nullable reader types.</summary>
internal sealed class ContextRevisionRow
{
    public Guid ContextId { get; set; }
    public int Revision { get; set; }
    public Guid? RootRunId { get; set; }
    public string Status { get; set; } = null!;
    public decimal Readiness { get; set; }
    public Guid PolicyId { get; set; }
    public string? SelectedSourceId { get; set; }
    public string? AdapterId { get; set; }
    public byte[]? Canonical { get; set; }
    public string? Sha { get; set; }
    public string? UnmetRequirements { get; set; }
    public DateTime AsOf { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
