using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Api.Tests;

/// <summary>
/// Skill CRUD(驗收案例 AT4-01 ~ AT4-08 + revision 歷史)。
/// body 只有 definition(YAML 原文):name/description/required_role 都寫在 YAML 裡,
/// 由引擎 validate 回報中繼資料 — backend 不解析 YAML。
/// fake repository/validator 為 class fixture 共用 → 每個測試用自己的 skill 名稱,避免相互汙染。
/// </summary>
public sealed class SkillsApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SkillsApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant);

    private HttpClient User(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("USER").WithTenant(tenant);

    private FakeSkillValidator Validator => (FakeSkillValidator)_factory.Fake<ISkillValidator>();

    /// <summary>合法的 Skill YAML(fake 驗證器逐行掃 name/description/required_role)。</summary>
    private static string Yaml(string name, string description = "季報問答", string requiredRole = "USER")
        => $"name: {name}\ndescription: {description}\nrequired_role: {requiredRole}\nflow:\n  - node: query_intake\n";

    /// <summary>會被引擎打回的 YAML(fake 驗證器看到 marker 就回 unbounded_loop + unknown_node)。</summary>
    private static string InvalidYaml(string name)
        => $"name: {name}\ndescription: 壞的\nflow:\n  - loop:\n      body: [{FakeSkillValidator.InvalidMarker}]\n";

    private static JsonObject Body(string? definition) => new() { ["definition"] = definition };

    private async Task<JsonNode> CreateAsync(HttpClient client, string name, string description = "季報問答")
    {
        var resp = await client.PostAsJsonAsync("/api/skills", Body(Yaml(name, description)));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.ReadJsonAsync();
    }

    // ---- AT4-01:POST 建立時呼叫 workflow validate,通過後寫入 skill + revision 1 ----

    [Fact]
    public async Task Post_CallsWorkflowValidate_ThenWritesSkillAndRevision1()
    {
        var yaml = Yaml("at401_skill");

        var resp = await Admin().PostAsJsonAsync("/api/skills", Body(yaml));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // 引擎驗證被呼叫且拿到的是 YAML 原文 + 呼叫者身分。
        var call = Assert.Single(Validator.Calls, c => c.Definition == yaml);
        Assert.Equal("demo-a", call.TenantId);
        Assert.Equal("ADMIN", call.Role);

        // name/description/required_role 取自引擎回報的中繼資料(不是 body 另外帶的欄位)。
        var body = await resp.ReadJsonAsync();
        Assert.Equal("at401_skill", body["name"]!.GetValue<string>());
        Assert.Equal("季報問答", body["description"]!.GetValue<string>());
        Assert.Equal("USER", body["required_role"]!.GetValue<string>());
        Assert.Equal(yaml, body["definition"]!.GetValue<string>());
        Assert.Equal(1, body["current_revision"]!.GetValue<int>());

        // revision 1 落地,definition_sha256 對應原文。
        var revisions = (await (await Admin().GetAsync("/api/skills/at401_skill/revisions")).ReadJsonAsync()).AsArray();
        var rev = Assert.Single(revisions)!;
        Assert.Equal(1, rev["revision"]!.GetValue<int>());
        Assert.Equal(yaml, rev["definition"]!.GetValue<string>());
        Assert.Equal(SkillHash.Sha256(yaml), rev["definition_sha256"]!.GetValue<string>());
    }

    // ---- AT4-02:驗證失敗 → 422 + fieldErrors 帶引擎錯誤碼;DB 未寫入 ----

    [Fact]
    public async Task Post_EngineRejects_Returns422_WithEngineCodes_AndWritesNothing()
    {
        var resp = await Admin().PostAsJsonAsync("/api/skills", Body(InvalidYaml("at402_skill")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(422, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 定義驗證失敗", body["message"]!.GetValue<string>());
        Assert.NotNull(body["timestamp"]);

        // fieldErrors 的 key 就是引擎錯誤碼;有行號的附在訊息尾。
        var fieldErrors = body["fieldErrors"]!.AsObject();
        Assert.Equal("loop 缺少 max_iterations（第 7 行）", fieldErrors["unbounded_loop"]!.GetValue<string>());
        Assert.Equal("節點不存在：no_such_node", fieldErrors["unknown_node"]!.GetValue<string>());

        // DB 未寫入:清單看不到,單筆 404,revision 也 404。
        var list = (await (await Admin().GetAsync("/api/skills")).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(list, n => n!["name"]!.GetValue<string>() == "at402_skill");
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync("/api/skills/at402_skill")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await Admin().GetAsync("/api/skills/at402_skill/revisions")).StatusCode);
    }

    [Fact] // AT4-02 的另半邊:PUT 也走同一道閘門(不能只擋 POST),且既有定義不得被改動。
    public async Task Put_EngineRejects_Returns422_AndKeepsPreviousDefinition()
    {
        var client = Admin();
        var original = Yaml("at402_put");
        await client.PostAsJsonAsync("/api/skills", Body(original));

        var resp = await client.PutAsJsonAsync("/api/skills/at402_put", Body(InvalidYaml("at402_put")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.NotNull((await resp.ReadJsonAsync())["fieldErrors"]!["unbounded_loop"]);

        var current = await (await client.GetAsync("/api/skills/at402_put")).ReadJsonAsync();
        Assert.Equal(original, current["definition"]!.GetValue<string>());
        Assert.Equal(1, current["current_revision"]!.GetValue<int>());
    }

    // ---- AT4-03:PUT 產生新 revision ----

    [Fact]
    public async Task Put_BumpsRevision_AndAppendsRevisionRow()
    {
        var client = Admin();
        await CreateAsync(client, "at403_skill");
        var updated = Yaml("at403_skill", description: "改過的描述");

        var resp = await client.PutAsJsonAsync("/api/skills/at403_skill", Body(updated));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(2, body["current_revision"]!.GetValue<int>());
        Assert.Equal("改過的描述", body["description"]!.GetValue<string>());

        // skill_revision 兩筆,遞減排序,r2 的 sha256 對應新內容。
        var revisions = (await (await client.GetAsync("/api/skills/at403_skill/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2, revisions[0]!["revision"]!.GetValue<int>());
        Assert.Equal(1, revisions[1]!["revision"]!.GetValue<int>());
        Assert.Equal(updated, revisions[0]!["definition"]!.GetValue<string>());
        Assert.Equal(SkillHash.Sha256(updated), revisions[0]!["definition_sha256"]!.GetValue<string>());
        Assert.Equal(SkillHash.Sha256(Yaml("at403_skill")), revisions[1]!["definition_sha256"]!.GetValue<string>());
    }

    [Fact] // PUT 的 YAML name 必須等於路由 name,不等 → 422(否則會「改 A 卻改到 B」)。
    public async Task Put_NameMismatch_Returns422_AndDoesNotTouchEitherSkill()
    {
        var client = Admin();
        await CreateAsync(client, "at403_a");
        await CreateAsync(client, "at403_b");

        var resp = await client.PutAsJsonAsync("/api/skills/at403_a", Body(Yaml("at403_b", description: "偷改 B")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("Skill 定義的 name 與路由不符：定義為 at403_b，路由為 at403_a", body["message"]!.GetValue<string>());
        Assert.NotNull(body["fieldErrors"]!["name"]);

        // 兩邊都沒被動到(revision 都還是 1)。
        foreach (var name in new[] { "at403_a", "at403_b" })
        {
            var current = await (await client.GetAsync("/api/skills/" + name)).ReadJsonAsync();
            Assert.Equal(1, current["current_revision"]!.GetValue<int>());
            Assert.Equal("季報問答", current["description"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Put_Missing_Returns404()
    {
        var resp = await Admin().PutAsJsonAsync("/api/skills/at403_ghost", Body(Yaml("at403_ghost")));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("找不到 Skill：at403_ghost", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // UpdateAsync 的 `AND enabled`:軟刪後的名字不得靠 PUT 復活(復活只有 POST 那條路,
    // 它才會走 revision 接續語意)。少了那個條件,PUT 會把已停用的列改回可見且完全沒有測試會紅。
    [Fact]
    public async Task Put_SoftDeletedSkill_Returns404()
    {
        var client = Admin();
        await CreateAsync(client, "at403_deleted");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/skills/at403_deleted")).StatusCode);

        var resp = await client.PutAsJsonAsync(
            "/api/skills/at403_deleted", Body(Yaml("at403_deleted", description: "偷偷復活")));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("找不到 Skill：at403_deleted", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());

        // 仍然不可見,且沒有多出 revision(歷史只有軟刪前的那一筆)。
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/skills/at403_deleted")).StatusCode);
        Assert.Single((await (await client.GetAsync("/api/skills/at403_deleted/revisions")).ReadJsonAsync()).AsArray());
    }

    // ---- AT4-04:DELETE 軟刪 enabled=false 且 revision 保留 ----

    [Fact]
    public async Task Delete_SoftDeletes_HidesFromReads_ButKeepsRevisions()
    {
        var client = Admin();
        await CreateAsync(client, "at404_skill");
        await client.PutAsJsonAsync("/api/skills/at404_skill", Body(Yaml("at404_skill", description: "第二版")));

        var del = await client.DeleteAsync("/api/skills/at404_skill");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // 清單不再列出;單筆 404。
        var list = (await (await client.GetAsync("/api/skills")).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(list, n => n!["name"]!.GetValue<string>() == "at404_skill");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/skills/at404_skill")).StatusCode);

        // 稽核紅線:revision 歷史一列都沒少,軟刪後仍查得到。
        var revisions = (await (await client.GetAsync("/api/skills/at404_skill/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(2, revisions.Count);

        // 二次刪除 → 404(已停用等同不存在);與「名稱從未存在」是同一分支(`!deleted → NotFound`)。
        var again = await client.DeleteAsync("/api/skills/at404_skill");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("找不到 Skill：at404_skill", (await again.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // ---- AT4-05:路由鍵用 name ----

    [Fact]
    public async Task Get_ByName_Returns200_WithDefinition()
    {
        var client = Admin();
        await CreateAsync(client, "at405_skill");

        var resp = await client.GetAsync("/api/skills/at405_skill");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("at405_skill", body["name"]!.GetValue<string>());
        Assert.Equal(Yaml("at405_skill"), body["definition"]!.GetValue<string>());
        Assert.Equal(
            SkillHash.Sha256(Yaml("at405_skill")),
            body["definition_sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_ByName_ReturnsCurrentRevisionDefinitionHash()
    {
        var client = Admin();
        await CreateAsync(client, "at405_hash");
        var updated = Yaml("at405_hash", description: "current");
        await client.PutAsJsonAsync("/api/skills/at405_hash", Body(updated));

        var body = await (await client.GetAsync("/api/skills/at405_hash")).ReadJsonAsync();

        Assert.Equal(2, body["current_revision"]!.GetValue<int>());
        Assert.Equal(SkillHash.Sha256(updated), body["definition_sha256"]!.GetValue<string>());
        Assert.Null(body["definitionSha256"]);
    }

    [Fact]
    public async Task Get_Missing_Returns404_ApiError()
    {
        var resp = await Admin().GetAsync("/api/skills/not_exist");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(404, body["status"]!.GetValue<int>());
        Assert.Equal("找不到 Skill：not_exist", body["message"]!.GetValue<string>());
        Assert.NotNull(body["timestamp"]);
        Assert.NotNull(body["fieldErrors"]);
    }

    // ---- AT4-06:snake_case ----

    [Fact]
    public async Task Get_ResponseFields_AreSnakeCase()
    {
        var client = Admin();
        await CreateAsync(client, "at406_skill");

        var single = (await (await client.GetAsync("/api/skills/at406_skill")).ReadJsonAsync()).AsObject();
        Assert.Equal(
            new[] { "created_at", "current_revision", "definition", "definition_sha256", "description", "enabled", "kind", "name", "required_role", "updated_at" },
            single.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        var item = (await (await client.GetAsync("/api/skills")).ReadJsonAsync()).AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == "at406_skill")!.AsObject();
        // 清單:含 enabled,但刻意不含 definition 內文。
        Assert.Equal(
            new[] { "created_at", "current_revision", "description", "enabled", "kind", "name", "required_role", "updated_at" },
            item.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.True(item["enabled"]!.GetValue<bool>());

        var rev = (await (await client.GetAsync("/api/skills/at406_skill/revisions")).ReadJsonAsync())
            .AsArray()[0]!.AsObject();
        Assert.Equal(
            new[] { "created_at", "created_by", "definition", "definition_sha256", "has_package", "kind", "revision" },
            rev.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    // ---- AT4-07:租戶隔離 — A 的 skill 對 B 一律「不存在」 ----

    [Fact]
    public async Task CrossTenant_SkillIsInvisible()
    {
        await CreateAsync(Admin("demo-a"), "at407_skill");
        var b = Admin("demo-b");

        // 清單看不到。
        var list = (await (await b.GetAsync("/api/skills")).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(list, n => n!["name"]!.GetValue<string>() == "at407_skill");

        // 不洩漏存在性:跨租戶讀取一律 404(非 403)。
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/skills/at407_skill")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync("/api/skills/at407_skill/revisions")).StatusCode);

        // 跨租戶寫入/刪除也不得生效。
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await b.PutAsJsonAsync("/api/skills/at407_skill", Body(Yaml("at407_skill", "B 偷改")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync("/api/skills/at407_skill")).StatusCode);

        // 原租戶完好如初。
        var a = await (await Admin("demo-a").GetAsync("/api/skills/at407_skill")).ReadJsonAsync();
        Assert.Equal("季報問答", a["description"]!.GetValue<string>());
        Assert.Equal(1, a["current_revision"]!.GetValue<int>());
    }

    [Fact] // 同名 skill 在兩個租戶各自存在、互不干擾(UNIQUE 是 (tenant_id, name) 而非 name)。
    public async Task SameName_InTwoTenants_AreIndependent()
    {
        await CreateAsync(Admin("demo-a"), "at407_shared", description: "A 的");
        await CreateAsync(Admin("demo-b"), "at407_shared", description: "B 的");

        Assert.Equal("A 的",
            (await (await Admin("demo-a").GetAsync("/api/skills/at407_shared")).ReadJsonAsync())
            ["description"]!.GetValue<string>());
        Assert.Equal("B 的",
            (await (await Admin("demo-b").GetAsync("/api/skills/at407_shared")).ReadJsonAsync())
            ["description"]!.GetValue<string>());
    }

    // ---- AT4-08:非 ADMIN 對 POST/PUT/DELETE 皆 403;GET 類開放 USER ----

    [Theory]
    [InlineData("POST", "/api/skills")]
    [InlineData("PUT", "/api/skills/at408_skill")]
    [InlineData("DELETE", "/api/skills/at408_skill")]
    public async Task NonAdmin_Write_Returns403(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(Body(Yaml("at408_skill")));
        }

        var resp = await User().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(403, body["status"]!.GetValue<int>());
        Assert.Equal("權限不足，無法存取 Skill", body["message"]!.GetValue<string>());
    }

    [Fact] // 缺 X-User-Role header(null)與 USER 同屬「非 ADMIN」等價類 — 一樣 403。
    public async Task MissingRoleHeader_Write_Returns403()
    {
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var resp = await client.PostAsJsonAsync("/api/skills", Body(Yaml("at408_norole")));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal("權限不足，無法存取 Skill", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // 角色把關必須早於模型驗證:非 ADMIN 送「不合法 body」仍是 403,不得回 400 並吐出欄位規則
    // (否則非 ADMIN 可藉此探測驗證規則),更不得因此打到引擎的 validate。
    [Theory]
    [InlineData("POST", "/api/skills")]
    [InlineData("PUT", "/api/skills/at408_skill")]
    public async Task NonAdmin_InvalidBody_Returns403_NotValidationError(string method, string path)
    {
        var before = Validator.Calls.Count;

        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(Body(string.Empty)),
        };
        var resp = await User().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("權限不足，無法存取 Skill", body["message"]!.GetValue<string>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
        Assert.Equal(before, Validator.Calls.Count);
    }

    [Fact] // 規格 §7.2:GET 清單/單筆/revisions 的角色是 USER。
    public async Task User_CanRead_ListSingleAndRevisions()
    {
        await CreateAsync(Admin(), "at408_readable");
        var user = User();

        var list = await user.GetAsync("/api/skills");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains((await list.ReadJsonAsync()).AsArray(),
            n => n!["name"]!.GetValue<string>() == "at408_readable");

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/skills/at408_readable")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/skills/at408_readable/revisions")).StatusCode);
    }

    // ---- 409:同名(同租戶 / 與 code 工作流同名) ----

    [Fact]
    public async Task Post_DuplicateName_Returns409()
    {
        var client = Admin();
        await CreateAsync(client, "at409_dup");

        var resp = await client.PostAsJsonAsync("/api/skills", Body(Yaml("at409_dup", description: "另一個")));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(409, body["status"]!.GetValue<int>());
        Assert.Equal("Skill 名稱已存在：at409_dup", body["message"]!.GetValue<string>());

        // 原本那筆沒被覆寫(仍是 revision 1、原描述)。
        var current = await (await client.GetAsync("/api/skills/at409_dup")).ReadJsonAsync();
        Assert.Equal("季報問答", current["description"]!.GetValue<string>());
        Assert.Equal(1, current["current_revision"]!.GetValue<int>());
    }

    // M-1:軟刪過的名字可以重用 —— 同一列復活,revision 接著加(稽核鏈不斷號),不是永久燒毀。
    [Fact]
    public async Task Post_AfterSoftDelete_RevivesName_AndContinuesRevisionChain()
    {
        var client = Admin();
        await CreateAsync(client, "at409_revive");                                       // r1
        await client.PutAsJsonAsync(
            "/api/skills/at409_revive", Body(Yaml("at409_revive", description: "第二版"))); // r2
        await client.DeleteAsync("/api/skills/at409_revive");

        // 名字已軟刪 → 同名 POST 不是 409,而是復活。
        var resp = await client.PostAsJsonAsync(
            "/api/skills", Body(Yaml("at409_revive", description: "重生")));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(3, body["current_revision"]!.GetValue<int>()); // 接著 r2 加,不是重頭來過
        Assert.Equal("重生", body["description"]!.GetValue<string>());
        Assert.True(body["enabled"]!.GetValue<bool>());

        // 可見性回來了,且稽核鏈 1/2/3 連續不斷號(舊歷史一列都沒少)。
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/skills/at409_revive")).StatusCode);
        var revisions = (await (await client.GetAsync("/api/skills/at409_revive/revisions")).ReadJsonAsync()).AsArray();
        Assert.Equal(new[] { 3, 2, 1 }, revisions.Select(r => r!["revision"]!.GetValue<int>()).ToArray());
    }

    // 與 code 註冊工作流同名 → 409,訊息講明衝突原因。
    // catalog/validate/nodes 是 platform 的字面路由段:字面段永遠勝過 {name},
    // 這種名字的 skill 就算建得起來也永遠點不進去 → 一併擋在建立時。
    // 清單在生產碼與測試裡都是硬寫的同一個 HashSet.Contains,不具偵測 workflow 內建名漂移的能力
    // (沒有打 GET /workflows)→ 三類來源各留一個代表值,多的案例是零資訊。
    [Theory]
    [InlineData("rag-qa")]         // workflow 內建 skill
    [InlineData("catalog")]        // platform 的字面路由段
    [InlineData("template-infer")] // template-* 樣板
    public async Task Post_ReservedWorkflowName_Returns409(string name)
    {
        var resp = await Admin().PostAsJsonAsync("/api/skills", Body(Yaml(name)));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("名稱與既有工作流同名，無法建立：" + name,
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // ---- body 驗證:definition 是唯一欄位且必填 ----

    // NotBlank 的三個判斷:null(JSON null / 缺欄位)、空字串、全空白字元。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_BlankDefinition_Returns400_AndNeverReachesEngine(string? definition)
    {
        var before = Validator.Calls.Count;

        var resp = await Admin().PostAsJsonAsync("/api/skills", Body(definition));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("definition 不可為空", body["fieldErrors"]!["definition"]!.GetValue<string>());
        Assert.Equal(before, Validator.Calls.Count);
    }

    // 同一道 NotBlank 閘門的另一半:PUT 與 POST 共用同一個 SkillUpsert 模型,ADMIN 送空白 definition
    // 一樣是 400(模型驗證在 authorization filter 之後、action 之前 → 早於「skill 存不存在」的 404,
    // 也早於引擎 validate)。少了這半邊,「換個 method 就漏掉必填檢查」不會被任何測試抓到。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Put_BlankDefinition_Returns400_AndNeverReachesEngine(string? definition)
    {
        var before = Validator.Calls.Count;

        var resp = await Admin().PutAsJsonAsync("/api/skills/at4_put_blank", Body(definition));

        // 400 而非 404:即使路由的 skill 根本不存在,模型驗證仍先出手。
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("definition 不可為空", body["fieldErrors"]!["definition"]!.GetValue<string>());
        Assert.Equal(before, Validator.Calls.Count);
    }

    // L-2:引擎不可達 → 對外 502(不是 422、不是 500),且零寫入。
    // 「驗證服務故障」與「定義不合法」是兩件事:前者不得放行未驗證的定義,也不得誣賴使用者。
    [Fact]
    public async Task Post_EngineUnreachable_Returns502_AndWritesNothing()
    {
        var yaml = Yaml("at4_engine_down") + FakeSkillValidator.EngineDownMarker;

        var resp = await Admin().PostAsJsonAsync("/api/skills", Body(yaml));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(502, body["status"]!.GetValue<int>());
        Assert.Contains("Skill 驗證服務呼叫失敗", body["message"]!.GetValue<string>());

        // 零寫入:清單看不到,單筆 404。
        var list = (await (await Admin().GetAsync("/api/skills")).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(list, n => n!["name"]!.GetValue<string>() == "at4_engine_down");
        Assert.Equal(HttpStatusCode.NotFound, (await Admin().GetAsync("/api/skills/at4_engine_down")).StatusCode);
    }

    [Fact] // required_role 由 YAML 決定(引擎回報),ADMIN 的 skill 一樣存得起來。
    public async Task Post_AdminRequiredRole_IsStoredFromYaml()
    {
        var resp = await Admin().PostAsJsonAsync(
            "/api/skills", Body(Yaml("at4_role_admin", requiredRole: "ADMIN")));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("ADMIN", (await resp.ReadJsonAsync())["required_role"]!.GetValue<string>());
    }

    // ---- simpleForm(B3):簡單模式表單狀態,opaque JSON 存取(camelCase 屬性) ----

    /// <summary>{ templateId, form:{...} };form 值全為字串(前端表單原值),backend 不解析。</summary>
    private static JsonObject SimpleForm(string templateId, string topK)
        => new()
        {
            ["templateId"] = templateId,
            ["form"] = new JsonObject
            {
                ["name"] = "名稱",
                ["description"] = "描述",
                ["rule"] = "規則",
                ["topK"] = topK,
            },
        };

    private static JsonObject BodyWithForm(string definition, JsonObject simpleForm)
        => new() { ["definition"] = definition, ["simpleForm"] = simpleForm };

    // (a) create 帶 simpleForm → 存進 DB,單筆 GET 與清單都讀得回原樣 JSON 物件。
    [Fact]
    public async Task Post_WithSimpleForm_IsStored_AndReturnedOnGetAndList()
    {
        var client = Admin();
        var resp = await client.PostAsJsonAsync(
            "/api/skills", BodyWithForm(Yaml("at_sf_create"), SimpleForm("template-stats", "50")));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // 單筆 GET:simpleForm 是 JSON 物件(不是被逃脫的字串),內容逐欄可讀。
        var single = (await (await client.GetAsync("/api/skills/at_sf_create")).ReadJsonAsync()).AsObject();
        var form = single["simpleForm"]!.AsObject();
        Assert.Equal("template-stats", form["templateId"]!.GetValue<string>());
        Assert.Equal("50", form["form"]!["topK"]!.GetValue<string>());

        // 清單項目也帶 simpleForm。
        var item = (await (await client.GetAsync("/api/skills")).ReadJsonAsync()).AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == "at_sf_create")!.AsObject();
        Assert.Equal("template-stats", item["simpleForm"]!["templateId"]!.GetValue<string>());
    }

    // 無 simpleForm 的 skill:欄位省略(不出現 null 鍵),不影響既有回應形狀。
    [Fact]
    public async Task Get_WithoutSimpleForm_OmitsField()
    {
        var client = Admin();
        await CreateAsync(client, "at_sf_absent");

        var single = (await (await client.GetAsync("/api/skills/at_sf_absent")).ReadJsonAsync()).AsObject();
        Assert.False(single.ContainsKey("simpleForm"));
    }

    // (b) 進階編輯器的 update(body 無 simpleForm)→ 保留既有值,不清空。
    [Fact]
    public async Task Put_WithoutSimpleForm_PreservesExisting()
    {
        var client = Admin();
        await client.PostAsJsonAsync(
            "/api/skills", BodyWithForm(Yaml("at_sf_preserve"), SimpleForm("template-stats", "50")));

        var put = await client.PutAsJsonAsync(
            "/api/skills/at_sf_preserve", Body(Yaml("at_sf_preserve", description: "進階手改")));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var single = (await (await client.GetAsync("/api/skills/at_sf_preserve")).ReadJsonAsync()).AsObject();
        Assert.Equal("進階手改", single["description"]!.GetValue<string>()); // definition 有更新
        Assert.Equal("template-stats", single["simpleForm"]!["templateId"]!.GetValue<string>()); // 表單狀態保留
        Assert.Equal("50", single["simpleForm"]!["form"]!["topK"]!.GetValue<string>());
    }

    // 「顯式 JSON null」與「缺欄位」是兩個不同的 wire 等價類,SimpleFormText 刻意對
    // Null / Undefined 一視同仁 → 都是「不帶新表單狀態」,保留既有值,不是「清空」。
    [Fact]
    public async Task Put_WithExplicitNullSimpleForm_PreservesExisting()
    {
        var client = Admin();
        await client.PostAsJsonAsync(
            "/api/skills", BodyWithForm(Yaml("at_sf_explicit_null"), SimpleForm("template-stats", "50")));

        var put = await client.PutAsJsonAsync(
            "/api/skills/at_sf_explicit_null",
            new JsonObject
            {
                ["definition"] = Yaml("at_sf_explicit_null", description: "顯式 null"),
                ["simpleForm"] = null,
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var single = (await (await client.GetAsync("/api/skills/at_sf_explicit_null")).ReadJsonAsync()).AsObject();
        Assert.Equal("顯式 null", single["description"]!.GetValue<string>()); // definition 有更新
        Assert.Equal("template-stats", single["simpleForm"]!["templateId"]!.GetValue<string>()); // 表單狀態保留
        Assert.Equal("50", single["simpleForm"]!["form"]!["topK"]!.GetValue<string>());
    }

    // 決策表另一半:update 帶新 simpleForm → 覆寫(簡單模式重存)。
    [Fact]
    public async Task Put_WithSimpleForm_Overwrites()
    {
        var client = Admin();
        await client.PostAsJsonAsync(
            "/api/skills", BodyWithForm(Yaml("at_sf_overwrite"), SimpleForm("template-stats", "50")));

        await client.PutAsJsonAsync(
            "/api/skills/at_sf_overwrite", BodyWithForm(Yaml("at_sf_overwrite"), SimpleForm("template-compare", "8")));

        var single = (await (await client.GetAsync("/api/skills/at_sf_overwrite")).ReadJsonAsync()).AsObject();
        Assert.Equal("template-compare", single["simpleForm"]!["templateId"]!.GetValue<string>());
        Assert.Equal("8", single["simpleForm"]!["form"]!["topK"]!.GetValue<string>());
    }

    // (e) 租戶隔離:A 帶 simpleForm 的 skill,B 一律 404(讀不到 skill,自然讀不到其表單狀態)。
    [Fact]
    public async Task SimpleForm_IsTenantScoped()
    {
        await Admin("demo-a").PostAsJsonAsync(
            "/api/skills", BodyWithForm(Yaml("at_sf_tenant"), SimpleForm("template-stats", "50")));

        Assert.Equal(
            HttpStatusCode.NotFound, (await Admin("demo-b").GetAsync("/api/skills/at_sf_tenant")).StatusCode);
        Assert.DoesNotContain(
            (await (await Admin("demo-b").GetAsync("/api/skills")).ReadJsonAsync()).AsArray(),
            n => n!["name"]!.GetValue<string>() == "at_sf_tenant");
    }
}
