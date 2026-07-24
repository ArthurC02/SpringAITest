using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Api.Agents;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// Agent Registry API 驗收(A-DATA-01~09/11 的服務層部分)。跑的是 InMemoryAgentRepository(fake=Lite 同一份),
/// 真 Postgres 的 jsonb 抽欄/pin/restore 由 AgentRepositoryTests 另外背書(A-DATA-04/05/06/07)。
/// fake repo/validator 為 class fixture 共用 → 每測用自己的 slug/skill 名,避免互汙染。
/// </summary>
public sealed class AgentsApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public AgentsApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant).WithUser("admin-a");

    private HttpClient User(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("USER").WithTenant(tenant);

    // 合法 Agent 建立 body(可 publish 的最小定義:system_prompt + worker role + pinned runtime workflow)。
    private static JsonObject ValidBody(string slug, string name = "研究助手")
        => new()
        {
            ["slug"] = slug,
            ["name"] = name,
            ["description"] = "說明",
            ["system_prompt"] = "你是研究助手",
            ["execution_roles"] = new JsonArray("worker"),
            ["runtime_workflow"] = new JsonObject
            {
                ["id"] = AgentDefaults.RuntimeWorkflowId,
                ["revision"] = AgentDefaults.RuntimeWorkflowRevision,
            },
        };

    private async Task<(string Id, string ETag)> CreateAsync(HttpClient client, JsonObject body)
    {
        var resp = await client.PostAsJsonAsync("/api/agents", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await resp.ReadJsonAsync();
        return (json["id"]!.GetValue<string>(), resp.Headers.ETag!.Tag);
    }

    private static HttpRequestMessage PutDraft(string id, JsonObject body, string? ifMatch)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/agents/{id}/draft")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return req;
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, string id, long expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/publish")
        {
            Content = JsonContent.Create(new JsonObject { ["expected_draft_version"] = expected }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{expected}\"");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> ValidateAsync(
        HttpClient client, string id, long expectedDraftVersion = 1)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/validate");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedDraftVersion}\"");
        return client.SendAsync(request);
    }

    /// <summary>建立一個 flow skill(供 binding 用),回傳其名稱。沿用 skills API 的 YAML-only body。</summary>
    private static async Task<string> CreateSkillAsync(HttpClient client, string name)
    {
        var yaml = $"name: {name}\ndescription: 綁定用\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        var resp = await client.PostAsJsonAsync("/api/skills", new JsonObject { ["definition"] = yaml });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return name;
    }

    // ---- A-DATA-01:同 tenant 兩個不同 slug ----

    [Fact]
    public async Task Create_TwoDifferentSlugs_Succeeds_WithNoPublishedRevisionYet()
    {
        var client = Admin();
        var (id1, etag1) = await CreateAsync(client, ValidBody("a01-one"));
        await CreateAsync(client, ValidBody("a01-two"));

        Assert.Equal("\"1\"", etag1);
        var body = await (await client.GetAsync($"/api/agents/{id1}")).ReadJsonAsync();
        Assert.Equal("a01-one", body["slug"]!.GetValue<string>());
        Assert.Equal(1, body["draft_version"]!.GetValue<long>());
        Assert.Null(body["published_revision"]); // JSON null → null node
        Assert.Null(body["draft_validated_version"]);
        Assert.True(body["enabled"]!.GetValue<bool>());
    }

    // ---- A-DATA-02:重複 slug → 409;不同 tenant 可用相同 slug ----

    [Fact]
    public async Task Create_DuplicateSlugSameTenant_Returns409()
    {
        var client = Admin();
        await CreateAsync(client, ValidBody("a02-dup"));

        var resp = await client.PostAsJsonAsync("/api/agents", ValidBody("a02-dup", "另一個"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("Agent slug 已存在：a02-dup", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_SameSlugDifferentTenant_AreIndependent()
    {
        await CreateAsync(Admin("demo-a"), ValidBody("a02-shared", "A 的"));
        var (idB, _) = await CreateAsync(Admin("demo-b"), ValidBody("a02-shared", "B 的"));

        Assert.Equal("B 的",
            (await (await Admin("demo-b").GetAsync($"/api/agents/{idB}")).ReadJsonAsync())["name"]!.GetValue<string>());
    }

    // ---- A-DATA-03:讀其他 tenant → 404,不洩漏存在性 ----

    [Fact]
    public async Task CrossTenant_Agent_IsInvisible()
    {
        var (id, etag) = await CreateAsync(Admin("demo-a"), ValidBody("a03-secret"));
        var b = Admin("demo-b");

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/agents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/agents/{id}/revisions")).StatusCode);
        Assert.DoesNotContain(
            (await (await b.GetAsync("/api/agents")).ReadJsonAsync()).AsArray(),
            n => n!["id"]!.GetValue<string>() == id);

        // 跨租戶寫入/發布/停用一律 404(不生效)。
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.SendAsync(PutDraft(id, ValidBody("a03-secret"), etag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishAsync(b, id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/agents/{id}")).StatusCode);
    }

    [Fact]
    public async Task Get_Missing_Returns404_ApiError()
    {
        var resp = await Admin().GetAsync($"/api/agents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.NotNull(body["timestamp"]);
        Assert.NotNull(body["fieldErrors"]);
    }

    // ---- A-DATA-08:stale ETag → 409;正確 If-Match → 版本 +1;缺 If-Match → 428 ----

    [Fact]
    public async Task PutDraft_StaleIfMatch_Returns409_AndDoesNotOverwrite()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a08-slug"));

        // 先用正確 ETag 更新一次 → version 2、ETag "2"。
        var ok = await client.SendAsync(PutDraft(id, ValidBody("a08-slug", "第二版"), "\"1\""));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"2\"", ok.Headers.ETag!.Tag);

        // 另一位 ADMIN 拿舊 ETag "1" 再改 → 409,不覆蓋。
        var stale = await client.SendAsync(PutDraft(id, ValidBody("a08-slug", "偷改"), "\"1\""));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync();
        Assert.Equal("第二版", current["name"]!.GetValue<string>());
        Assert.Equal(2, current["draft_version"]!.GetValue<long>());
    }

    [Fact]
    public async Task PutDraft_MissingIfMatch_Returns428()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a08-noetag"));

        var resp = await client.SendAsync(PutDraft(id, ValidBody("a08-noetag"), ifMatch: null));

        Assert.Equal(HttpStatusCode.PreconditionRequired, resp.StatusCode);
    }

    [Fact]
    public async Task Validate_MissingOrStaleIfMatch_FailsClosed()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("validate-etag"));

        Assert.Equal(
            HttpStatusCode.PreconditionRequired,
            (await client.PostAsync($"/api/agents/{id}/validate", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await ValidateAsync(client, id, expectedDraftVersion: 99)).StatusCode);

        var current = await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync();
        Assert.Null(current["draft_validated_version"]);
    }

    [Fact]
    public async Task PutDraft_ClearsValidated_BumpsVersion()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a08-validated"));
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(client, id)).StatusCode);

        // validate 後 draft_validated_version = 1。
        var afterValidate = await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync();
        Assert.Equal(1, afterValidate["draft_validated_version"]!.GetValue<long>());

        // 改 draft → version 2、validated 清空。
        await client.SendAsync(PutDraft(id, ValidBody("a08-validated", "改"), "\"1\""));
        var afterEdit = await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync();
        Assert.Equal(2, afterEdit["draft_version"]!.GetValue<long>());
        Assert.Null(afterEdit["draft_validated_version"]);
    }

    // ---- A-DATA-09:validate 後改 draft 再 publish → 拒絕;重新驗證後可 publish ----

    [Fact]
    public async Task Publish_AfterDraftEditedPostValidate_Returns409_UntilRevalidated()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a09-slug"));
        await ValidateAsync(client, id);                                    // validated = 1
        await client.SendAsync(PutDraft(id, ValidBody("a09-slug", "改"), "\"1\"")); // version 2,validated 清空

        // draft 已漂移且未重新驗證 → publish 拒絕。
        var rejected = await PublishAsync(client, id, 2);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("尚未重新驗證", (await rejected.ReadJsonAsync())["message"]!.GetValue<string>());

        // 重新驗證同一 draft version → publish 成功。
        await ValidateAsync(client, id, 2);
        var ok = await PublishAsync(client, id, 2);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, (await ok.ReadJsonAsync())["published_revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Publish_WrongExpectedVersion_Returns409()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a09-ver"));
        await ValidateAsync(client, id);

        var resp = await PublishAsync(client, id, 99);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Publish_MissingExpectedVersion_Returns400()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a09-noexp"));
        await ValidateAsync(client, id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/publish")
        {
            Content = JsonContent.Create(new JsonObject()),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var resp = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Publish_MissingIfMatch_Returns428()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a09-no-if-match"));
        await ValidateAsync(client, id);

        var resp = await client.PostAsJsonAsync(
            $"/api/agents/{id}/publish",
            new JsonObject { ["expected_draft_version"] = 1 });

        Assert.Equal(HttpStatusCode.PreconditionRequired, resp.StatusCode);
    }

    [Fact]
    public async Task Publish_IfMatchAndBodyVersionMismatch_Returns400_WithoutPublishing()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a09-mismatch"));
        await ValidateAsync(client, id);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{id}/publish")
        {
            Content = JsonContent.Create(new JsonObject { ["expected_draft_version"] = 1 }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"2\"");

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(
            "必須指向同一個 draft version",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
        Assert.Null(
            (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["published_revision"]);
    }

    // ---- runtime_workflow 缺席 → 預設 pin 到 P0 種子(A-UI-07:Builder 不暴露 workflow id)----

    [Fact]
    public async Task Create_WithoutRuntimeWorkflow_DefaultsToSeed_ValidatesAndPublishes()
    {
        var client = Admin();
        var body = ValidBody("wf-default");
        body.Remove("runtime_workflow"); // 前端不帶此欄

        var (id, _) = await CreateAsync(client, body);

        // draft 已補上預設種子 pin。
        var draft = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!.AsObject();
        Assert.Equal(AgentDefaults.RuntimeWorkflowId, draft["runtime_workflow"]!["id"]!.GetValue<string>());
        Assert.Equal(
            AgentDefaults.RuntimeWorkflowRevision,
            draft["runtime_workflow"]!["revision"]!.GetValue<int>());

        // validate 不再恆失敗。
        Assert.True((await (await ValidateAsync(client, id)).ReadJsonAsync())["valid"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, id, 1)).StatusCode);
        var rev = Assert.Single((await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray())!;
        Assert.Equal(AgentDefaults.RuntimeWorkflowId, rev["runtime_workflow_id"]!.GetValue<string>());
        Assert.Equal(
            AgentDefaults.RuntimeWorkflowRevision,
            rev["runtime_workflow_revision"]!.GetValue<int>());
    }

    // ---- skill_bindings 去重(共用 canonicalizer,兩路徑同一份 canonical draft → 一致)----

    [Fact]
    public async Task Create_DuplicateSkillBindings_Deduped_InDraftAndPublishedRevision()
    {
        var client = Admin();
        var skill = await CreateSkillAsync(client, "dedup-skill");
        var body = ValidBody("dedup-agent");
        body["skill_bindings"] = new JsonArray(
            new JsonObject { ["skill"] = skill },
            new JsonObject { ["skill"] = skill });

        var (id, _) = await CreateAsync(client, body);

        // canonical draft 去重 → 一筆(Dapper/InMemory 都消費這份 draft)。
        var draft = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!.AsObject();
        Assert.Single(draft["skill_bindings"]!.AsArray());

        await ValidateAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, id, 1)).StatusCode);

        var rev = Assert.Single((await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray())!;
        Assert.Single(rev["skill_bindings"]!.AsArray());
    }

    // ---- publish 清 draft_validated_version:同一 draft 必須重新驗證才能再發 ----

    [Fact]
    public async Task Publish_ClearsValidated_RequiresRevalidateBeforeRepublish()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("republish"));
        await ValidateAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, id, 1)).StatusCode); // revision 1

        // 未重新驗證 → 再 publish 被拒(不產生重複 revision)。
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(client, id, 1)).StatusCode);

        // 重新驗證後才可再發 → revision 2。
        await ValidateAsync(client, id);
        var ok = await PublishAsync(client, id, 1);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await ok.ReadJsonAsync())["published_revision"]!.GetValue<int>());
    }

    // ---- A-DATA-04:publish 固定 Skill revision 與 definition hash;建立不可變 revision ----

    [Fact]
    public async Task Publish_PinsSkillCurrentRevision_AndDefinitionHash()
    {
        var client = Admin();
        var skill = await CreateSkillAsync(client, "a04-skill");

        var body = ValidBody("a04-agent");
        body["skill_bindings"] = new JsonArray(new JsonObject { ["skill"] = skill });
        var (id, _) = await CreateAsync(client, body);
        await ValidateAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, id, 1)).StatusCode);

        var revs = (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray();
        var rev = Assert.Single(revs)!;
        Assert.Equal(1, rev["revision"]!.GetValue<int>());
        Assert.Equal("published", rev["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(rev["definition_sha256"]!.GetValue<string>()));
        Assert.Equal(AgentDefaults.RuntimeWorkflowId, rev["runtime_workflow_id"]!.GetValue<string>());

        var binding = Assert.Single(rev["skill_bindings"]!.AsArray())!;
        Assert.Equal(skill, binding["skill"]!.GetValue<string>());
        Assert.Equal(1, binding["skill_revision"]!.GetValue<int>()); // 固定到 skill current_revision=1
    }

    // ---- A-DATA-05:Skill 更新後,已發布 Agent 的 pin 不變 ----

    [Fact]
    public async Task SkillUpdate_DoesNotChangePublishedAgentPin()
    {
        var client = Admin();
        var skill = await CreateSkillAsync(client, "a05-skill");

        var body = ValidBody("a05-agent");
        body["skill_bindings"] = new JsonArray(new JsonObject { ["skill"] = skill });
        var (id, _) = await CreateAsync(client, body);
        await ValidateAsync(client, id);
        await PublishAsync(client, id, 1);

        // 更新 skill → current_revision 2。
        var yaml = $"name: {skill}\ndescription: 改版\nrequired_role: USER\nflow:\n  - node: query_intake\n";
        await client.PutAsJsonAsync($"/api/skills/{skill}", new JsonObject { ["definition"] = yaml });

        var rev = Assert.Single((await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray())!;
        Assert.Equal(1, rev["skill_bindings"]!.AsArray()[0]!["skill_revision"]!.GetValue<int>()); // 仍是 1
    }

    // ---- A-DATA-06:rollback 產生新 revision,不改寫歷史 ----

    [Fact]
    public async Task Restore_CreatesNewRevision_WithoutRewritingHistory()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a06-slug"));
        await ValidateAsync(client, id);
        await PublishAsync(client, id, 1);                                          // revision 1

        await client.SendAsync(PutDraft(id, ValidBody("a06-slug", "第二版"), "\"1\"")); // version 2
        await ValidateAsync(client, id, 2);
        await PublishAsync(client, id, 2);                                          // revision 2

        // rollback 到 revision 1 → 產生 revision 3(published),歷史不動。
        var restore = await client.PostAsync($"/api/agents/{id}/revisions/1/restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal(3, (await restore.ReadJsonAsync())["published_revision"]!.GetValue<int>());

        var revs = (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(new[] { 3, 2, 1 }, revs.Select(r => r!["revision"]!.GetValue<int>()).ToArray());
        Assert.Equal("published", revs[0]!["status"]!.GetValue<string>());
        Assert.Equal("superseded", revs[1]!["status"]!.GetValue<string>());
        Assert.Equal("superseded", revs[2]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restore_MissingRevision_Returns404()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("a06-norev"));

        var resp = await client.PostAsync($"/api/agents/{id}/revisions/9/restore", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- A-DATA-11:集合欄位缺席/null/空 → canonicalize 成空陣列(fail closed,禁止 null=unrestricted)----

    [Theory]
    [InlineData("omitted")] // 不帶該 key
    [InlineData("null")]    // key 顯式 null
    [InlineData("empty")]   // key 空陣列
    public async Task Create_AllowedToolsAndKnowledge_CanonicalizeToEmpty(string mode)
    {
        var client = Admin();
        var body = ValidBody($"a11-{mode}");
        if (mode != "omitted")
        {
            JsonNode? value = mode == "empty" ? new JsonArray() : null;
            body["allowed_tools"] = value?.DeepClone();
            body["knowledge_sources"] = value?.DeepClone();
        }

        var (id, _) = await CreateAsync(client, body);

        var draft = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!.AsObject();
        Assert.Empty(draft["allowed_tools"]!.AsArray());
        Assert.Empty(draft["knowledge_sources"]!.AsArray());
        // fail closed:是明確空陣列,不是 null(絕不代表全開)。
        Assert.Equal(JsonValueKind.Array, draft["allowed_tools"]!.GetValueKind());
    }

    [Fact]
    public async Task Create_BusinessRules_AlwaysCanonicalEmptyAst()
    {
        var client = Admin();
        var body = ValidBody("a11-rules");
        // 即使送入非空 rules,本期一律存 canonical 空 AST。
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject { ["id"] = "x" }),
        };
        var (id, _) = await CreateAsync(client, body);

        var draft = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!.AsObject();
        Assert.Empty(draft["business_rules"]!["rules"]!.AsArray());
        Assert.Equal(1, draft["business_rules"]!["version"]!.GetValue<int>());
    }

    // ---- validate:決策表兩半(valid → 記錄 validated;invalid → 200 errors,不記錄)----

    [Fact]
    public async Task Validate_ValidDraft_ReturnsValidTrue()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("val-ok"));

        var body = await (await ValidateAsync(client, id)).ReadJsonAsync();
        Assert.True(body["valid"]!.GetValue<bool>());
        Assert.Empty(body["errors"]!.AsArray());
    }

    [Theory]
    [InlineData("system_prompt", "")]      // 空 system_prompt
    [InlineData("execution_roles", "[]")]  // 空 roles
    [InlineData("execution_roles", "bad")] // 非法 role
    public async Task Validate_InvalidDraft_ReturnsErrors_AndDoesNotMarkValidated(string field, string variant)
    {
        var client = Admin();
        var body = ValidBody($"val-bad-{field}-{variant}");
        if (field == "system_prompt")
        {
            body["system_prompt"] = "";
        }
        else
        {
            body["execution_roles"] = variant == "[]" ? new JsonArray() : new JsonArray("manager");
        }

        var (id, _) = await CreateAsync(client, body);
        var result = await (await ValidateAsync(client, id)).ReadJsonAsync();

        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.NotEmpty(result["errors"]!.AsArray());

        // 未記錄 validated → publish 仍被拒。
        var draft = await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync();
        Assert.Null(draft["draft_validated_version"]);
    }

    [Fact]
    public async Task Validate_BindingToMissingSkill_FailsBeforePublish()
    {
        var client = Admin();
        var body = ValidBody("bind-missing");
        body["skill_bindings"] = new JsonArray(new JsonObject { ["skill"] = "no-such-skill" });
        var (id, _) = await CreateAsync(client, body);
        var validation = await (await ValidateAsync(client, id)).ReadJsonAsync();
        Assert.False(validation["valid"]!.GetValue<bool>());
        Assert.Contains(
            "no-such-skill",
            Assert.Single(
                validation["errors"]!.AsArray(),
                e => e!["field"]!.GetValue<string>() == "skill_bindings")!["message"]!
                .GetValue<string>());

        var resp = await PublishAsync(client, id, 1);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Validate_RejectsUnknownRuntimeWorkflow_AndCatalogOnlySkill()
    {
        var client = Admin();
        var body = ValidBody("bad-refs");
        body["runtime_workflow"] = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["revision"] = 1,
        };
        body["skill_bindings"] = new JsonArray(
            new JsonObject { ["skill"] = "builtin-catalog-only" });
        var (id, _) = await CreateAsync(client, body);

        var response = await ValidateAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadJsonAsync();
        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Contains(
            result["errors"]!.AsArray(),
            e => e!["field"]!.GetValue<string>() == "runtime_workflow");
        Assert.Contains(
            result["errors"]!.AsArray(),
            e => e!["field"]!.GetValue<string>() == "skill_bindings"
                 && e["message"]!.GetValue<string>().Contains("builtin/catalog-only"));

        // Invalid validation result is never marked; publish fails closed as unvalidated.
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(client, id, 1)).StatusCode);
    }

    [Fact]
    public async Task Publish_RechecksSkillAfterValidation_AndRejectsDisabledBinding()
    {
        var client = Admin();
        var skill = await CreateSkillAsync(client, "disabled-after-validation");
        var body = ValidBody("binding-race");
        body["skill_bindings"] = new JsonArray(new JsonObject { ["skill"] = skill });
        var (id, _) = await CreateAsync(client, body);
        Assert.True(
            (await (await ValidateAsync(client, id)).ReadJsonAsync())["valid"]!.GetValue<bool>());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/skills/{skill}")).StatusCode);

        var response = await PublishAsync(client, id, 1);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.ReadJsonAsync();
        Assert.Contains(
            "disabled-after-validation",
            error["fieldErrors"]!["skill_bindings"]!.GetValue<string>());
    }

    // ---- soft disable / enable ----

    [Fact]
    public async Task Delete_SoftDisables_ThenEnableReactivates()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("toggle"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/agents/{id}")).StatusCode);
        Assert.False((await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["enabled"]!.GetValue<bool>());

        var enable = await client.PostAsync($"/api/agents/{id}/enable", null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.True((await enable.ReadJsonAsync())["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Delete_Missing_Returns404()
    {
        var resp = await Admin().DeleteAsync($"/api/agents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---- 角色:寫入需 ADMIN;GET 類開放 USER ----

    [Fact]
    public async Task NonAdmin_Create_Returns403_NotValidationError()
    {
        // 非 ADMIN 送不合法 body(缺 slug)仍是 403,不得先被 400 短路而洩漏欄位規則。
        var resp = await User().PostAsJsonAsync("/api/agents", new JsonObject { ["name"] = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("權限不足，無法存取 Agent", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
    }

    [Theory]
    [InlineData("PUT", "/draft")]
    [InlineData("POST", "/publish")]
    [InlineData("POST", "/validate")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/enable")]
    public async Task NonAdmin_Write_Returns403(string method, string suffix)
    {
        var (id, _) = await CreateAsync(Admin(), ValidBody($"role-{method}-{suffix.Trim('/')}"));

        var req = new HttpRequestMessage(new HttpMethod(method), $"/api/agents/{id}{suffix}");
        if (method is "PUT" or "POST")
        {
            req.Content = JsonContent.Create(ValidBody("x"));
        }

        var resp = await User().SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task User_CannotReadBuilderListSingleOrRevisions()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("user-read"));
        await ValidateAsync(client, id);
        await PublishAsync(client, id, 1);

        var user = User();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/agents/{id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await user.GetAsync($"/api/agents/{id}/revisions")).StatusCode);
    }

    [Fact]
    public void Canonicalize_DeepSortsObjects_AndNormalizesSetFields()
    {
        var left = ValidBody("canonical-left");
        left["allowed_tools"] = new JsonArray("z", "a", "z");
        left["output_contract"] = new JsonObject
        {
            ["z"] = new JsonObject { ["b"] = 2, ["a"] = 1 },
            ["a"] = true,
        };

        var right = ValidBody("canonical-right");
        right["allowed_tools"] = new JsonArray("a", "z");
        right["output_contract"] = new JsonObject
        {
            ["a"] = true,
            ["z"] = new JsonObject { ["a"] = 1, ["b"] = 2 },
        };

        var leftRequest = JsonSerializer.Deserialize<AgentUpsert>(left.ToJsonString())!;
        var rightRequest = JsonSerializer.Deserialize<AgentUpsert>(right.ToJsonString())!;
        var leftCanonical = AgentCanonicalizer.Canonicalize(leftRequest);
        var rightCanonical = AgentCanonicalizer.Canonicalize(rightRequest);

        Assert.Equal(leftCanonical, rightCanonical);
        Assert.Equal(SkillHash.Sha256(leftCanonical), SkillHash.Sha256(rightCanonical));
    }

    [Fact]
    public void DefaultRuntimeWorkflowFixture_HasValidExplicitStartAndEnd()
    {
        Assert.Empty(AgentDefaults.ValidateRuntimeWorkflowFixture());
        var root = JsonNode.Parse(AgentDefaults.RuntimeWorkflowDefinition)!.AsObject();
        Assert.Single(root["nodes"]!.AsArray(), n => n!["type"]!.GetValue<string>() == "start");
        Assert.Single(root["nodes"]!.AsArray(), n => n!["type"]!.GetValue<string>() == "end");
    }

    // ---- 回應領域欄位 snake_case ----

    [Fact]
    public async Task Get_ResponseFields_AreSnakeCase()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("snake"));

        var single = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync()).AsObject();
        Assert.Equal(
            new[]
            {
                "created_at", "description", "draft", "draft_validated_version", "draft_version",
                "enabled", "id", "name", "published_revision", "slug", "updated_at",
            },
            single.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }
}
