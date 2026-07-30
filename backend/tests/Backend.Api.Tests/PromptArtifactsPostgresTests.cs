using System.Net;
using Backend.Api.Agents;
using Backend.Api.PromptArtifacts;
using Backend.Api.Skills;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task ComponentContent_IsProjectedOnlyByGetComponentContentAsync_AndTenantIsolated()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "content";
        var other = TenantPrefix + "content-other";
        await Repo.PublishComponentAsync(tenant, "governance_frame", "GOVERNANCE-SECRET", "tester", default);

        var (content, sha) = (await Repo.GetComponentContentAsync(tenant, "governance_frame", 1, default))!.Value;
        Assert.Equal("GOVERNANCE-SECRET", content);
        Assert.Equal(SkillHash.Sha256("GOVERNANCE-SECRET"), sha);
        Assert.Null(await Repo.GetComponentContentAsync(other, "governance_frame", 1, default));
        Assert.Null(await Repo.GetComponentContentAsync(tenant, "governance_frame", 2, default));
    }

    /// <summary>
    /// backend/AGENTS.md 中1 修復:resolved 路由現在讀取時重驗庫存 content 的 SHA-256,竄改(或損毀)過
    /// 的庫存內容必須 fail closed(500),絕不能把被動過手腳的文字原樣送給 Platform/Workflow 的組裝器。
    /// 直接以 SQL UPDATE 繞過應用層竄改,模擬資料被動過手腳或儲存層損毀。
    /// </summary>
    [SkippableFact]
    public async Task ResolvedRoute_FailsClosed_WhenStoredComponentContentIsTampered()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "tamper-component";
        await Repo.PublishComponentAsync(tenant, "guard", "GUARD-ORIGINAL", "tester", default);
        var manifest = await Repo.CreateManifestAsync(tenant, Canonical(("guard", 1)), "tester", default);

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE prompt_component_revision SET content=@content"
                + " WHERE tenant_id=@tenant AND kind='guard' AND revision=1",
                new { tenant, content = "TAMPERED-CONTENT" });
        }

        using var factory = new DapperPromptArtifactFactory();
        var client = factory.CreateInternalClient().WithTenant(tenant).WithRole("USER").WithUser("caller");
        var response = await client.GetAsync($"/api/prompt-manifests/{manifest.Revision}/resolved");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>同一條防線的 manifest 那一半:manifest_canonical 被竄改(manifest_sha256 不動)同樣 500。</summary>
    [SkippableFact]
    public async Task ResolvedRoute_FailsClosed_WhenStoredManifestCanonicalIsTampered()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "tamper-manifest";
        await Repo.PublishComponentAsync(tenant, "guard", "GUARD-ORIGINAL", "tester", default);
        var manifest = await Repo.CreateManifestAsync(tenant, Canonical(("guard", 1)), "tester", default);

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE prompt_manifest_revision SET manifest_canonical=@canonical"
                + " WHERE tenant_id=@tenant AND revision=@revision",
                new { tenant, revision = manifest.Revision, canonical = Canonical(("guard", 999)) });
        }

        using var factory = new DapperPromptArtifactFactory();
        var client = factory.CreateInternalClient().WithTenant(tenant).WithRole("USER").WithUser("caller");
        var response = await client.GetAsync($"/api/prompt-manifests/{manifest.Revision}/resolved");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>真 Dapper 版 <see cref="IPromptArtifactRepository"/>(其餘依賴仍走 TestWebAppFactory 的 fake),
    /// 讓竄改測試能連真的 Postgres 直接下 SQL 破壞資料,再打真正的 HTTP 路由驗證 fail-closed。</summary>
    private sealed class DapperPromptArtifactFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PROMPT_ARTIFACTS_ENABLED", "true");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPromptArtifactRepository>();
                services.AddScoped<IPromptArtifactRepository, PromptArtifactRepository>();
            });
        }
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
