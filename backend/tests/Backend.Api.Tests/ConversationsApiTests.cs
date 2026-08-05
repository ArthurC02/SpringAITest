using System.Net;
using System.Net.Http.Json;
using Backend.Api.Common;
using Microsoft.AspNetCore.Http;

namespace Backend.Api.Tests;

/// <summary>聊天歷史端點,以 (tenant_id, user_id) 隔離:建立回 201 {id, createdAt};清單 created_at DESC;prompt/reply 空白 400;缺租戶 400。</summary>
public sealed class ConversationsApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConversationsApiTests(TestWebAppFactory factory) => _factory = factory;

    private HttpClient Client(string tenant = "demo-a", string user = "user-a")
        => _factory.CreateInternalClient().WithTenant(tenant).WithUser(user);

    [Fact]
    public async Task Create_Returns201_WithIdAndCreatedAt()
    {
        var client = Client();

        var resp = await client.PostAsJsonAsync("/api/conversations", new { prompt = "問句", reply = "答句" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.True(body["id"]!.GetValue<long>() > 0);
        Assert.NotNull(body["createdAt"]);
    }

    [Fact]
    public async Task List_ReturnsNewestFirst()
    {
        var client = Client("demo-a", "list-newest-first-user");
        await client.PostAsJsonAsync("/api/conversations", new { prompt = "P1", reply = "較早" });
        await client.PostAsJsonAsync("/api/conversations", new { prompt = "P2", reply = "較晚" });

        var arr = (await (await client.GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();

        // created_at DESC 契約:相鄰項時間非遞增。
        for (var i = 1; i < arr.Count; i++)
        {
            var prev = arr[i - 1]!["createdAt"]!.GetValue<DateTime>();
            var cur = arr[i]!["createdAt"]!.GetValue<DateTime>();
            Assert.True(prev >= cur);
        }

        // 較晚插入者排在較早之前(時間相同時以 id DESC 打破平手)。
        var replies = arr.Select(n => n!["reply"]!.GetValue<string>()).ToList();
        Assert.True(replies.IndexOf("較晚") < replies.IndexOf("較早"));
    }

    // 零筆邊界:全新的 (tenant,user) 沒有任何紀錄時是 200 + 空陣列,不是 null、不是 404。
    [Fact]
    public async Task List_NoRecords_ReturnsEmptyArray()
    {
        var client = Client("demo-a", "empty-history-user");

        var resp = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await resp.ReadJsonAsync()).AsArray());
    }

    // created_at 平手邊界:id DESC 是唯一的平手判準,所以連續快速寫入後清單必須是
    // 「建立順序的完全倒序」。時間戳是否真的落在同一刻取決於時鐘解析度,但正確實作在
    // 兩種情況下結果一致;若拿掉 id DESC,撞時的穩定排序會退化成建立順序(id 遞增),
    // 這個斷言就會紅 —— 這是不動生產碼(注入時鐘)所能逼近平手路徑的最大程度。
    [Fact]
    public async Task List_RapidBurst_IsExactReverseOfCreationOrder()
    {
        var client = Client("demo-a", "tie-break-user");
        var createdIds = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var created = await client.PostAsJsonAsync("/api/conversations", new { prompt = $"P{i}", reply = $"R{i}" });
            createdIds.Add((await created.ReadJsonAsync())["id"]!.GetValue<long>());
        }

        var arr = (await (await client.GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();

        Assert.Equal(
            createdIds.AsEnumerable().Reverse(),
            arr.Select(n => n!["id"]!.GetValue<long>()));
    }

    // 第三格「全是空白」是 NotBlank 之所以存在的理由(內建 [Required] 只擋 null),走的是
    // IsNullOrWhiteSpace 分支而非 null 分支,和空字串不是同一條路。
    [Theory]
    [InlineData("", "答", "prompt", "prompt 不可為空")]
    [InlineData("問", "", "reply", "reply 不可為空")]
    [InlineData("   ", "答", "prompt", "prompt 不可為空")]
    public async Task Create_BlankField_Returns400(string prompt, string reply, string field, string message)
    {
        var client = Client();

        var resp = await client.PostAsJsonAsync("/api/conversations", new { prompt, reply });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(message, (await resp.ReadJsonAsync())["fieldErrors"]![field]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_MissingTenantHeader_Returns400()
    {
        var client = _factory.CreateInternalClient().WithUser("user-a");

        var resp = await client.PostAsJsonAsync("/api/conversations", new { prompt = "問句", reply = "答句" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // 兩個維度同時無效(缺租戶標頭 + 欄位空白):[ApiController] 的 ModelState 驗證在
    // action body 之前跑,所以贏的是欄位驗證 400,RequireTenant() 根本沒被呼叫到 ——
    // 對外看到的是「輸入驗證失敗」而不是缺租戶訊息。
    [Fact]
    public async Task Create_MissingTenantHeaderAndBlankField_ValidationWins()
    {
        var client = _factory.CreateInternalClient().WithUser("user-a");

        var resp = await client.PostAsJsonAsync("/api/conversations", new { prompt = "", reply = "答句" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("輸入驗證失敗", body["message"]!.GetValue<string>());
        Assert.Equal("prompt 不可為空", body["fieldErrors"]!["prompt"]!.GetValue<string>());
    }

    // 「只有空白的 X-Tenant-Id」與「完全缺 header」是同一等價類 —— IdentityHeaders.Value() 把空白
    // 正規化成 null。這一格直接打 RequireTenant(不經傳輸層,避免 header 是否被中介層 trim 掉的干擾),
    // Documents/Conversations/ConfigurationSet 共用同一段程式碼,驗一次即證成整條路徑。
    [Fact]
    public void RequireTenant_BlankHeader_IsTreatedAsMissing()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdentityHeaders.TenantHeader] = "   ";

        var error = Assert.Throws<ApiException>(() => context.Request.RequireTenant());

        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", error.Message);
    }

    // X-User-Id 是刻意可選的:缺 header → UserIdOrEmpty() 回落空字串寫入,不是 400。
    // 同一個空使用者才讀得回自己的紀錄(不會外洩給具名使用者)。
    [Fact]
    public async Task Create_MissingUserHeader_FallsBackToEmptyUser()
    {
        var anonymous = _factory.CreateInternalClient().WithTenant("demo-a");

        var created = await anonymous.PostAsJsonAsync(
            "/api/conversations", new { prompt = "無使用者問句", reply = "無使用者專屬回覆" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var mine = (await (await anonymous.GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();
        Assert.Contains(mine, n => n!["reply"]!.GetValue<string>() == "無使用者專屬回覆");
        var named = (await (await Client("demo-a", "named-user").GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();
        Assert.DoesNotContain(named, n => n!["reply"]!.GetValue<string>() == "無使用者專屬回覆");
    }

    [Fact]
    public async Task CrossTenant_List_ExcludesOtherTenant()
    {
        var clientA = Client("demo-a", "cross-tenant-user");
        await clientA.PostAsJsonAsync("/api/conversations", new { prompt = "甲租戶問句", reply = "甲租戶專屬回覆" });

        var clientB = Client("demo-b", "cross-tenant-user");
        var arr = (await (await clientB.GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();

        Assert.DoesNotContain(arr, n => n!["reply"]!.GetValue<string>() == "甲租戶專屬回覆");
    }

    [Fact]
    public async Task CrossUser_List_ExcludesOtherUser()
    {
        var clientA = Client("demo-a", "user-x");
        await clientA.PostAsJsonAsync("/api/conversations", new { prompt = "使用者甲問句", reply = "使用者甲專屬回覆" });

        var clientB = Client("demo-a", "user-y");
        var arr = (await (await clientB.GetAsync("/api/conversations")).ReadJsonAsync()).AsArray();

        Assert.DoesNotContain(arr, n => n!["reply"]!.GetValue<string>() == "使用者甲專屬回覆");
    }

    [Fact]
    public async Task Page_Empty_ReturnsEnvelope()
    {
        var response = await Client("demo-a", "page-empty-user").GetAsync("/api/conversations/page");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Empty(body["items"]!.AsArray());
        Assert.Null(body["nextCursor"]);
        Assert.False(body["hasMore"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Page_FirstNextAndConcurrentInsert_UseStableKeysetBoundary()
    {
        var client = Client("demo-a", "page-boundary-user");
        var created = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/conversations", new { prompt = $"p{i}", reply = $"r{i}" });
            created.Add((await response.ReadJsonAsync())["id"]!.GetValue<long>());
        }

        var first = await (await client.GetAsync("/api/conversations/page?limit=2")).ReadJsonAsync();
        Assert.True(first["hasMore"]!.GetValue<bool>());
        var firstIds = first["items"]!.AsArray().Select(x => x!["id"]!.GetValue<long>()).ToArray();
        Assert.Equal(created.TakeLast(2).Reverse(), firstIds);
        var cursor = first["nextCursor"]!.GetValue<string>();

        var inserted = await client.PostAsJsonAsync(
            "/api/conversations", new { prompt = "new", reply = "new" });
        var insertedId = (await inserted.ReadJsonAsync())["id"]!.GetValue<long>();

        var next = await (await client.GetAsync(
            "/api/conversations/page?limit=2&before=" + Uri.EscapeDataString(cursor))).ReadJsonAsync();
        var nextIds = next["items"]!.AsArray().Select(x => x!["id"]!.GetValue<long>()).ToArray();
        Assert.Equal(created.Take(2).Reverse(), nextIds);
        Assert.False(next["hasMore"]!.GetValue<bool>());
        Assert.Null(next["nextCursor"]);
        Assert.DoesNotContain(insertedId, firstIds.Concat(nextIds));
        Assert.Empty(firstIds.Intersect(nextIds));
    }

    [Fact]
    public async Task Page_Max100_UsesLimitPlusOne()
    {
        var client = Client("demo-a", "page-max-user");
        for (var i = 0; i < 101; i++)
        {
            await client.PostAsJsonAsync(
                "/api/conversations", new { prompt = $"p{i}", reply = $"r{i}" });
        }

        var defaultPage = await (await client.GetAsync("/api/conversations/page")).ReadJsonAsync();
        Assert.Equal(50, defaultPage["items"]!.AsArray().Count);
        Assert.True(defaultPage["hasMore"]!.GetValue<bool>());

        var body = await (await client.GetAsync("/api/conversations/page?limit=100")).ReadJsonAsync();

        Assert.Equal(100, body["items"]!.AsArray().Count);
        Assert.True(body["hasMore"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(body["nextCursor"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Page_InvalidLimit_ReturnsStable400(int limit)
    {
        var response = await Client().GetAsync($"/api/conversations/page?limit={limit}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("validation_failed", body["code"]!.GetValue<string>());
        Assert.Equal("limit 必須介於 1 到 100", body["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    public async Task Page_MalformedCursor_ReturnsStable400(string cursor)
    {
        var response = await Client().GetAsync(
            "/api/conversations/page?before=" + Uri.EscapeDataString(cursor));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("validation_failed", body["code"]!.GetValue<string>());
        Assert.Equal("聊天歷史游標無效", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Page_CursorIsBoundToTenantAndUser()
    {
        var owner = Client("demo-a", "cursor-owner");
        await owner.PostAsJsonAsync("/api/conversations", new { prompt = "p1", reply = "r1" });
        await owner.PostAsJsonAsync("/api/conversations", new { prompt = "p2", reply = "r2" });
        var first = await (await owner.GetAsync("/api/conversations/page?limit=1")).ReadJsonAsync();
        var cursor = first["nextCursor"]!.GetValue<string>();

        foreach (var client in new[]
                 {
                     Client("demo-b", "cursor-owner"),
                     Client("demo-a", "cursor-other-user"),
                 })
        {
            var response = await client.GetAsync(
                "/api/conversations/page?before=" + Uri.EscapeDataString(cursor));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("聊天歷史游標無效", (await response.ReadJsonAsync())["message"]!.GetValue<string>());
        }

        var wrongVersion = (cursor[0] == 'A' ? 'B' : 'A') + cursor[1..];
        var versionResponse = await owner.GetAsync(
            "/api/conversations/page?before=" + Uri.EscapeDataString(wrongVersion));
        Assert.Equal(HttpStatusCode.BadRequest, versionResponse.StatusCode);
        Assert.Equal(
            "聊天歷史游標無效",
            (await versionResponse.ReadJsonAsync())["message"]!.GetValue<string>());
    }
}
