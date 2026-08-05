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

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task AdditiveHealthEndpoints_AreAlsoTokenExempt(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownHealthSubpath_IsNotTokenExempt()
    {
        var response = await _factory.CreateClient().GetAsync("/health/not-an-endpoint");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task DocumentIngestIntent_CreateThenReplay_ReturnsSameId_AndPendingIsHidden()
    {
        var client = _factory.CreateInternalClient().WithTenant("ingest-api-a").WithUser("user-a");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var created = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容" });
        var replay = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var first = await created.ReadJsonAsync();
        var second = await replay.ReadJsonAsync();
        Assert.Equal(first["id"]!.GetValue<string>(), second["id"]!.GetValue<string>());
        Assert.Equal("processing", first["status"]!.GetValue<string>());
        Assert.Empty((await (await client.GetAsync("/api/documents")).ReadJsonAsync()).AsArray());
    }

    [Fact]
    public async Task DocumentIngestIntent_RequiresInternalToken()
    {
        var client = _factory.CreateClient().WithTenant("ingest-token").WithUser("user-a");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        (await response.ReadJsonAsync()).AssertApiError(401, "authentication_required");
    }

    [Fact]
    public async Task DocumentIngestIntent_SameKeyDifferentPayload_ReturnsStable409()
    {
        var client = _factory.CreateInternalClient().WithTenant("ingest-api-b").WithUser("user-a");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容一" })).StatusCode);

        var conflict = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容二" });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.ReadJsonAsync();
        body.AssertApiError(409, "version_conflict");
        Assert.Equal("Idempotency-Key 已用於不同的文件請求", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task DocumentIngestIntent_AfterDelete_Returns409_AndCannotResurrect()
    {
        var tenant = "ingest-api-deleted";
        var client = _factory.CreateInternalClient().WithTenant(tenant).WithUser("user-a");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var created = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容" });
        var id = (await created.ReadJsonAsync())["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/documents/{id}")).StatusCode);

        var conflict = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容" });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("已刪除文件的 Idempotency-Key 不可重用", (await conflict.ReadJsonAsync())["message"]!.GetValue<string>());
        Assert.Empty((await (await client.GetAsync("/api/documents")).ReadJsonAsync()).AsArray());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task DocumentIngestIntent_RequiresTenantUserAndIdempotencyKey(
        bool tenant,
        bool user,
        bool key)
    {
        var client = _factory.CreateInternalClient();
        if (tenant) client.WithTenant("ingest-api-headers");
        if (user) client.WithUser("user-a");
        if (key) client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await client.PostAsJsonAsync(
            "/api/documents/ingest-intents", new { title = "手冊", text = "內容" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        (await response.ReadJsonAsync()).AssertApiError(400, "validation_failed");
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
