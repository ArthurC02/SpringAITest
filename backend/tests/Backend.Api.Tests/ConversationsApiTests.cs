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

    [Theory]
    [InlineData("", "答", "prompt", "prompt 不可為空")]
    [InlineData("問", "", "reply", "reply 不可為空")]
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
}
