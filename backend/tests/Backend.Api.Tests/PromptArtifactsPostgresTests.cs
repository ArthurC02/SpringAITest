using Backend.Api.Agents;
using Backend.Api.PromptArtifacts;
using Backend.Api.Skills;
using Dapper;

namespace Backend.Api.Tests;

/// <summary>
/// P1 prompt artifacts against the **real Dapper + Postgres** repository: the SQL a hand-written
/// fake cannot back (content projection that never selects the protected column, revision
/// idempotency under the real unique constraint, SHA lookup index, and the published Agent snapshot
/// actually carrying the manifest pin columns). Behavioural coverage lives in the InMemory-backed
/// <see cref="PromptArtifactsApiTests"/>; isolation here is tenant prefixing (shared springaitest db).
/// </summary>
[Collection("Postgres")]
public sealed class PromptArtifactsPostgresTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string TenantPrefix = "promptart-";

    private PromptArtifactRepository Repo => new(fixture.DataSource!);

    public Task InitializeAsync() => CleanupAsync();

    public Task DisposeAsync() => CleanupAsync();

    [SkippableFact]
    public async Task ComponentRevisions_AreShaIdempotent_Immutable_AndTenantIsolated()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "components";
        var other = TenantPrefix + "components-other";

        var first = await Repo.PublishComponentAsync(tenant, "guard", "GUARD-V1", "tester", default);
        var replay = await Repo.PublishComponentAsync(tenant, "guard", "GUARD-V1", "tester", default);
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, replay.Revision);
        Assert.Equal(first.ContentSha256, replay.ContentSha256);

        var second = await Repo.PublishComponentAsync(tenant, "guard", "GUARD-V2-LONGER", "tester", default);
        Assert.Equal(2, second.Revision);

        // Immutable: revision 1 still hashes its original content, and reads project length, not text.
        var rev1 = await Repo.GetComponentAsync(tenant, "guard", 1, default);
        Assert.Equal(SkillHash.Sha256("GUARD-V1"), rev1!.ContentSha256);
        Assert.Equal("GUARD-V1".Length, rev1.ContentLength);

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM prompt_component_revision WHERE tenant_id=@tenant AND kind='guard'",
                new { tenant }));
        }

        // Cross-tenant: same kind/revision number is a different, invisible artifact.
        Assert.Null(await Repo.GetComponentAsync(other, "guard", 1, default));
        Assert.Empty(await Repo.ListComponentsAsync(other, default));
    }

    [SkippableFact]
    public async Task ManifestRevisions_LookUpBySha_AndRollbackAddsANewRevision()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "manifests";
        await Repo.PublishComponentAsync(tenant, "routing", "ROUTING-V1", "tester", default);
        var canonicalA = Canonical(("routing", 1));
        var canonicalB = Canonical(("routing", 2));

        var first = await Repo.CreateManifestAsync(tenant, canonicalA, "tester", default);
        var replay = await Repo.CreateManifestAsync(tenant, canonicalA, "tester", default);
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, replay.Revision);

        var second = await Repo.CreateManifestAsync(tenant, canonicalB, "tester", default);
        Assert.Equal(2, second.Revision);

        // Rollback: same canonical content as revision 1 becomes a NEW revision 3; history is intact.
        var rolledBack = await Repo.CreateManifestAsync(tenant, canonicalA, "tester", default);
        Assert.Equal(3, rolledBack.Revision);
        Assert.Equal(first.ManifestSha256, rolledBack.ManifestSha256);
        Assert.Equal(canonicalA, (await Repo.GetManifestAsync(tenant, 1, default))!.ManifestCanonical);
        Assert.Equal(canonicalB, (await Repo.GetManifestAsync(tenant, 2, default))!.ManifestCanonical);

        // SHA lookup resolves to the live (newest) revision holding those bytes; cross-tenant → null.
        Assert.Equal(3, (await Repo.FindManifestBySha256Async(tenant, first.ManifestSha256, default))!.Revision);
        Assert.Null(await Repo.FindManifestBySha256Async(TenantPrefix + "manifests-other", first.ManifestSha256, default));
    }

    [SkippableFact]
    public async Task AgentPublish_PersistsManifestPin_AndNeverDrifts()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "pin";
        var agents = new AgentRepository(fixture.DataSource!);
        await Repo.PublishComponentAsync(tenant, "persona", "PERSONA-V1", "tester", default);
        var manifest = await Repo.CreateManifestAsync(tenant, Canonical(("persona", 1)), "tester", default);

        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            null, null, null, "你是研究助手", ["worker"], null, null, null, null, null, null, null, null,
            new AgentWorkflowRef(AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));
        var sha = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync(tenant, "pinned", "n", "d", definition, sha, "author", default);
        Assert.True(await agents.MarkValidatedAsync(tenant, agent!.Id, agent.DraftVersion, definition, sha, default));

        var published = await agents.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, definition, sha, "publisher", default,
            new PromptManifestPin(manifest.Revision, manifest.ManifestSha256));
        Assert.Equal(AgentWriteStatus.Success, published.Status);

        var revision = Assert.Single(await agents.ListRevisionsAsync(tenant, agent.Id, default));
        Assert.Equal(manifest.Revision, revision.PromptManifestRevision);
        Assert.Equal(manifest.ManifestSha256, revision.PromptManifestSha256);

        // A newer manifest revision must not move the already published snapshot.
        await Repo.PublishComponentAsync(tenant, "persona", "PERSONA-V2", "tester", default);
        Assert.Equal(2, (await Repo.CreateManifestAsync(tenant, Canonical(("persona", 2)), "tester", default)).Revision);
        var reread = Assert.Single(await agents.ListRevisionsAsync(tenant, agent.Id, default));
        Assert.Equal(1, reread.PromptManifestRevision);
        Assert.Equal(manifest.ManifestSha256, reread.PromptManifestSha256);
    }

    private static string Canonical(params (string Kind, int Revision)[] components)
        => PromptManifestCanonicalizer.Canonicalize(
            PromptArtifactContract.SchemaVersion,
            components.ToDictionary(c => c.Kind, c => c.Revision),
            "tool-hash",
            "skill-hash");

    private async Task CleanupAsync()
    {
        if (!fixture.Available) return;
        await using var connection = await fixture.DataSource!.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            DELETE FROM agent_revision WHERE agent_id IN (SELECT id FROM agent WHERE tenant_id LIKE @prefix);
            DELETE FROM agent WHERE tenant_id LIKE @prefix;
            DELETE FROM prompt_component_revision WHERE tenant_id LIKE @prefix;
            DELETE FROM prompt_manifest_revision WHERE tenant_id LIKE @prefix;
            """,
            new { prefix = TenantPrefix + "%" });
    }
}
