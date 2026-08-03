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

    /// <summary>
    /// 把測試用的識別標籤壓成合法 slug(create 端有 canonical 格式驗證)。
    /// 標籤只是為了讓每個測試用互不衝突的 slug,本身從不是被測對象;格式規則本身由
    /// <see cref="Create_MalformedSlug_Returns400"/> 直接背書。
    /// </summary>
    private static string Slug(string label)
        => new string(label.ToLowerInvariant()
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
                .ToArray())
            .Trim('-');

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

    private static JsonObject BodyWithRule(string slug, string ruleId)
    {
        var body = ValidBody(slug);
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject
            {
                ["id"] = ruleId,
                ["when"] = new JsonObject
                {
                    ["fact"] = "action.amount",
                    ["op"] = "gt",
                    ["value"] = 5000,
                },
                ["then"] = new JsonArray(new JsonObject { ["action"] = "deny" }),
            }),
        };
        return body;
    }

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

    /// <summary>D3 test run 起始請求(用來驗 runtime 閘門,不驗 run 生命週期本身)。</summary>
    private static HttpRequestMessage StartRun(string agentId, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{agentId}/runs")
        {
            Content = JsonContent.Create(new JsonObject { ["message"] = "請整理重點" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
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
        (await resp.ReadJsonAsync()).AssertApiError(404, "not_found");
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
        (await stale.ReadJsonAsync()).AssertApiError(409, "version_conflict");
        // 02-spec §5:repository 判別「不存在/版本不符」的那一趟查詢順手帶回當下 draft_version,
        // 所以這條最常撞的併發 409 也附得出最新 ETag —— 呼叫端直接拿 "2" 重試,不必再打一次 GET。
        Assert.Equal("\"2\"", stale.Headers.ETag!.Tag);

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
        var stale = await ValidateAsync(client, id, expectedDraftVersion: 99);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        // 02-spec §5:資源對呼叫者可見時,409 要把當下最新 ETag 一併帶回,呼叫端不必再多打一次 GET。
        // 這條 409 走 ApiErrors.VersionConflict 直接回 ObjectResult、繞開例外路徑 —— 例外路徑上
        // UseExceptionHandler 的 ClearCacheHeaders 會清掉 ETag,永遠帶不出來。
        Assert.Equal("\"1\"", stale.Headers.ETag!.Tag);

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

        // draft 已漂移且未重新驗證 → publish 拒絕,並帶回當下最新 ETag(02-spec §5)。
        var rejected = await PublishAsync(client, id, 2);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("尚未重新驗證", (await rejected.ReadJsonAsync())["message"]!.GetValue<string>());
        Assert.Equal("\"2\"", rejected.Headers.ETag!.Tag);

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

    [Fact]
    public async Task Restore_RevalidatesRules_AndDoesNotAppendWhenCurrentContractRejectsThem()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(
            client,
            BodyWithRule("restore-invalid-rules", "invalid-on-restore"));
        await ValidateAsync(client, id);
        await PublishAsync(client, id, 1);

        var restore = await client.PostAsync($"/api/agents/{id}/revisions/1/restore", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, restore.StatusCode);
        var error = await restore.ReadJsonAsync();
        Assert.Equal(
            "number fact 不可使用 string operator",
            error["fieldErrors"]!["business_rules.rules[0].when"]!.GetValue<string>());
        var revisions = (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray();
        var only = Assert.Single(revisions)!;
        Assert.Equal(1, only["revision"]!.GetValue<int>());
        Assert.Equal("published", only["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restore_WhenRuleEngineUnavailable_FailsClosedWithoutAppendingRevision()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(
            client,
            BodyWithRule("restore-engine-down", "engine-down-on-restore"));
        await ValidateAsync(client, id);
        await PublishAsync(client, id, 1);

        var restore = await client.PostAsync($"/api/agents/{id}/revisions/1/restore", null);

        Assert.Equal(HttpStatusCode.BadGateway, restore.StatusCode);
        var revisions = (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Single(revisions);
    }

    [Fact]
    public async Task Restore_PersistsNewCanonicalRulesAndHash_WithoutMutatingSourceRevision()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(
            client,
            BodyWithRule("restore-new-canonical", "canonical-on-restore"));
        await ValidateAsync(client, id);
        await PublishAsync(client, id, 1);
        var repo = _factory.Fake<IAgentRepository>();
        var agentId = Guid.Parse(id);
        var sourceBefore = await repo.GetRevisionDefinitionAsync("demo-a", agentId, 1, default);
        Assert.NotNull(sourceBefore);

        var restore = await client.PostAsync($"/api/agents/{id}/revisions/1/restore", null);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var sourceAfter = await repo.GetRevisionDefinitionAsync("demo-a", agentId, 1, default);
        var restored = await repo.GetRevisionDefinitionAsync("demo-a", agentId, 2, default);
        Assert.Equal(sourceBefore, sourceAfter);
        Assert.NotNull(restored);
        Assert.Equal(
            "deny",
            JsonNode.Parse(restored!)!["business_rules"]!["rules"]![0]!["onUnknown"]![0]!["action"]!
                .GetValue<string>());

        var revisions = (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray();
        var rev2 = Assert.Single(revisions, r => r!["revision"]!.GetValue<int>() == 2)!;
        var rev1 = Assert.Single(revisions, r => r!["revision"]!.GetValue<int>() == 1)!;
        Assert.Equal(SkillHash.Sha256(restored!), rev2["definition_sha256"]!.GetValue<string>());
        Assert.NotEqual(
            rev1["definition_sha256"]!.GetValue<string>(),
            rev2["definition_sha256"]!.GetValue<string>());
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
    public async Task Create_BusinessRules_PreservesAndCanonicalizesRealAst()
    {
        var client = Admin();
        var body = ValidBody("a11-rules");
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject
            {
                ["priority"] = 100,
                ["id"] = "refund-approval",
                ["when"] = new JsonObject
                {
                    ["fact"] = "action.amount",
                    ["value"] = 5000,
                    ["op"] = "gt",
                },
                ["then"] = new JsonArray(new JsonObject
                {
                    ["role"] = "ADMIN",
                    ["action"] = "require_approval",
                }),
            }),
        };
        var (id, _) = await CreateAsync(client, body);

        var draft = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!.AsObject();
        var rule = Assert.Single(draft["business_rules"]!["rules"]!.AsArray())!;
        Assert.Equal("refund-approval", rule["id"]!.GetValue<string>());
        Assert.Equal("gt", rule["when"]!["op"]!.GetValue<string>());
        Assert.Equal("require_approval", rule["then"]![0]!["action"]!.GetValue<string>());
        Assert.Equal(1, draft["business_rules"]!["version"]!.GetValue<int>());
        // backend 只是保存者:create 不得自行發明 Workflow 的 canonical 預設值(onUnknown 只有在
        // validate/publish 由 Workflow 回傳 canonicalRuleSet 之後才會出現)。
        Assert.Null(rule["onUnknown"]);
    }

    [Fact]
    public async Task Validate_BusinessRuleError_IsLocatedAndCoded_WithoutMarkingDraft()
    {
        var client = Admin();
        var body = ValidBody("rule-invalid-api");
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject { ["id"] = "invalid-rule" }),
        };
        var (id, _) = await CreateAsync(client, body);

        var response = await ValidateAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.ReadJsonAsync();
        Assert.False(result["valid"]!.GetValue<bool>());
        var error = Assert.Single(result["errors"]!.AsArray())!;
        Assert.Equal("business_rules.rules[0].when", error["field"]!.GetValue<string>());
        Assert.Equal("operator_type_mismatch", error["code"]!.GetValue<string>());
        Assert.Null((await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft_validated_version"]);

        var call = Assert.Single(
            _factory.Fake<IBusinessRuleValidator>() is FakeBusinessRuleValidator fake
                ? fake.Calls.Where(c => c.RuleSet.GetRawText().Contains("invalid-rule", StringComparison.Ordinal))
                : Array.Empty<FakeBusinessRuleValidator.Call>());
        Assert.Equal("pre-action", call.Gate);
        Assert.Equal("demo-a", call.TenantId);
        Assert.Equal("admin-a", call.UserId);
        Assert.Equal("ADMIN", call.Role);
        Assert.Empty(call.ReferenceCatalog.Skills);
        Assert.Empty(call.ReferenceCatalog.Tools);
    }

    [Fact]
    public async Task Validate_ForwardsAgentSkillAndToolAllowlistsToRuleEngine()
    {
        var client = Admin();
        var skill = await CreateSkillAsync(client, "rule-reference-skill");
        var body = BodyWithRule("rule-reference-catalog", "reference-catalog-forwarded");
        body["skill_bindings"] = new JsonArray(
            new JsonObject { ["skill"] = skill, ["revision_policy"] = "latest" });
        body["allowed_tools"] = new JsonArray("z-read-tool", "a-read-tool");
        var (id, _) = await CreateAsync(client, body);

        var response = await ValidateAsync(client, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fake = Assert.IsType<FakeBusinessRuleValidator>(_factory.Fake<IBusinessRuleValidator>());
        var call = Assert.Single(
            fake.Calls,
            c => c.RuleSet.GetRawText()
                .Contains("reference-catalog-forwarded", StringComparison.Ordinal));
        Assert.Equal(new[] { skill }, call.ReferenceCatalog.Skills);
        Assert.Equal(new[] { "a-read-tool", "z-read-tool" }, call.ReferenceCatalog.Tools);
    }

    [Fact]
    public async Task ConcurrentPublish_SameEtag_AppendsExactlyOneRevision()
    {
        var client = Admin();
        var body = BodyWithRule("rule-concurrent-publish", "concurrent-publish");
        var (id, _) = await CreateAsync(client, body);
        Assert.True((await (await ValidateAsync(client, id)).ReadJsonAsync())["valid"]!.GetValue<bool>());

        var fake = Assert.IsType<FakeBusinessRuleValidator>(_factory.Fake<IBusinessRuleValidator>());
        fake.CoordinateNextCalls(2);

        var first = PublishAsync(client, id, 1);
        var second = PublishAsync(client, id, 1);
        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var revisions = (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray();
        Assert.Single(revisions);
        Assert.Equal(1, revisions[0]!["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Publish_AtomicallyPersistsLatestWorkflowCanonicalRulesAndHash()
    {
        var client = Admin();
        var body = BodyWithRule("rule-canonical-on-publish", "canonical-on-publish");
        var (id, _) = await CreateAsync(client, body);
        Assert.True((await (await ValidateAsync(client, id)).ReadJsonAsync())["valid"]!.GetValue<bool>());

        var publish = await PublishAsync(client, id, 1);

        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var stored = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!;
        Assert.Equal(
            "deny",
            stored["business_rules"]!["rules"]![0]!["onUnknown"]![0]!["action"]!
                .GetValue<string>());
        var revision = Assert.Single(
            (await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync()).AsArray())!;
        Assert.Equal(SkillHash.Sha256(stored.ToJsonString()), revision["definition_sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Validate_PersistsWorkflowCanonicalRuleSet_AndPublishHashesIt()
    {
        var client = Admin();
        var body = ValidBody("rule-canonical-persist");
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject
            {
                ["id"] = "needs-canonical-default",
                ["when"] = new JsonObject { ["fact"] = "action.amount", ["op"] = "gt", ["value"] = 5000 },
                ["then"] = new JsonArray(new JsonObject { ["action"] = "deny" }),
            }),
        };
        var (id, _) = await CreateAsync(client, body);

        Assert.True((await (await ValidateAsync(client, id)).ReadJsonAsync())["valid"]!.GetValue<bool>());
        var stored = (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft"]!;
        Assert.Equal(
            "deny",
            stored["business_rules"]!["rules"]![0]!["onUnknown"]!.GetValue<string>());
        var canonicalHash = SkillHash.Sha256(stored.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, id, 1)).StatusCode);
        var revisions = await (await client.GetAsync($"/api/agents/{id}/revisions")).ReadJsonAsync();
        Assert.Equal(canonicalHash, revisions![0]!["definition_sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Publish_RevalidatesBusinessRules_AndFailsClosed()
    {
        var client = Admin();
        var body = ValidBody("rule-publish-recheck");
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject { ["id"] = "invalid-on-publish" }),
        };
        var (id, _) = await CreateAsync(client, body);
        Assert.True((await (await ValidateAsync(client, id)).ReadJsonAsync())["valid"]!.GetValue<bool>());

        var publish = await PublishAsync(client, id, 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
        var error = await publish.ReadJsonAsync();
        Assert.Equal(
            "number fact 不可使用 string operator",
            error["fieldErrors"]!["business_rules.rules[0].when"]!.GetValue<string>());
    }

    [Fact]
    public async Task Validate_WhenRuleEngineUnavailable_Returns502_AndDoesNotMarkDraft()
    {
        var client = Admin();
        var body = ValidBody("rule-engine-down-api");
        body["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject { ["id"] = "engine-down" }),
        };
        var (id, _) = await CreateAsync(client, body);

        var response = await ValidateAsync(client, id);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null((await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())["draft_validated_version"]);
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

    [Fact]
    public async Task Validate_KnowledgeSourcesRequireCanonicalDocumentUuids()
    {
        var client = Admin();
        var body = ValidBody("val-source-id");
        body["knowledge_sources"] = new JsonArray("not-a-document-uuid");
        var (id, _) = await CreateAsync(client, body);

        var result = await (await ValidateAsync(client, id)).ReadJsonAsync();

        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Contains(
            result["errors"]!.AsArray(),
            error => error!["field"]!.GetValue<string>() == "knowledge_sources");
    }

    [Fact]
    public async Task Validate_RejectsDefinitionsOutsideD3ExecutionSnapshotBounds()
    {
        var client = Admin();
        var body = ValidBody(
            $"d3-snapshot-bounds-{Guid.NewGuid():N}",
            new string('n', AgentExecutionContract.MaxAgentNameLength + 1));
        body["system_prompt"] =
            new string('p', AgentExecutionContract.MaxSystemPromptLength + 1);
        body["audience"] = new JsonArray(
            Enumerable.Range(0, AgentExecutionContract.MaxAudience + 1)
                .Select(index => JsonValue.Create($"group:role-{index}"))
                .ToArray());
        body["allowed_tools"] = new JsonArray(
            Enumerable.Range(0, AgentExecutionContract.MaxAllowedTools + 1)
                .Select(index => JsonValue.Create($"tool-{index}"))
                .ToArray());
        body["knowledge_sources"] = new JsonArray(
            Enumerable.Range(0, AgentExecutionContract.MaxKnowledgeSources + 1)
                .Select(_ => JsonValue.Create(Guid.NewGuid().ToString("D")))
                .ToArray());
        body["skill_bindings"] = new JsonArray(
            Enumerable.Range(0, AgentExecutionContract.MaxSkillBindings + 1)
                .Select(index => (JsonNode)new JsonObject
                {
                    ["skill"] = $"bound-skill-{index}",
                    ["revision_policy"] = "latest",
                })
                .ToArray());
        body["output_contract"] = new JsonObject();
        body["runtime_limits"] = new JsonObject
        {
            ["max_tool_rounds"] = -1,
            ["max_context_rounds"] = AgentExecutionContract.MaxContextRounds + 1,
            ["timeout_seconds"] = AgentExecutionContract.MaxTimeoutSeconds + 1,
            ["token_budget"] = AgentExecutionContract.MaxTokenBudget + 1,
            ["step_budget"] = AgentExecutionContract.MaxStepBudget + 1,
        };
        var (id, _) = await CreateAsync(client, body);

        var result = await (await ValidateAsync(client, id)).ReadJsonAsync();

        Assert.False(result["valid"]!.GetValue<bool>());
        var fields = result["errors"]!.AsArray()
            .Select(error => error!["field"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("name", fields);
        Assert.Contains("system_prompt", fields);
        Assert.Contains("audience", fields);
        Assert.Contains("allowed_tools", fields);
        Assert.Contains("knowledge_sources", fields);
        Assert.Contains("skill_bindings", fields);
        Assert.Contains("runtime_limits.max_tool_rounds", fields);
        Assert.Contains("runtime_limits.max_context_rounds", fields);
        Assert.Contains("runtime_limits.timeout_seconds", fields);
        Assert.Contains("runtime_limits.token_budget", fields);
        Assert.Contains("runtime_limits.step_budget", fields);
        Assert.Null(
            (await (await client.GetAsync($"/api/agents/{id}")).ReadJsonAsync())
            ["draft_validated_version"]);
    }

    [Fact]
    public void ExecutionSnapshotContract_AcceptsExactPublishedBounds()
    {
        var definition = AgentCanonicalizer.Canonicalize(new AgentUpsert(
            Slug: null,
            Name: null,
            Description: null,
            SystemPrompt: new string('p', AgentExecutionContract.MaxSystemPromptLength),
            ExecutionRoles: new[] { "worker", "verifier" },
            Capabilities: null,
            OutputContract: JsonSerializer.SerializeToElement(new { type = "object" }),
            Audience: Enumerable.Range(0, AgentExecutionContract.MaxAudience)
                .Select(index => $"group:role-{index}")
                .ToArray(),
            AllowedTools: Enumerable.Range(0, AgentExecutionContract.MaxAllowedTools)
                .Select(index => $"tool-{index}")
                .ToArray(),
            SkillBindings: Enumerable.Range(0, AgentExecutionContract.MaxSkillBindings)
                .Select(index => new AgentSkillBinding($"skill-{index}"))
                .ToArray(),
            KnowledgeSources: Enumerable.Range(0, AgentExecutionContract.MaxKnowledgeSources)
                .Select(_ => Guid.NewGuid().ToString("D"))
                .ToArray(),
            BusinessRules: null,
            RuntimeLimits: new AgentRuntimeLimits(
                AgentExecutionContract.MaxToolRounds,
                AgentExecutionContract.MaxContextRounds,
                AgentExecutionContract.MaxTimeoutSeconds,
                AgentExecutionContract.MaxTokenBudget,
                AgentExecutionContract.MaxStepBudget),
            RuntimeWorkflow: new AgentWorkflowRef(
                AgentDefaults.RuntimeWorkflowId,
                AgentDefaults.RuntimeWorkflowRevision)));

        Assert.Empty(AgentCanonicalizer.Validate(
            definition,
            new string('n', AgentExecutionContract.MaxAgentNameLength)));
    }

    // 型別錯誤等價類:Canonicalize 只在缺席/null 時才補預設物件,呼叫端送什麼 JSON 型別就原樣保留,
    // 所以非 object 的 output_contract / business_rules 會一路帶到 Validate 的這兩個 branch。
    [Theory]
    [InlineData("output_contract", "\"not-an-object\"", "output_contract 必須是 JSON object")]
    [InlineData("business_rules", "[]", "business_rules 必須是 JSON object")]
    public void Validate_NonObjectOutputContractOrBusinessRules_ReportsTypeError(
        string field, string rawJson, string expectedMessage)
    {
        var body = ValidBody($"non-object-{field}");
        body[field] = JsonNode.Parse(rawJson);

        var definition = AgentCanonicalizer.Canonicalize(body.Deserialize<AgentUpsert>()!);

        var error = Assert.Single(AgentCanonicalizer.Validate(definition));
        Assert.Equal(field, error.Field);
        Assert.Equal(expectedMessage, error.Message);
    }

    // 筆數上限的兩側(16 通過 / 17 拒絕)。斷言鎖在 ValidateList 的計數訊息:只斷言「有沒有
    // execution_roles 錯誤」抓不到上限被改掉 —— 這些名稱本來就各自會觸發「不支援的 execution role」。
    [Theory]
    [InlineData(AgentExecutionContract.MaxExecutionRoles, false)]
    [InlineData(AgentExecutionContract.MaxExecutionRoles + 1, true)]
    public void Validate_ExecutionRolesCountBoundary_RejectsOnlyAboveMax(
        int roleCount, bool expectTooMany)
    {
        var body = ValidBody($"roles-{roleCount}");
        body["execution_roles"] = new JsonArray(
            Enumerable.Range(0, roleCount)
                .Select(index => JsonValue.Create($"role-{index}"))
                .ToArray());

        var errors = AgentCanonicalizer.Validate(
            AgentCanonicalizer.Canonicalize(body.Deserialize<AgentUpsert>()!));

        Assert.Equal(
            expectTooMany,
            errors.Any(error => error.Message
                == $"execution_roles 最多 {AgentExecutionContract.MaxExecutionRoles} 筆"));
    }

    [Theory]
    [InlineData("system_prompt", "")]      // 空 system_prompt
    [InlineData("execution_roles", "[]")]  // 空 roles
    [InlineData("execution_roles", "bad")] // 非法 role
    public async Task Validate_InvalidDraft_ReturnsErrors_AndDoesNotMarkValidated(string field, string variant)
    {
        var client = Admin();
        var body = ValidBody(Slug($"val-bad-{field}-{variant}"));
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

    // 「不存在的 skill」與「builtin/catalog-only」在 InMemory 路徑同屬「查不到可固定的 persisted
    // revision」等價類(InMemoryAgentRepository.ResolveReferencesUnsafe 發同一句訊息);合併為一條,
    // 保留原本兩條各自的增量斷言:訊息含 skill 名 + 訊息含 builtin/catalog-only 說明 + unknown workflow 半邊。
    [Fact]
    public async Task Validate_RejectsUnknownRuntimeWorkflow_AndUnbindableSkill()
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
        var bindingMessage = Assert.Single(
            result["errors"]!.AsArray(),
            e => e!["field"]!.GetValue<string>() == "skill_bindings")!["message"]!
            .GetValue<string>();
        Assert.Contains("「builtin-catalog-only」", bindingMessage);
        Assert.Contains("builtin/catalog-only", bindingMessage);

        // Invalid validation result is never marked; publish fails closed as unvalidated.
        Assert.Equal(HttpStatusCode.Conflict, (await PublishAsync(client, id, 1)).StatusCode);
    }

    // 「id 格式就不合法」等價類(上一條測的是格式合法但查無此 workflow,由 repo 的 reference 解析擋下):
    // ToWorkflow 原樣放行任何非空字串 → create 仍是 201,由 canonicalizer 自己的 Guid.TryParse branch 擋。
    [Fact]
    public async Task Validate_MalformedRuntimeWorkflowId_ReportsCanonicalIdError()
    {
        var client = Admin();
        var body = ValidBody("wf-malformed-id");
        body["runtime_workflow"] = new JsonObject { ["id"] = "not-a-guid", ["revision"] = 1 };
        var (id, _) = await CreateAsync(client, body);

        var result = await (await ValidateAsync(client, id)).ReadJsonAsync();

        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Contains(
            result["errors"]!.AsArray(),
            e => e!["field"]!.GetValue<string>() == "runtime_workflow"
                 && e!["message"]!.GetValue<string>()
                     == "runtime_workflow.id 必須是已發布 agent-runtime Workflow 的合法 id");
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

    // 全部寫入路由共用 class-level [AdminOnly](AgentController.cs:18)→ 同一等價類;
    // 各留一個 body-carrying(PUT)與一個 no-body(DELETE)代表值即可,新路由自動繼承。
    [Theory]
    [InlineData("PUT", "/draft")]
    [InlineData("DELETE", "")]
    public async Task NonAdmin_Write_Returns403(string method, string suffix)
    {
        var (id, _) = await CreateAsync(Admin(), ValidBody(Slug($"role-{method}-{suffix.Trim('/')}")));

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
        left["business_rules"] = new JsonObject
        {
            ["rules"] = new JsonArray(new JsonObject
            {
                ["when"] = new JsonObject { ["value"] = 2, ["fact"] = "action.amount", ["op"] = "gt" },
                ["id"] = "canonical-rule",
            }),
            ["version"] = 1,
        };

        var right = ValidBody("canonical-right");
        right["allowed_tools"] = new JsonArray("a", "z");
        right["output_contract"] = new JsonObject
        {
            ["a"] = true,
            ["z"] = new JsonObject { ["a"] = 1, ["b"] = 2 },
        };
        right["business_rules"] = new JsonObject
        {
            ["version"] = 1,
            ["rules"] = new JsonArray(new JsonObject
            {
                ["id"] = "canonical-rule",
                ["when"] = new JsonObject { ["fact"] = "action.amount", ["op"] = "gt", ["value"] = 2 },
            }),
        };

        var leftRequest = JsonSerializer.Deserialize<AgentUpsert>(left.ToJsonString())!;
        var rightRequest = JsonSerializer.Deserialize<AgentUpsert>(right.ToJsonString())!;
        var leftCanonical = AgentCanonicalizer.Canonicalize(leftRequest);
        var rightCanonical = AgentCanonicalizer.Canonicalize(rightRequest);

        Assert.Equal(leftCanonical, rightCanonical);
        Assert.Equal(SkillHash.Sha256(leftCanonical), SkillHash.Sha256(rightCanonical));

        // 模擬 jsonb::text 以不同 object key order 讀回；完整 definition 再 canonicalize 後
        // 必須和 in-memory 原文/雜湊一致。
        var reordered = new JsonObject(
            JsonNode.Parse(leftCanonical)!.AsObject()
                .Reverse()
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        var afterJsonbRoundTrip = AgentCanonicalizer.CanonicalizeDefinition(reordered.ToJsonString());
        Assert.Equal(leftCanonical, afterJsonbRoundTrip);
        Assert.Equal(SkillHash.Sha256(leftCanonical), SkillHash.Sha256(afterJsonbRoundTrip));
    }

    // ---- soft-disable 的兩面:管理/稽核仍可寫,runtime 閘門關閉 ----

    [Fact]
    public async Task Disabled_Agent_RemainsManageableButIsNotRuntimeVisible()
    {
        var client = Admin();
        var body = ValidBody("soft-disabled");
        body["audience"] = new JsonArray("role:ADMIN"); // run 需要 audience 命中,才能證明擋下來的是 enabled 閘門
        var (id, _) = await CreateAsync(client, body);
        await ValidateAsync(client, id);
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(client, id, 1)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.SendAsync(StartRun(id, "soft-disabled-enabled"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/agents/{id}")).StatusCode);

        // runtime:已發布但停用的 Agent 不可執行(不是靜默放行)。
        var blocked = await client.SendAsync(StartRun(id, "soft-disabled-blocked"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(
            "找不到可執行的已發布 Agent",
            (await blocked.ReadJsonAsync())["message"]!.GetValue<string>());

        // 管理/稽核:soft-disable 不是寫入凍結 — draft/validate/publish 仍走得完,產生 revision 2。
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(PutDraft(id, body, "\"1\""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ValidateAsync(client, id, 2)).StatusCode);
        var republish = await PublishAsync(client, id, 2);
        Assert.Equal(HttpStatusCode.OK, republish.StatusCode);
        Assert.Equal(2, (await republish.ReadJsonAsync())["published_revision"]!.GetValue<int>());
    }

    // ---- lifecycle 寫入前的 audience 遷移(CanonicalizeForLifecycleWrite)----

    [Fact]
    public async Task Validate_MigratesLegacyBareRoleAudience_ButRejectsLegacyBareGroup()
    {
        var client = Admin();

        // 裸 role 只可能存在於舊資料(Canonicalize 會即時遷移),因此直接寫進 repo 造出 legacy draft。
        var legacyNode = JsonNode.Parse(AgentCanonicalizer.Canonicalize(
            ValidBody("legacy-bare-role").Deserialize<AgentUpsert>()!))!.AsObject();
        legacyNode["audience"] = new JsonArray("ADMIN");
        var legacy = AgentCanonicalizer.CanonicalizeDefinition(legacyNode.ToJsonString());
        var created = await _factory.Fake<IAgentRepository>().CreateAsync(
            "demo-a", "legacy-bare-role", "legacy", string.Empty,
            legacy, SkillHash.Sha256(legacy), "admin-a", default);
        Assert.NotNull(created);

        var validate = await ValidateAsync(client, created!.Id.ToString());

        Assert.True((await validate.ReadJsonAsync())["valid"]!.GetValue<bool>());
        var migrated = await (await client.GetAsync($"/api/agents/{created.Id}")).ReadJsonAsync();
        Assert.Equal(
            new[] { "role:ADMIN" },
            migrated["draft"]!["audience"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        // 就地遷移改寫了 draft bytes,但沿用同一個 ETag(不 bump draft_version)。
        Assert.Equal(1, migrated["draft_version"]!.GetValue<long>());
        Assert.Equal(1, migrated["draft_validated_version"]!.GetValue<long>());
        Assert.Equal("\"1\"", validate.Headers.ETag!.Tag);

        // 裸 group 字串不被當成 canonical:必須顯式改成 group:<id>,validate 直接報 audience 錯誤。
        var groupBody = ValidBody("legacy-bare-group");
        groupBody["audience"] = new JsonArray("finance-reviewers");
        var (groupId, _) = await CreateAsync(client, groupBody);
        var result = await (await ValidateAsync(client, groupId)).ReadJsonAsync();
        Assert.False(result["valid"]!.GetValue<bool>());
        Assert.Contains(
            result["errors"]!.AsArray(),
            e => e!["field"]!.GetValue<string>() == "audience");
    }

    // ---- If-Match 語法:非數字 / weak ETag / 萬用字元 / 低於下界一律 400(不是 428/409/500)----
    // 訊息改為 Common/VersionEtags.RequireIfMatchVersion 的共用字串:四個 controller 的 If-Match
    // 解析已統一到同一個 helper(原本 Agent 專屬的中文訊息與另外三份逐字重複的實作一併移除)。
    // "0" 是 draft_version 下界(第一版就是 1)的 off-point:共用 helper 一律拒絕 version < 1。

    [Theory]
    [InlineData("abc")]
    [InlineData("W/\"1\"")]
    [InlineData("*")] // HTTP 語意上代表「任何現存資源」;此處刻意不支援
    [InlineData("\"0\"")]
    public async Task IfMatch_MalformedOrWildcard_Returns400(string ifMatch)
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody($"if-match-{Guid.NewGuid():N}"));

        var resp = await client.SendAsync(PutDraft(id, ValidBody("ignored"), ifMatch));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(
            "If-Match header is invalid",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // ---- 身分 header 缺席:tenant 是 per-handler 400,role 是 filter 403 ----

    [Fact]
    public async Task MissingTenantOrRoleHeader_FailsClosedOnAgentRoutes()
    {
        var (id, _) = await CreateAsync(Admin(), ValidBody("header-gate"));

        var tenantless = await _factory.CreateInternalClient().WithRole("ADMIN").WithUser("admin-a")
            .GetAsync($"/api/agents/{id}");
        Assert.Equal(HttpStatusCode.BadRequest, tenantless.StatusCode);
        Assert.Equal(
            "缺少租戶識別標頭：X-Tenant-Id",
            (await tenantless.ReadJsonAsync())["message"]!.GetValue<string>());

        // 完全不帶 X-User-Role(不是 USER,是缺 header)→ 403,不是 500/200。
        var roleless = await _factory.CreateInternalClient().WithTenant("demo-a").WithUser("admin-a")
            .GetAsync($"/api/agents/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, roleless.StatusCode);
        Assert.Equal(
            "權限不足，無法存取 Agent",
            (await roleless.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("slug")]
    [InlineData("name")]
    public async Task Create_BlankSlugOrName_Returns400(string field)
    {
        var body = ValidBody("a10-blank");
        body[field] = "   "; // 只有空白 → Require() 視為空

        var resp = await Admin().PostAsJsonAsync("/api/agents", body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal($"{field} 不可為空", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // slug 是 UNIQUE(tenant_id, slug) 的顯示鍵(路由一律 uuid),但先前完全沒有格式驗證:
    // 路徑片段、內含空白、非 ASCII、超長一律 201。比照 AgentAudience 的 canonical group id 加 regex + 長度上限。
    // slug 是 immutable(PUT 忽略、UPDATE 不含該欄、canonical 定義排除)且 validate/publish/restore 驗的是
    // Name,故**只在 create 驗證**:既有不合規的 Agent 不會在其他路徑上被追溯打爆。
    [Theory]
    [InlineData("../../etc")]
    [InlineData("has space")]
    [InlineData("研究助手")]
    [InlineData("UPPER")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    public async Task Create_MalformedSlug_Returns400(string slug)
    {
        var resp = await Admin().PostAsJsonAsync("/api/agents", ValidBody(slug));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(400, body["status"]!.GetValue<int>());
        Assert.Equal("slug 格式不正確", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
    }

    // 長度上限的兩側:剛好 128 通過、129 拒絕(只測「8000 字元被擋」抓不到把上限打錯的改動)。
    [Fact]
    public async Task Create_SlugLengthBoundary_AcceptsMaxRejectsMaxPlusOne()
    {
        var atMax = new string('a', AgentAudience.MaxGroupIdLength);
        var overMax = new string('b', AgentAudience.MaxGroupIdLength + 1);

        Assert.Equal(
            HttpStatusCode.Created,
            (await Admin().PostAsJsonAsync("/api/agents", ValidBody(atMax))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Admin().PostAsJsonAsync("/api/agents", ValidBody(overMax))).StatusCode);
    }

    [Fact]
    public async Task List_ResponseFields_ExcludeDraftDefinition()
    {
        var client = Admin();
        var (id, _) = await CreateAsync(client, ValidBody("list-shape"));

        var item = Assert.Single(
            (await (await client.GetAsync("/api/agents")).ReadJsonAsync()).AsArray(),
            n => n!["id"]!.GetValue<string>() == id)!.AsObject();

        // 管理列表不批量外洩 prompt/policy:AgentInfo 沒有 draft 欄位。
        Assert.Equal(
            new[]
            {
                "created_at", "description", "draft_validated_version", "draft_version",
                "enabled", "id", "name", "published_revision", "slug", "updated_at",
            },
            item.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// 跨服務向量:backend 會**重新排序** Workflow 回傳的 canonicalRuleSet key。今天 hash 是 backend 自己算的
    /// 所以無害,但一旦 Workflow 對自己的 canonical bytes 簽章,兩邊 canonical form 分歧就要在這裡先爆。
    /// </summary>
    [Fact]
    public void WorkflowCanonicalRuleSet_ReSortIsByteStable()
    {
        const string workflowCanonical =
            """{"rules":[{"when":{"value":5000,"op":"gt","fact":"action.amount"},"then":[{"action":"deny"}],"onUnknown":[{"action":"deny"}],"id":"vector-rule"}],"version":1}""";
        const string expected =
            """{"rules":[{"id":"vector-rule","onUnknown":[{"action":"deny"}],"then":[{"action":"deny"}],"when":{"fact":"action.amount","op":"gt","value":5000}}],"version":1}""";
        var definition = AgentCanonicalizer.Canonicalize(
            ValidBody("rule-vector").Deserialize<AgentUpsert>()!);

        using var fromWorkflow = JsonDocument.Parse(workflowCanonical);
        var stored = AgentCanonicalizer.WithBusinessRules(definition, fromWorkflow.RootElement);

        Assert.Equal(expected, JsonNode.Parse(stored)!["business_rules"]!.ToJsonString());
        // 已 canonical 的 AST 再套一次 → bytes 不變(穩定點,不是每次都重排出新形狀)。
        using var again = JsonDocument.Parse(expected);
        Assert.Equal(stored, AgentCanonicalizer.WithBusinessRules(stored, again.RootElement));
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
