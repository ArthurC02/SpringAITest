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
    [InlineData("GET", "/api/prompt-manifests/1/resolved")]
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

    // The other half of RequireContent's guard: string.IsNullOrWhiteSpace. Content is hashed
    // verbatim, so "blank" can never become a stored artifact identity -- omitted, empty (the
    // length-0 on-point boundary) and whitespace-only all 400, while 1 char is the shortest
    // publishable content (off-point).
    [Theory]
    [InlineData(null, HttpStatusCode.BadRequest)]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("   ", HttpStatusCode.BadRequest)]
    [InlineData("x", HttpStatusCode.OK)]
    public async Task Component_BlankContentBoundary(string? content, HttpStatusCode expected)
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var response = await Admin(factory).PostAsJsonAsync(
            "/api/prompt-components",
            new JsonObject { ["kind"] = "summary", ["content"] = content });
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

    // Catalog hashes are part of the manifest's canonical identity, so a blank value or one
    // smuggling a control character would poison the hash. RequireCatalogHash rejects that whole
    // invalid class with 400 -- and both fields are guarded identically (last row = sibling field).
    [Theory]
    [InlineData(null, "skill-hash")]
    [InlineData("", "skill-hash")]
    [InlineData("   ", "skill-hash")]
    [InlineData("tool\u0001hash", "skill-hash")]
    [InlineData("tool-hash", "")]
    public async Task Manifest_RejectsInvalidCatalogHash(string? toolCatalogHash, string? skillCatalogHash)
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        await PublishComponentAsync(client, "guard", "GUARD");

        var response = await PostManifestAsync(
            client, new JsonObject { ["guard"] = 1 },
            toolCatalogHash: toolCatalogHash, skillCatalogHash: skillCatalogHash);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The guard runs before the repository write, so no revision was created.
        Assert.Empty((await (await client.GetAsync("/api/prompt-manifests")).ReadJsonAsync()).AsArray());
    }

    [Theory]
    [InlineData(PromptArtifactContract.MaxCatalogHashLength, HttpStatusCode.OK)]
    [InlineData(PromptArtifactContract.MaxCatalogHashLength + 1, HttpStatusCode.BadRequest)]
    public async Task Manifest_CatalogHashLengthBoundary(int length, HttpStatusCode expected)
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        await PublishComponentAsync(client, "guard", "GUARD");

        var response = await PostManifestAsync(
            client, new JsonObject { ["guard"] = 1 }, toolCatalogHash: new string('x', length));
        Assert.Equal(expected, response.StatusCode);
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
    public async Task ResolvedManifest_ReturnsComponentsInManifestOrder_AndIsTheOnlyRouteWithContent()
    {
        // Secret scan (plan 03 §3): raw content must appear on the resolved route and NOWHERE else.
        const string GuardSecret = "RESOLVED-GUARD-SECRET";
        const string PersonaSecret = "RESOLVED-PERSONA-SECRET";
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);

        // Published out of alphabetical order; the manifest's own canonical (ordinal-sorted) order
        // is "guard" then "persona" regardless of publish/reference order.
        await PublishComponentAsync(client, "persona", PersonaSecret);
        await PublishComponentAsync(client, "guard", GuardSecret);
        var manifest = await CreateManifestAsync(
            client, new JsonObject { ["persona"] = 1, ["guard"] = 1 });
        var revision = manifest["revision"]!.GetValue<int>();
        var sha = manifest["manifest_sha256"]!.GetValue<string>();

        var resolvedResponse = await client.GetAsync($"/api/prompt-manifests/{revision}/resolved");
        Assert.Equal(HttpStatusCode.OK, resolvedResponse.StatusCode);
        var resolved = await resolvedResponse.ReadJsonAsync();
        Assert.Equal(revision, resolved["revision"]!.GetValue<int>());
        Assert.Equal(sha, resolved["manifest_sha256"]!.GetValue<string>());
        Assert.Equal(PromptArtifactContract.SchemaVersion, resolved["schema_version"]!.GetValue<int>());
        Assert.Equal("tool-hash", resolved["tool_catalog_hash"]!.GetValue<string>());
        Assert.Equal("skill-hash", resolved["skill_catalog_hash"]!.GetValue<string>());

        var components = resolved["components"]!.AsArray();
        Assert.Equal(
            new[] { "guard", "persona" },
            components.Select(c => c!["kind"]!.GetValue<string>()));
        var guard = components.Single(c => c!["kind"]!.GetValue<string>() == "guard")!;
        Assert.Equal(1, guard["revision"]!.GetValue<int>());
        Assert.Equal(GuardSecret, guard["content"]!.GetValue<string>());
        Assert.Equal(
            Backend.Api.Skills.SkillHash.Sha256(GuardSecret), guard["content_sha256"]!.GetValue<string>());
        var persona = components.Single(c => c!["kind"]!.GetValue<string>() == "persona")!;
        Assert.Equal(PersonaSecret, persona["content"]!.GetValue<string>());

        // Every sibling prompt-* route still projects only digest/summary -- never raw text.
        var siblingBodies = new List<string>
        {
            await (await client.GetAsync("/api/prompt-components")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/prompt-components/guard/1")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/prompt-components/persona/1")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/prompt-manifests")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/prompt-manifests/{revision}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/prompt-manifests/by-sha/{sha}")).Content.ReadAsStringAsync(),
        };
        foreach (var body in siblingBodies)
        {
            Assert.DoesNotContain(GuardSecret, body, StringComparison.Ordinal);
            Assert.DoesNotContain(PersonaSecret, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ResolvedManifest_IsNotAdminGated_UnlikeItsSiblingBuilderRoutes()
    {
        // Deliberate divergence (plan 03 §3 / backend/AGENTS.md): every other prompt-* route is
        // ADMIN-only Builder management; the resolved route is called at execution time by
        // Platform/Workflow assemblers, so it must stay reachable with only a tenant header.
        using var factory = new PromptArtifactsEnabledFactory();
        var owner = Admin(factory, "resolved-nonadmin");
        await PublishComponentAsync(owner, "guard", "GUARD");
        await CreateManifestAsync(owner, new JsonObject { ["guard"] = 1 });

        var nonAdmin = factory.CreateInternalClient()
            .WithTenant("resolved-nonadmin")
            .WithRole("USER")
            .WithUser("caller");
        Assert.Equal(
            HttpStatusCode.OK,
            (await nonAdmin.GetAsync("/api/prompt-manifests/1/resolved")).StatusCode);

        // The sibling Builder route on the exact same tenant still requires ADMIN.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await nonAdmin.GetAsync("/api/prompt-manifests/1")).StatusCode);
    }

    // The other half of that divergence: the resolved GET route is the ONLY route a non-ADMIN may
    // reach. With the flag ON, both POST write routes still 403 for a USER on a valid tenant, with
    // the resource-specific message -- and because [AdminOnly] is an authorization filter, an
    // invalid body cannot short-circuit into a 400 that leaks the field rules.
    [Theory]
    [InlineData("/api/prompt-components")]
    [InlineData("/api/prompt-manifests")]
    public async Task WriteRoutes_RejectNonAdmin_AndWriteNothing(string path)
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var nonAdmin = factory.CreateInternalClient()
            .WithTenant("write-nonadmin")
            .WithRole("USER")
            .WithUser("caller");

        var response = await nonAdmin.PostAsJsonAsync(path, new JsonObject { ["kind"] = "not-a-kind" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "權限不足，無法存取 prompt artifacts",
            (await response.ReadJsonAsync())["message"]!.GetValue<string>());

        // Nothing reached the tenant's store (read back as the ADMIN of that same tenant).
        Assert.Empty(
            (await (await Admin(factory, "write-nonadmin").GetAsync(path)).ReadJsonAsync()).AsArray());
    }

    [Fact]
    public async Task ResolvedManifest_UnknownRevision_AndCrossTenant_AreNotFound()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var owner = Admin(factory, "resolved-a");
        await PublishComponentAsync(owner, "guard", "GUARD");
        await CreateManifestAsync(owner, new JsonObject { ["guard"] = 1 });

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.GetAsync("/api/prompt-manifests/999/resolved")).StatusCode);

        var other = Admin(factory, "resolved-b");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.GetAsync("/api/prompt-manifests/1/resolved")).StatusCode);
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

    [Fact]
    public async Task Publish_PinnedManifest_FlowsIntoDirectRunExecutionSnapshot()
    {
        using var factory = new PromptArtifactsEnabledFactory();
        var client = Admin(factory);
        await PublishComponentAsync(client, "guard", "GUARD-V1");
        var manifest = await CreateManifestAsync(client, new JsonObject { ["guard"] = 1 });
        var manifestRevision = manifest["revision"]!.GetValue<int>();
        var manifestSha = manifest["manifest_sha256"]!.GetValue<string>();

        // A runnable Agent needs an audience the caller matches; ValidAgentBody omits it (those tests
        // never execute a run), so this test supplies it directly.
        var body = ValidAgentBody("prompt-pin-run-" + Guid.NewGuid().ToString("N"));
        body["audience"] = new JsonArray("ADMIN");
        var create = await client.PostAsJsonAsync("/api/agents", body);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var agentId = (await create.ReadJsonAsync())["id"]!.GetValue<string>();
        await ValidateAgentAsync(client, agentId);
        var publish = await PublishAgentRequestAsync(client, agentId, manifestRevision);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var start = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{agentId}/runs")
        {
            Content = JsonContent.Create(new { message = "hi" }),
        };
        start.Headers.TryAddWithoutValidation("Idempotency-Key", "prompt-pin-run-1");
        var response = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = await response.ReadJsonAsync();

        var artifactResponse = await client.GetAsync(
            $"/api/agent-runs/{run["id"]!.GetValue<string>()}/execution-artifact");
        Assert.Equal(HttpStatusCode.OK, artifactResponse.StatusCode);
        var envelope = await artifactResponse.ReadJsonAsync();
        var canonicalBytes = Convert.FromBase64String(
            envelope["snapshot_canonical_base64"]!.GetValue<string>());
        var snapshot = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(canonicalBytes))!;
        var pin = snapshot["agent"]!["prompt_manifest"]!;
        Assert.Equal(manifestRevision, pin["revision"]!.GetValue<int>());
        Assert.Equal(manifestSha, pin["sha256"]!.GetValue<string>());
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
        HttpClient client,
        JsonObject components,
        int? schemaVersion = PromptArtifactContract.SchemaVersion,
        string? toolCatalogHash = "tool-hash",
        string? skillCatalogHash = "skill-hash")
        => client.PostAsJsonAsync("/api/prompt-manifests", new JsonObject
        {
            ["schema_version"] = schemaVersion,
            ["components"] = components,
            ["tool_catalog_hash"] = toolCatalogHash,
            ["skill_catalog_hash"] = skillCatalogHash,
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
