using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Backend.Api.Tests;

/// <summary>
/// Configuration Set 的 controller 層驗收(SSR-P4-002/003/005/006/007/008/009/010 + active 端點)。
/// 走 HTTP + FakeConfigurationSetRepository:授權(早於模型驗證)、逐鍵值驗證(422 + fieldErrors 精確 key)、
/// snake_case 形狀、租戶 404、list 不含 values / detail 含。
/// **DB 級不變量(部分唯一索引兜底、原子 activate 並發)不在此檔** — 見 ConfigurationSetRepositoryTests(真 DB)。
/// fake 為 class fixture 共用 → 每測用自己的名稱,避免互相汙染。
/// </summary>
public sealed class ConfigurationSetApiTests : IClassFixture<TestWebAppFactory>
{
    private const string Path = "/api/configuration-sets";

    private readonly TestWebAppFactory _factory;

    public ConfigurationSetApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Admin(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("ADMIN").WithTenant(tenant);

    private HttpClient User(string tenant = "demo-a")
        => _factory.CreateInternalClient().WithRole("USER").WithTenant(tenant);

    private static JsonObject Body(string name, JsonObject? values = null)
        => new() { ["name"] = name, ["values"] = values ?? new JsonObject() };

    private async Task<JsonNode> CreateAsync(HttpClient client, string name, JsonObject? values = null)
    {
        var resp = await client.PostAsJsonAsync(Path, Body(name, values));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.ReadJsonAsync();
    }

    // ---- SSR-P4-002:CRUD 全鏈;list 不含 values、detail 含;snake_case ----

    [Fact]
    public async Task Crud_FullChain_ListWithoutValues_DetailWithValues_SnakeCase()
    {
        var client = Admin();
        var values = new JsonObject { ["retrieval.top_k"] = 10, ["llm.model"] = "gpt-4o-mini" };

        // POST → 201,回單筆(含 values / created / updated),is_active 預設 false。
        var created = await CreateAsync(client, "p4_002", values);
        var id = created["id"]!.GetValue<string>();
        Assert.False(created["is_active"]!.GetValue<bool>());
        Assert.Equal(10, created["values"]!["retrieval.top_k"]!.GetValue<int>());
        Assert.Equal(
            new[] { "created_at", "created_by", "id", "is_active", "name", "updated_at", "values" },
            created.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // GET list → 不含 values(snake_case)。
        var listItem = (await (await client.GetAsync(Path)).ReadJsonAsync()).AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == "p4_002")!.AsObject();
        Assert.Equal(
            new[] { "created_at", "id", "is_active", "name", "updated_at" },
            listItem.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // GET {id} → 含 values。
        var detail = await (await client.GetAsync($"{Path}/{id}")).ReadJsonAsync();
        Assert.Equal("gpt-4o-mini", detail["values"]!["llm.model"]!.GetValue<string>());

        // PUT → 更新 values。
        var putResp = await client.PutAsJsonAsync($"{Path}/{id}",
            Body("p4_002", new JsonObject { ["retrieval.top_k"] = 20 }));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.ReadJsonAsync();
        Assert.Equal(20, updated["values"]!["retrieval.top_k"]!.GetValue<int>());
        Assert.Null(updated["values"]!.AsObject().FirstOrDefault(p => p.Key == "llm.model").Value);

        // DELETE → 204,之後 GET {id} 404。
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{id}")).StatusCode);
    }

    // ---- SSR-P4-003:同租戶同名拒收無第二列;tenant-b 可建同名 ----

    [Fact]
    public async Task DuplicateName_SameTenant_409_NoSecondRow_OtherTenantCanReuse()
    {
        await CreateAsync(Admin("demo-a"), "p4_003");

        var dup = await Admin("demo-a").PostAsJsonAsync(Path, Body("p4_003"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("Configuration Set 名稱已存在：p4_003",
            (await dup.ReadJsonAsync())["message"]!.GetValue<string>());

        // 只有一列。
        var mine = (await (await Admin("demo-a").GetAsync(Path)).ReadJsonAsync()).AsArray()
            .Count(n => n!["name"]!.GetValue<string>() == "p4_003");
        Assert.Equal(1, mine);

        // tenant-b 可建同名。
        Assert.Equal(HttpStatusCode.Created,
            (await Admin("demo-b").PostAsJsonAsync(Path, Body("p4_003"))).StatusCode);
    }

    // 唯一名稱的另一半:Create 撞名有人顧,改名撞既有(Update 的 409)沒人顧 —
    // 少了這條,UpdateAsync 忘記排除自己以外的同名列也不會被抓到。
    [Fact]
    public async Task Update_RenameToExistingName_409_KeepsOriginalName()
    {
        var client = Admin();
        await CreateAsync(client, "p4_rename_a");
        var b = await CreateAsync(client, "p4_rename_b");
        var bId = b["id"]!.GetValue<string>();

        var resp = await client.PutAsJsonAsync($"{Path}/{bId}", Body("p4_rename_a"));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("Configuration Set 名稱已存在：p4_rename_a",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());

        // B 未被改名,A 仍只有一列(衝突 = 零寫入)。
        Assert.Equal("p4_rename_b",
            (await (await client.GetAsync($"{Path}/{bId}")).ReadJsonAsync())["name"]!.GetValue<string>());
        var list = (await (await client.GetAsync(Path)).ReadJsonAsync()).AsArray();
        Assert.Equal(1, list.Count(n => n!["name"]!.GetValue<string>() == "p4_rename_a"));
    }

    // ---- SSR-P4-005:刪 active 後該租戶變無 active、不自動選另一組 ----

    [Fact]
    public async Task DeleteActive_LeavesTenantWithNoActive_DoesNotAutoSelectOther()
    {
        var client = Admin();
        var a = await CreateAsync(client, "p4_005_a");
        await CreateAsync(client, "p4_005_b");
        var aId = a["id"]!.GetValue<string>();

        await client.PostAsync($"{Path}/{aId}/activate", null);
        Assert.True((await (await client.GetAsync($"{Path}/{aId}")).ReadJsonAsync())["is_active"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Path}/{aId}")).StatusCode);

        // active 端點 404(無 active),B 未被自動啟用。
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/active")).StatusCode);
        var b = (await (await client.GetAsync(Path)).ReadJsonAsync()).AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == "p4_005_b")!;
        Assert.False(b["is_active"]!.GetValue<bool>());
    }

    // ---- SSR-P4-006:USER 對全部(受保護)端點 403,且授權早於模型驗證 ----

    public static IEnumerable<object[]> ProtectedEndpoints()
    {
        var id = Guid.NewGuid();
        yield return new object[] { "GET", Path };
        yield return new object[] { "GET", $"{Path}/{id}" };
        yield return new object[] { "POST", Path };
        yield return new object[] { "PUT", $"{Path}/{id}" };
        yield return new object[] { "DELETE", $"{Path}/{id}" };
        yield return new object[] { "POST", $"{Path}/{id}/activate" };
    }

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task User_ProtectedEndpoint_403_AuthBeforeModelValidation(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            // 故意不合法 body(缺 name + 越界值):USER 仍 403,不得先被 400/422 短路。
            request.Content = JsonContent.Create(new JsonObject
            {
                ["values"] = new JsonObject { ["retrieval.top_k"] = 999 },
            });
        }

        var resp = await User().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(403, body["status"]!.GetValue<int>());
        Assert.Empty(body["fieldErrors"]!.AsObject());
        // 訊息資源中性 — 不得誤導成 Skill(重用 [SkillAdminOnly] 時的 UX bug)。
        var message = body["message"]!.GetValue<string>();
        Assert.Equal("權限不足，需要管理員權限", message);
        Assert.DoesNotContain("Skill", message);
    }

    // 缺 X-User-Role(不是 USER,是完全不帶 header)與 USER 同屬「非 ADMIN」等價類 → 一樣 403。
    [Fact]
    public async Task ProtectedEndpoint_MissingRoleHeader_Returns403()
    {
        var roleless = _factory.CreateInternalClient().WithTenant("demo-a");

        var resp = await roleless.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(
            "權限不足，需要管理員權限",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // 上面兩條證明「非 ADMIN 不會跑到模型驗證」;這條是決策表的另一半 —— ADMIN 通過授權後,
    // name 的 [NotBlank] 真的擋下來,且回 400「輸入驗證失敗」(不是 values 的 422)。
    // null 走 NotBlankAttribute 的 null 分支,"   " 走 IsNullOrWhiteSpace 分支。
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_BlankName_Returns400_BeforeValuesValidation(string? name)
    {
        // values 同時越界:模型驗證早於 ConfigurationValues → 只能報 name,不得混進 422 的欄位錯誤。
        var body = new JsonObject
        {
            ["name"] = name,
            ["values"] = new JsonObject { ["retrieval.top_k"] = 999 },
        };

        var resp = await Admin().PostAsJsonAsync(Path, body);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = await resp.ReadJsonAsync();
        Assert.Equal(400, error["status"]!.GetValue<int>());
        Assert.Equal("輸入驗證失敗", error["message"]!.GetValue<string>());
        Assert.Equal("name 不可為空", error["fieldErrors"]!["name"]!.GetValue<string>());
        Assert.Null(error["fieldErrors"]!.AsObject().FirstOrDefault(p => p.Key == "retrieval.top_k").Value);
    }

    // ---- SSR-P4-007:跨租戶 GET/PUT/DELETE/activate 全 404;tenant-b 不受影響;list 只見自己 ----

    [Fact]
    public async Task CrossTenant_AllMutations_404_OtherTenantUnaffected()
    {
        var a = await CreateAsync(Admin("demo-a"), "p4_007");
        var aId = a["id"]!.GetValue<string>();
        await Admin("demo-a").PostAsync($"{Path}/{aId}/activate", null);

        var b = Admin("demo-b");
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{Path}/{aId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await b.PutAsJsonAsync($"{Path}/{aId}", Body("p4_007"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"{Path}/{aId}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"{Path}/{aId}")).StatusCode);

        // tenant-b 的 list 看不到 a 的組。
        var bList = (await (await b.GetAsync(Path)).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(bList, n => n!["name"]!.GetValue<string>() == "p4_007");

        // tenant-a 的組完好且仍是 active。
        var still = await (await Admin("demo-a").GetAsync($"{Path}/{aId}")).ReadJsonAsync();
        Assert.True(still["is_active"]!.GetValue<bool>());
    }

    // ---- SSR-P4-008 / 009:數值 on/off-point ----

    [Theory]
    [InlineData("retrieval.top_k", 1, HttpStatusCode.Created)]
    [InlineData("retrieval.top_k", 50, HttpStatusCode.Created)]
    [InlineData("retrieval.top_k", 0, HttpStatusCode.UnprocessableEntity)]
    [InlineData("retrieval.top_k", 51, HttpStatusCode.UnprocessableEntity)]
    [InlineData("kb_query.top_k", 1, HttpStatusCode.Created)]
    [InlineData("kb_query.top_k", 500, HttpStatusCode.Created)]
    [InlineData("kb_query.top_k", 0, HttpStatusCode.UnprocessableEntity)]
    [InlineData("kb_query.top_k", -3, HttpStatusCode.UnprocessableEntity)]
    [InlineData("kb_query.max_retrieval_attempts", 1, HttpStatusCode.Created)]
    [InlineData("kb_query.max_retrieval_attempts", 1000000, HttpStatusCode.Created)] // 與姊妹鍵一致:無上限
    [InlineData("kb_query.max_retrieval_attempts", 0, HttpStatusCode.UnprocessableEntity)]
    [InlineData("kb_query.max_retrieval_attempts", -5, HttpStatusCode.UnprocessableEntity)] // 負值與 0 同屬 < min
    [InlineData("workflow.timeout_seconds", 1, HttpStatusCode.Created)]
    [InlineData("workflow.timeout_seconds", 3600, HttpStatusCode.Created)]
    [InlineData("workflow.timeout_seconds", 0, HttpStatusCode.UnprocessableEntity)]
    [InlineData("workflow.timeout_seconds", -5, HttpStatusCode.UnprocessableEntity)]
    public async Task IntKey_OnOffPoint(string key, int value, HttpStatusCode expected)
    {
        var name = $"p4_int_{key.Replace('.', '_')}_{(value < 0 ? "neg" + -value : value.ToString())}";
        var resp = await Admin().PostAsJsonAsync(Path,
            Body(name, new JsonObject { [key] = value }));

        Assert.Equal(expected, resp.StatusCode);
        if (expected == HttpStatusCode.UnprocessableEntity)
        {
            await AssertRejected(resp, key, name);
        }
    }

    [Theory] // int 鍵餵浮點 / 字串 → 拒(SSR-P4-009 非整數)
    [InlineData(2.5)]
    public async Task IntKey_Float_Rejected(double value)
    {
        var resp = await Admin().PostAsJsonAsync(Path,
            Body("p4_int_float", new JsonObject { ["kb_query.top_k"] = value }));
        await AssertRejected(resp, "kb_query.top_k", "p4_int_float");
    }

    [Fact]
    public async Task IntKey_String_Rejected()
    {
        var resp = await Admin().PostAsJsonAsync(Path,
            Body("p4_int_str", new JsonObject { ["kb_query.top_k"] = "5" }));
        await AssertRejected(resp, "kb_query.top_k", "p4_int_str");
    }

    [Theory]
    [InlineData("intent.confidence_threshold", 0.0, HttpStatusCode.Created)]
    [InlineData("intent.confidence_threshold", 1.0, HttpStatusCode.Created)]
    [InlineData("intent.confidence_threshold", -0.1, HttpStatusCode.UnprocessableEntity)]
    [InlineData("intent.confidence_threshold", 1.1, HttpStatusCode.UnprocessableEntity)]
    [InlineData("llm.temperature", 0.0, HttpStatusCode.Created)]
    [InlineData("llm.temperature", 2.0, HttpStatusCode.Created)]
    [InlineData("llm.temperature", -0.1, HttpStatusCode.UnprocessableEntity)]
    [InlineData("llm.temperature", 2.1, HttpStatusCode.UnprocessableEntity)]
    public async Task FloatKey_OnOffPoint(string key, double value, HttpStatusCode expected)
    {
        var name = $"p4_flt_{key.Replace('.', '_')}_{value.ToString().Replace('.', 'd').Replace('-', 'm')}";
        var resp = await Admin().PostAsJsonAsync(Path, Body(name, new JsonObject { [key] = value }));

        Assert.Equal(expected, resp.StatusCode);
        if (expected == HttpStatusCode.UnprocessableEntity)
        {
            await AssertRejected(resp, key, name);
        }
    }

    // ---- SSR-P4-010:llm.model 白名單/型別 + 非白名單鍵 → 422 精確 fieldErrors,無部分寫入 ----

    [Theory]
    [InlineData("gpt-4o-mini", HttpStatusCode.Created)]
    [InlineData("mock-gpt", HttpStatusCode.Created)]
    [InlineData("gpt-5-turbo", HttpStatusCode.UnprocessableEntity)]
    [InlineData("", HttpStatusCode.UnprocessableEntity)]
    public async Task ModelKey_Whitelist(string model, HttpStatusCode expected)
    {
        var name = $"p4_model_{(model.Length == 0 ? "empty" : model.Replace('.', '_'))}";
        var resp = await Admin().PostAsJsonAsync(Path, Body(name, new JsonObject { ["llm.model"] = model }));

        Assert.Equal(expected, resp.StatusCode);
        if (expected == HttpStatusCode.UnprocessableEntity)
        {
            await AssertRejected(resp, "llm.model", name);
        }
    }

    [Fact] // llm.model 為數字 → 型別錯 → 422
    public async Task ModelKey_Number_Rejected()
    {
        var resp = await Admin().PostAsJsonAsync(Path,
            Body("p4_model_num", new JsonObject { ["llm.model"] = 42 }));
        await AssertRejected(resp, "llm.model", "p4_model_num");
    }

    [Fact] // 非白名單鍵 → 422,fieldErrors 指向該未知 key
    public async Task UnknownValuesKey_Rejected_422_ExactFieldError_NoWrite()
    {
        var resp = await Admin().PostAsJsonAsync(Path,
            Body("p4_unknown", new JsonObject { ["totally.bogus"] = 5 }));
        await AssertRejected(resp, "totally.bogus", "p4_unknown");
    }

    [Fact] // 一有效 + 一無效鍵:整批拒收,無部分寫入(合法鍵也不落地)
    public async Task PartialInvalid_RejectsWholeBatch_NoPartialWrite()
    {
        var resp = await Admin().PostAsJsonAsync(Path, Body("p4_partial", new JsonObject
        {
            ["llm.model"] = "gpt-4o-mini",   // 合法
            ["bogus.key"] = 1,               // 非白名單
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.NotNull(body["fieldErrors"]!["bogus.key"]);
        Assert.Null(body["fieldErrors"]!.AsObject().FirstOrDefault(p => p.Key == "llm.model").Value);

        // 零寫入。
        var list = (await (await Admin().GetAsync(Path)).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(list, n => n!["name"]!.GetValue<string>() == "p4_partial");
    }

    [Fact] // 驗證迴圈累積所有錯誤才丟:兩個非法鍵必須同時出現在 fieldErrors,不是只報第一個
    public async Task MultipleInvalidKeys_ReturnsAllFieldErrors()
    {
        var resp = await Admin().PostAsJsonAsync(Path, Body("p4_multi_invalid", new JsonObject
        {
            ["bogus.one"] = 1,
            ["retrieval.top_k"] = 0, // 白名單鍵但越界
        }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var fieldErrors = (await resp.ReadJsonAsync())["fieldErrors"]!.AsObject();
        Assert.NotNull(fieldErrors["bogus.one"]);
        Assert.NotNull(fieldErrors["retrieval.top_k"]);
    }

    // 上面整組逐鍵驗證都只打 POST;PUT 是同一道 ConfigurationValues.Validate 的第二個呼叫點,
    // 接線斷了(忘了呼叫)只有這條會紅:越界值不得覆蓋既有 values。
    [Fact]
    public async Task Update_InvalidValues_Returns422_KeepsStoredValues()
    {
        var client = Admin();
        var set = await CreateAsync(client, "p4_put_invalid", new JsonObject { ["retrieval.top_k"] = 10 });
        var id = set["id"]!.GetValue<string>();

        var resp = await client.PutAsJsonAsync($"{Path}/{id}",
            Body("p4_put_invalid", new JsonObject { ["retrieval.top_k"] = 0 }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var error = await resp.ReadJsonAsync();
        Assert.Equal("Configuration Set 驗證失敗", error["message"]!.GetValue<string>());
        Assert.NotNull(error["fieldErrors"]!["retrieval.top_k"]);

        // 驗證早於寫入 → 原值原封不動。
        var detail = await (await client.GetAsync($"{Path}/{id}")).ReadJsonAsync();
        Assert.Equal(10, detail["values"]!["retrieval.top_k"]!.GetValue<int>());
    }

    // ---- active 端點(協調者補列):USER 可讀、無 active 404、跨租戶只見自己 ----

    [Fact]
    public async Task Active_ReturnsCurrentActiveWithValues_AndUserCanRead()
    {
        var admin = Admin("demo-a");
        var set = await CreateAsync(admin, "p4_active", new JsonObject { ["kb_query.top_k"] = 12 });
        await admin.PostAsync($"{Path}/{set["id"]!.GetValue<string>()}/activate", null);

        // active 端點不掛 [SkillAdminOnly] → USER 也讀得到(invoke 期取有效設定)。
        var resp = await User("demo-a").GetAsync($"{Path}/active");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("p4_active", body["name"]!.GetValue<string>());
        Assert.Equal(12, body["values"]!["kb_query.top_k"]!.GetValue<int>());
    }

    [Fact]
    public async Task Active_NoActive_Returns404()
    {
        // 隔離租戶(backend 信任 X-Tenant-Id header,不必是種子租戶)→ 無任何組、無 active。
        const string tenant = "p4-noactive";
        await CreateAsync(Admin(tenant), "p4_active_none"); // 建但不啟用

        var resp = await User(tenant).GetAsync($"{Path}/active");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("沒有啟用中的 Configuration Set",
            (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Active_CrossTenant_OnlySeesOwn()
    {
        var aAdmin = Admin("demo-a");
        var aSet = await CreateAsync(aAdmin, "p4_active_a", new JsonObject { ["llm.model"] = "mock-gpt" });
        await aAdmin.PostAsync($"{Path}/{aSet["id"]!.GetValue<string>()}/activate", null);

        var bAdmin = Admin("demo-b");
        var bSet = await CreateAsync(bAdmin, "p4_active_b", new JsonObject { ["llm.model"] = "gpt-4o-mini" });
        await bAdmin.PostAsync($"{Path}/{bSet["id"]!.GetValue<string>()}/activate", null);

        var aActive = await (await User("demo-a").GetAsync($"{Path}/active")).ReadJsonAsync();
        Assert.Equal("p4_active_a", aActive["name"]!.GetValue<string>());
        Assert.Equal("mock-gpt", aActive["values"]!["llm.model"]!.GetValue<string>());

        var bActive = await (await User("demo-b").GetAsync($"{Path}/active")).ReadJsonAsync();
        Assert.Equal("p4_active_b", bActive["name"]!.GetValue<string>());
    }

    private async Task AssertRejected(HttpResponseMessage resp, string key, string name)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(422, body["status"]!.GetValue<int>());
        Assert.Equal("Configuration Set 驗證失敗", body["message"]!.GetValue<string>());
        Assert.NotNull(body["fieldErrors"]![key]); // fieldErrors 指向精確 key

        // 無副作用:該名稱未落地。
        var list = (await (await Admin().GetAsync(Path)).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(list, n => n!["name"]!.GetValue<string>() == name);
    }
}
