using System.Net;
using Backend.Api.Agents;
using Backend.Api.PromptArtifacts;
using Backend.Api.Skills;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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

        using var factory = new DapperPromptArtifactFactory(fixture.DataSource!);
        var client = factory.CreateInternalClient().WithTenant(tenant).WithRole("USER").WithUser("caller");
        var response = await client.GetAsync($"/api/prompt-manifests/{manifest.Revision}/resolved");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// 同一條防線的 manifest 那一半:manifest_canonical 被竄改(manifest_sha256 不動)同樣 500。
    /// 竄改後的 canonical 刻意仍結構合法且指向存在的 guard#1,只有 tool_catalog_hash 不同 ——
    /// 唯一會觸發 500 的就是 manifest SHA 檢查本身。(指向不存在的 guard#999 會因「元件找不到」
    /// 也丟 InvalidOperationException,拿掉 SHA 守門照樣 500,測試就成了假綠。)
    /// 竄改前先打一次同一條路由並斷言 200,把「500 因竄改」與「路由本來就 500」分開。
    /// </summary>
    [SkippableFact]
    public async Task ResolvedRoute_FailsClosed_WhenStoredManifestCanonicalIsTampered()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "tamper-manifest";
        await Repo.PublishComponentAsync(tenant, "guard", "GUARD-ORIGINAL", "tester", default);
        var manifest = await Repo.CreateManifestAsync(tenant, Canonical(("guard", 1)), "tester", default);

        using var factory = new DapperPromptArtifactFactory(fixture.DataSource!);
        var client = factory.CreateInternalClient().WithTenant(tenant).WithRole("USER").WithUser("caller");
        var url = $"/api/prompt-manifests/{manifest.Revision}/resolved";

        var beforeTamper = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, beforeTamper.StatusCode);

        await using (var connection = await fixture.DataSource!.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE prompt_manifest_revision SET manifest_canonical=@canonical"
                + " WHERE tenant_id=@tenant AND revision=@revision",
                new
                {
                    tenant,
                    revision = manifest.Revision,
                    canonical = Canonical("TAMPERED-TOOL-HASH", ("guard", 1)),
                });
        }

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>真 Dapper 版 <see cref="IPromptArtifactRepository"/>(其餘依賴仍走 TestWebAppFactory 的 fake),
    /// 讓竄改測試能連真的 Postgres 直接下 SQL 破壞資料,再打真正的 HTTP 路由驗證 fail-closed。</summary>
    private sealed class DapperPromptArtifactFactory(NpgsqlDataSource dataSource)
        : PostgresTestWebAppFactory(dataSource)
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

        var (agent, definition, sha) = await CreateValidatedAgentAsync(agents, tenant, "pinned");

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

    /// <summary>
    /// 另一半等價類:<c>promptManifestPin: null</c>(預設、也是最常見的 publish 呼叫)。null 是綁進
    /// publish 那條 <c>INSERT ... SELECT @promptManifestRevision, @promptManifestSha256 FROM agent</c>
    /// 的,參數型別由目標欄位推導而非 VALUES 字面值 — 這條 Npgsql/Postgres 路徑只有 InMemory fake 背書
    /// 過(PromptArtifactsApiTests),真 DB 上必須確認落的是 NULL 而不是 0/空字串。
    /// </summary>
    [SkippableFact]
    public async Task AgentPublish_WithoutPin_PersistsNullManifestColumns()
    {
        fixture.SkipIfUnavailable();
        var tenant = TenantPrefix + "unpinned";
        var agents = new AgentRepository(fixture.DataSource!);
        var (agent, definition, sha) = await CreateValidatedAgentAsync(agents, tenant, "unpinned");

        var published = await agents.PublishAsync(
            tenant, agent.Id, agent.DraftVersion, definition, sha, "publisher", default);
        Assert.Equal(AgentWriteStatus.Success, published.Status);

        var revision = Assert.Single(await agents.ListRevisionsAsync(tenant, agent.Id, default));
        Assert.Null(revision.PromptManifestRevision);
        Assert.Null(revision.PromptManifestSha256);
    }

    /// <summary>可直接 publish 的已驗證 draft;definition 內容與 manifest pin 無關,兩個 pin 測試共用。</summary>
    private static async Task<(Agent Agent, string Definition, string Sha)> CreateValidatedAgentAsync(
        AgentRepository agents, string tenant, string slug)
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            null, null, null, "你是研究助手", ["worker"], null, null, null, null, null, null, null, null,
            new AgentWorkflowRef(AgentDefaults.RuntimeWorkflowId, AgentDefaults.RuntimeWorkflowRevision)));
        var sha = SkillHash.Sha256(definition);
        var agent = await agents.CreateAsync(tenant, slug, "n", "d", definition, sha, "author", default);
        Assert.NotNull(agent);
        Assert.True(await agents.MarkValidatedAsync(tenant, agent!.Id, agent.DraftVersion, definition, sha, default));
        return (agent!, definition, sha);
    }

    private static string Canonical(params (string Kind, int Revision)[] components)
        => Canonical("tool-hash", components);

    private static string Canonical(string toolCatalogHash, params (string Kind, int Revision)[] components)
        => PromptManifestCanonicalizer.Canonicalize(
            PromptArtifactContract.SchemaVersion,
            components.ToDictionary(c => c.Kind, c => c.Revision),
            toolCatalogHash,
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
