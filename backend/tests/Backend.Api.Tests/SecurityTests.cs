using System.Net;
using System.Net.Http.Json;
using Backend.Api.Common;

namespace Backend.Api.Tests;

public sealed class SecurityTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public SecurityTests(TestWebAppFactory factory) => _factory = factory;

    // 缺 header(null)與錯 token("nope")同屬「內部憑證無效」等價類:守門一律回 401 ApiError。
    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    public async Task InvalidInternalToken_Returns401_ApiError(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add(InternalTokenMiddleware.HeaderName, token);
        }

        var resp = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        body.AssertApiError(401, "authentication_required");
        Assert.Equal("內部憑證無效", body["message"]!.GetValue<string>());
    }

    // FixedTimeEquals 的「長度相同、內容不同」分支:上面的 "nope" 長度就對不上,只走到長度短路那條路。
    // 近似 token(由真 token 換掉末字元構成,長度必然相同)才會真的逐 byte 比對,同樣必須 401。
    [Fact]
    public async Task NearMissInternalToken_SameLengthDifferentContent_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalTokenMiddleware.HeaderName, TestWebAppFactory.InternalToken[..^1] + "X");

        var resp = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("內部憑證無效", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    // INTERNAL_API_TOKEN 顯式設為空/空白 → fail-fast(避免信任邊界因 FixedTimeEquals("","")==true 而失效)。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InternalTokenResolver_EmptyOrWhitespace_Throws(string configuredValue)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => InternalTokenResolver.Resolve(configuredValue));
        Assert.Contains("INTERNAL_API_TOKEN", ex.Message);
    }

    [Fact]
    public void InternalTokenResolver_Unset_ReturnsDevDefault()
    {
        Assert.Equal(InternalTokenResolver.DevDefault, InternalTokenResolver.Resolve(null));
    }

    [Fact]
    public void InternalTokenResolver_NonEmpty_ReturnsAsIs()
    {
        Assert.Equal("custom-token", InternalTokenResolver.Resolve("custom-token"));
    }

    // /health 在 token 檢查之前就 return:缺 token 與帶錯 token 都必須 200(順序改錯就會爆)。
    [Theory]
    [InlineData(null)]
    [InlineData("nope")]
    public async Task Health_AnyToken_Returns200(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add(InternalTokenMiddleware.HeaderName, token);
        }

        var resp = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("UP", (await resp.ReadJsonAsync())["status"]!.GetValue<string>());
    }

    // 「完全沒帶 header」與「帶了但只有空白」是兩種不同的線上輸入,IdentityHeaders.Value() 把空白折成 null,
    // 兩者都必須落在同一個 RequireTenant() 400 分支(空白不得被當成合法租戶 code)。
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Documents_MissingTenantHeader_Returns400(string? tenantHeader)
    {
        var client = _factory.CreateInternalClient();
        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(IdentityHeaders.TenantHeader, tenantHeader);
        }

        var resp = await client.GetAsync("/api/documents");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("缺少租戶識別標頭：X-Tenant-Id", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Documents_SeedListDelete_HappyPath()
    {
        var id = await _factory.SeedDocumentAsync("demo-a", "手冊", "第一段內容。\n\n第二段內容。");
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var list = await client.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var arr = (await list.ReadJsonAsync()).AsArray();
        var doc = arr.Single(n => n!["id"]!.GetValue<string>() == id)!;
        Assert.Equal("手冊", doc["title"]!.GetValue<string>());
        // GET 帶 status;種入已處理完成 → ready。
        Assert.Equal("ready", doc["status"]!.GetValue<string>());
        Assert.True(doc["chunk_count"]!.GetValue<int>() >= 1);

        var del = await client.DeleteAsync($"/api/documents/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task Documents_DeleteMissing_Returns404()
    {
        var client = _factory.CreateInternalClient().WithTenant("demo-a");

        var resp = await client.DeleteAsync($"/api/documents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.StartsWith("找不到文件：", (await resp.ReadJsonAsync())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Retrieval_Search_ReturnsChunks()
    {
        await _factory.SeedDocumentAsync("demo-b", "報告", "重點一。\n\n重點二。");
        var client = _factory.CreateInternalClient().WithTenant("demo-b");

        var resp = await client.PostAsJsonAsync("/api/retrieval/search", new { query = "重點", top_k = 2 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var chunks = (await resp.ReadJsonAsync())["chunks"]!.AsArray();
        Assert.NotEmpty(chunks);
        Assert.NotNull(chunks[0]!["document_id"]);
        Assert.NotNull(chunks[0]!["score"]);
    }

    // ---- C2:跨租戶隔離(兩個不同 X-Tenant-Id 的 client 打同一 factory;fake repo 忠實模擬租戶過濾) ----

    [Fact]
    public async Task CrossTenant_DocumentsList_ExcludesOtherTenant()
    {
        var idA = await _factory.SeedDocumentAsync("demo-a", "甲租戶專屬手冊", "內容一。\n\n內容二。");
        var clientB = _factory.CreateInternalClient().WithTenant("demo-b");

        var arr = (await (await clientB.GetAsync("/api/documents")).ReadJsonAsync()).AsArray();

        Assert.DoesNotContain(arr, n => n!["id"]!.GetValue<string>() == idA);
    }

    [Fact]
    public async Task CrossTenant_Delete_Returns404_AndKeepsDocument()
    {
        var idA = await _factory.SeedDocumentAsync("demo-a", "甲租戶不可刪", "內容。");
        var clientB = _factory.CreateInternalClient().WithTenant("demo-b");

        var del = await clientB.DeleteAsync($"/api/documents/{idA}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        // 跨租戶刪除視為找不到,文件仍在(demo-a 可見)。
        var clientA = _factory.CreateInternalClient().WithTenant("demo-a");
        var arr = (await (await clientA.GetAsync("/api/documents")).ReadJsonAsync()).AsArray();
        Assert.Contains(arr, n => n!["id"]!.GetValue<string>() == idA);
    }

    [Fact]
    public async Task CrossTenant_Search_ExcludesOtherTenantChunks()
    {
        var idA = await _factory.SeedDocumentAsync("demo-a", "甲租戶機密報告", "祕密一。\n\n祕密二。");
        var clientB = _factory.CreateInternalClient().WithTenant("demo-b");

        var resp = await clientB.PostAsJsonAsync("/api/retrieval/search", new { query = "祕密", top_k = 10 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var chunks = (await resp.ReadJsonAsync())["chunks"]!.AsArray();
        // demo-b 的檢索絕不含 demo-a 的片段。
        Assert.DoesNotContain(chunks, c => c!["document_id"]!.GetValue<string>() == idA);
    }
}
