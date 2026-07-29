using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.PromptArtifacts;
using Microsoft.AspNetCore.Hosting;

namespace Backend.Api.Tests;

/// <summary>
/// P1 canonical prompt manifest authority (plans/agent-architecture-improvements/03 §3): component
/// revision registry, manifest canonical identity, and the optional Agent publish pin. Runs against
/// the InMemory repositories (same implementation Lite mode uses); the Dapper projections are the
/// same shape and are exercised by the Agent publish path there.
/// </summary>
public sealed class PromptArtifactsApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;
    public PromptArtifactsApiTests(TestWebAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET", "/api/prompt-components")]
    [InlineData("POST", "/api/prompt-components")]
    [InlineData("GET", "/api/prompt-components/guard/1")]
    [InlineData("GET", "/api/prompt-manifests")]
    [InlineData("POST", "/api/prompt-manifests")]
    [InlineData("GET", "/api/prompt-manifests/1")]
    [InlineData("GET", "/api/prompt-manifests/by-sha/abc")]
    public async Task FlagOff_HidesEveryPromptArtifactRoute(string method, string path)
    {
        // PROMPT_ARTIFACTS_ENABLED is unset on the shared factory (defaults false). The gate sits
        // after InternalTokenMiddleware like the D4/D5/context/eval gates, so a valid internal token
        // still reaches it -- ADMIN/tenant auth is what stays bypassed (404 before auth).
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await _factory.CreateInternalClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FlagOff_PublishIgnoresPromptManifestRevision()
    {
        // Off path: the field is not read at all, so a caller cannot pin (or fail) through it.
        var client = Admin(_factory);
        var (_, revisions) = await PublishAgentAsync(client, "flag-off-pin", promptManifestRevision: 99);
        Assert.DoesNotContain("prompt_manifest", revisions.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Component_RepublishIsIdempotent_NewContentAdvances_HistoryNeverChanges()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);

        var first = await PublishComponentAsync(client, "guard", "GUARD-V1");
        Assert.Equal(1, first["revision"]!.GetValue<int>());

        var republished = await PublishComponentAsync(client, "guard", "GUARD-V1");
        Assert.Equal(1, republished["revision"]!.GetValue<int>());
        Assert.Equal(first["content_sha256"]!.GetValue<string>(), republished["content_sha256"]!.GetValue<string>());

        var second = await PublishComponentAsync(client, "guard", "GUARD-V2");
        Assert.Equal(2, second["revision"]!.GetValue<int>());
        Assert.NotEqual(first["content_sha256"]!.GetValue<string>(), second["content_sha256"]!.GetValue<string>());

        // rev1 is immutable: its digest is untouched by everything published after it.
        var reread = await (await client.GetAsync("/api/prompt-components/guard/1")).ReadJsonAsync();
        Assert.Equal(first["content_sha256"]!.GetValue<string>(), reread["content_sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Component_RejectsUnknownKind_AndCrossTenantReadIsNotFound()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var owner = Admin(factory, "prompt-a");
        await PublishComponentAsync(owner, "persona", "PERSONA-A");

        var unknownKind = await owner.PostAsJsonAsync(
            "/api/prompt-components", new JsonObject { ["kind"] = "system_prompt", ["content"] = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);

        var other = Admin(factory, "prompt-b");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/prompt-components/persona/1")).StatusCode);
        Assert.Empty((await (await other.GetAsync("/api/prompt-components")).ReadJsonAsync()).AsArray());
    }

    [Theory]
    [InlineData(PromptArtifactContract.MaxContentLength, HttpStatusCode.OK)]
    [InlineData(PromptArtifactContract.MaxContentLength + 1, HttpStatusCode.BadRequest)]
    public async Task Component_ContentLengthBoundary(int length, HttpStatusCode expected)
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var response = await Admin(factory).PostAsJsonAsync(
            "/api/prompt-components",
            new JsonObject { ["kind"] = "summary", ["content"] = new string('x', length) });
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task RawComponentContent_NeverAppearsInAnyResponse()
    {
        // Secret scan: the protected artifact text must not reach a browser-facing DTO through the
        // publish echo, the list/read projections, or anything a manifest carries.
        const string Secret = "SECRET-GOVERNANCE-FRAME-TEXT-42";
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);

        var published = await PublishComponentAsync(client, "governance_frame", Secret);
        var bodies = new List<string> { published.ToJsonString() };
        foreach (var path in new[] { "/api/prompt-components", "/api/prompt-components/governance_frame/1" })
        {
            bodies.Add(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        }

        var manifest = await CreateManifestAsync(client, new JsonObject { ["governance_frame"] = 1 });
        bodies.Add(manifest.ToJsonString());
        var sha = manifest["manifest_sha256"]!.GetValue<string>();
        foreach (var path in new[] { "/api/prompt-manifests", "/api/prompt-manifests/1", $"/api/prompt-manifests/by-sha/{sha}" })
        {
            bodies.Add(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        }

        foreach (var body in bodies)
        {
            Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        }
        // The projections that replace it are still useful: digest + server-authored summary.
        Assert.Contains("governance_frame revision 1 (", published["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Manifest_CanonicalBytesAndSha_AreDeterministic_AndKeyOrderIndependent()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        await PublishComponentAsync(client, "guard", "GUARD");
        await PublishComponentAsync(client, "persona", "PERSONA");

        var first = await CreateManifestAsync(
            client, new JsonObject { ["guard"] = 1, ["persona"] = 1 });
        var reordered = await CreateManifestAsync(
            client, new JsonObject { ["persona"] = 1, ["guard"] = 1 });

        // Same inputs in a different key order → identical canonical bytes, identical SHA, and the
        // idempotent no-op keeps it at the same revision.
        Assert.Equal(first["manifest"]!.ToJsonString(), reordered["manifest"]!.ToJsonString());
        Assert.Equal(first["manifest_sha256"]!.GetValue<string>(), reordered["manifest_sha256"]!.GetValue<string>());
        Assert.Equal(first["revision"]!.GetValue<int>(), reordered["revision"]!.GetValue<int>());

        // The canonical text is the exact hashed bytes, reproducible without the server.
        var expected = PromptManifestCanonicalizer.Canonicalize(
            PromptArtifactContract.SchemaVersion,
            new Dictionary<string, int> { ["persona"] = 1, ["guard"] = 1 },
            "tool-hash", "skill-hash");
        Assert.Equal(expected, first["manifest"]!.ToJsonString());
        Assert.Equal(Backend.Api.Skills.SkillHash.Sha256(expected), first["manifest_sha256"]!.GetValue<string>());

        // Referencing a different component revision is a different identity, not a mutation.
        await PublishComponentAsync(client, "guard", "GUARD-V2");
        var advanced = await CreateManifestAsync(client, new JsonObject { ["guard"] = 2, ["persona"] = 1 });
        Assert.NotEqual(first["manifest_sha256"]!.GetValue<string>(), advanced["manifest_sha256"]!.GetValue<string>());
        Assert.Equal(2, advanced["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Manifest_FailsClosed_OnMissingReference_CrossTenant_SchemaDrift_AndLatest()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var owner = Admin(factory, "manifest-a");
        await PublishComponentAsync(owner, "guard", "GUARD");

        // Existing tenant component (on-point schema_version) is the control: it succeeds.
        Assert.Equal(1, (await CreateManifestAsync(owner, new JsonObject { ["guard"] = 1 }))["revision"]!.GetValue<int>());

        // Nonexistent revision of an existing kind.
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostManifestAsync(owner, new JsonObject { ["guard"] = 2 })).StatusCode);

        // Cross-tenant reference: tenant B cannot borrow tenant A's component revision.
        var other = Admin(factory, "manifest-b");
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostManifestAsync(other, new JsonObject { ["guard"] = 1 })).StatusCode);

        // schema_version off-point (supported = 1) and a missing schema_version both fail closed.
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostManifestAsync(owner, new JsonObject { ["guard"] = 1 }, schemaVersion: PromptArtifactContract.SchemaVersion + 1)).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostManifestAsync(owner, new JsonObject { ["guard"] = 1 }, schemaVersion: null)).StatusCode);

        // "latest" is not representable: component references must be explicit revision integers.
        var latest = await owner.PostAsJsonAsync("/api/prompt-manifests", new JsonObject
        {
            ["schema_version"] = PromptArtifactContract.SchemaVersion,
            ["components"] = new JsonObject { ["guard"] = "latest" },
            ["tool_catalog_hash"] = "tool-hash",
            ["skill_catalog_hash"] = "skill-hash",
        });
        Assert.Equal(HttpStatusCode.BadRequest, latest.StatusCode);

        // Unknown kind and an empty component set are rejected before anything is written.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostManifestAsync(owner, new JsonObject { ["system_prompt"] = 1 })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await PostManifestAsync(owner, new JsonObject())).StatusCode);

        // None of the rejected attempts created a revision.
        Assert.Single((await (await owner.GetAsync("/api/prompt-manifests")).ReadJsonAsync()).AsArray());
    }

    [Fact]
    public async Task Manifest_Rollback_CreatesNewRevisionReferencingOldComponents_WithoutRewritingHistory()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        await PublishComponentAsync(client, "routing", "ROUTING-V1");
        var manifest1 = await CreateManifestAsync(client, new JsonObject { ["routing"] = 1 });
        await PublishComponentAsync(client, "routing", "ROUTING-V2");
        var manifest2 = await CreateManifestAsync(client, new JsonObject { ["routing"] = 2 });

        // Rollback = a NEW manifest revision that references the old component revision.
        var rolledBack = await CreateManifestAsync(client, new JsonObject { ["routing"] = 1 });
        Assert.Equal(3, rolledBack["revision"]!.GetValue<int>());
        Assert.Equal(manifest1["manifest_sha256"]!.GetValue<string>(), rolledBack["manifest_sha256"]!.GetValue<string>());

        // History is untouched: revisions 1 and 2 still read back exactly as published.
        foreach (var original in new[] { manifest1, manifest2 })
        {
            var revision = original["revision"]!.GetValue<int>();
            var reread = await (await client.GetAsync($"/api/prompt-manifests/{revision}")).ReadJsonAsync();
            Assert.Equal(original["manifest_sha256"]!.GetValue<string>(), reread["manifest_sha256"]!.GetValue<string>());
            Assert.Equal(original["manifest"]!.ToJsonString(), reread["manifest"]!.ToJsonString());
        }
    }

    [Fact]
    public async Task Publish_PinsManifestIntoSnapshot_UnpinnedIsUnchanged_AndPinNeverDrifts()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        await PublishComponentAsync(client, "guard", "GUARD-V1");
        var manifest = await CreateManifestAsync(client, new JsonObject { ["guard"] = 1 });
        var manifestSha = manifest["manifest_sha256"]!.GetValue<string>();

        // No manifest supplied → snapshot carries no pin fields at all (byte-for-byte the pre-P1 shape).
        var (_, unpinned) = await PublishAgentAsync(client, "prompt-unpinned");
        Assert.DoesNotContain("prompt_manifest", unpinned.ToJsonString(), StringComparison.Ordinal);

        // Manifest supplied → revision + canonical SHA are pinned into the immutable snapshot.
        var (agentId, revisions) = await PublishAgentAsync(client, "prompt-pinned", promptManifestRevision: 1);
        var pinned = revisions[0]!;
        Assert.Equal(1, pinned["prompt_manifest_revision"]!.GetValue<int>());
        Assert.Equal(manifestSha, pinned["prompt_manifest_sha256"]!.GetValue<string>());

        // A newer manifest revision must not move an already published snapshot.
        await PublishComponentAsync(client, "guard", "GUARD-V2");
        var newer = await CreateManifestAsync(client, new JsonObject { ["guard"] = 2 });
        Assert.Equal(2, newer["revision"]!.GetValue<int>());
        var reread = (await (await client.GetAsync($"/api/agents/{agentId}/revisions")).ReadJsonAsync())[0]!;
        Assert.Equal(1, reread["prompt_manifest_revision"]!.GetValue<int>());
        Assert.Equal(manifestSha, reread["prompt_manifest_sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Publish_WithUnknownManifestRevision_FailsClosed_AndCreatesNoRevision()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        var (id, _) = await CreateAgentAsync(client, "prompt-bad-pin");
        await ValidateAgentAsync(client, id);

        var response = await PublishAgentRequestAsync(client, id, promptManifestRevision: 7);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty((await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray());

        // A manifest belonging to another tenant is equally invisible here.
        var other = Admin(factory, "pin-other-tenant");
        await PublishComponentAsync(other, "guard", "GUARD");
        await CreateManifestAsync(other, new JsonObject { ["guard"] = 1 });
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PublishAgentRequestAsync(client, id, promptManifestRevision: 1)).StatusCode);
    }

    // ---- helpers ----

    private static HttpClient Admin(TestWebAppFactory factory, string tenant = "demo-a")
        => factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant).WithUser("admin-a");

    private static async Task<JsonNode> PublishComponentAsync(HttpClient client, string kind, string content)
    {
        var response = await client.PostAsJsonAsync(
            "/api/prompt-components", new JsonObject { ["kind"] = kind, ["content"] = content });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static Task<HttpResponseMessage> PostManifestAsync(
        HttpClient client, JsonObject components, int? schemaVersion = PromptArtifactContract.SchemaVersion)
        => client.PostAsJsonAsync("/api/prompt-manifests", new JsonObject
        {
            ["schema_version"] = schemaVersion,
            ["components"] = components,
            ["tool_catalog_hash"] = "tool-hash",
            ["skill_catalog_hash"] = "skill-hash",
        });

    private static async Task<JsonNode> CreateManifestAsync(HttpClient client, JsonObject components)
    {
        var response = await PostManifestAsync(client, components);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static JsonObject ValidAgentBody(string slug) => new()
    {
        ["slug"] = slug,
        ["name"] = "prompt-artifact-agent",
        ["description"] = "",
        ["system_prompt"] = "你是研究助手",
        ["execution_roles"] = new JsonArray("worker"),
        ["runtime_workflow"] = new JsonObject
        {
            ["id"] = AgentDefaults.RuntimeWorkflowId,
            ["revision"] = AgentDefaults.RuntimeWorkflowRevision,
        },
    };

    private static async Task<(string Id, string ETag)> CreateAgentAsync(HttpClient client, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/agents", ValidAgentBody(slug));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await response.ReadJsonAsync())["id"]!.GetValue<string>(), response.Headers.ETag!.Tag);
    }

    private static async Task ValidateAgentAsync(HttpClient client, string id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/validate");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        Assert.True((await (await client.SendAsync(request)).ReadJsonAsync())["valid"]!.GetValue<bool>());
    }

    private static Task<HttpResponseMessage> PublishAgentRequestAsync(
        HttpClient client, string id, int? promptManifestRevision)
    {
        var body = new JsonObject { ["expected_draft_version"] = 1 };
        if (promptManifestRevision is int revision)
        {
            body["prompt_manifest_revision"] = revision;
        }
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/publish")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        return client.SendAsync(request);
    }

    /// <summary>Create → validate → publish, returning the agent id and its revision list.</summary>
    private static async Task<(string Id, JsonArray Revisions)> PublishAgentAsync(
        HttpClient client, string slug, int? promptManifestRevision = null)
    {
        var (id, _) = await CreateAgentAsync(client, slug);
        await ValidateAgentAsync(client, id);
        var published = await PublishAgentRequestAsync(client, id, promptManifestRevision);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (id, (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray());
    }

    private sealed class PromptArtifactsEnabledFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("PROMPT_ARTIFACTS_ENABLED", "true");
        }
    }
}
