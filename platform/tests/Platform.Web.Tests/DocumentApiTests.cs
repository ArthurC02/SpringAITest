using System.Net;
using System.Net.Http.Json;

namespace Platform.Web.Tests;

public sealed class DocumentApiTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public DocumentApiTests(TestWebAppFactory factory) => _factory = factory;

    // 類別層級 [Authorize]:三個端點在沒有 JWT 時都必須是 401,不能有任何一條漏掛。
    [Theory]
    [InlineData("POST", "/api/documents")]
    [InlineData("GET", "/api/documents")]
    [InlineData("DELETE", "/api/documents/doc-1")]
    public async Task Endpoints_Return401_WithoutToken(string method, string path)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            req.Content = JsonContent.Create(new { title = "標題", text = "內容" });
        }

        var resp = await _factory.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        (await resp.ReadJsonAsync()).AssertApiError(401, "authentication_required");
    }

    [Fact]
    public async Task Create_Returns202_WithStatusProcessing()
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
            new { title = "標題", text = "內容" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("doc-1", body["id"]!.GetValue<string>());
        Assert.Equal("標題", body["title"]!.GetValue<string>());
        Assert.Equal("processing", body["status"]!.GetValue<string>());
    }

    // NotBlank 的三個等價類(欄位缺漏 / 空字串 / 全空白)× 兩個必填欄位:title 與 text 各自掛
    // 自己的 [NotBlank],兩者的驗證分支獨立,不能只測其中一個再類推另一個。
    [Theory]
    [InlineData("title", null)]
    [InlineData("title", "")]
    [InlineData("title", "   ")]
    [InlineData("text", null)]
    [InlineData("text", "")]
    [InlineData("text", "   ")]
    public async Task Create_Returns400_WhenRequiredFieldBlank(string field, string? value)
    {
        var payload = new Dictionary<string, string> { ["title"] = "標題", ["text"] = "內容" };
        if (value is null)
        {
            payload.Remove(field); // 欄位完全不出現在 body 裡(與送空字串是不同輸入)。
        }
        else
        {
            payload[field] = value;
        }

        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents", payload);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal($"{field} 不可為空", body["fieldErrors"]![field]!.GetValue<string>());
    }

    // StringLength 邊界:on-point(上限剛好)受理,off-point(超一)回 400 fieldErrors。
    [Theory]
    [InlineData(500, 10)]         // title 上限剛好
    [InlineData(10, 1_000_000)]   // text 上限剛好
    public async Task Create_AtLengthLimit_Accepted(int titleLen, int textLen)
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
            new { title = new string('a', titleLen), text = new string('b', textLen) });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Theory]
    [InlineData(501, 10, "title", "title 長度不可超過 500 字")]
    [InlineData(10, 1_000_001, "text", "text 長度不可超過 1000000 字")]
    public async Task Create_OverLengthLimit_Returns400_WithFieldError(
        int titleLen, int textLen, string field, string message)
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
            new { title = new string('a', titleLen), text = new string('b', textLen) });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal(message, body["fieldErrors"]![field]!.GetValue<string>());
    }

    // 上面兩組 400 測試每次只讓一個欄位失效,另一個永遠合法;title 與 text 的驗證屬性彼此獨立,
    // ValidationErrorResponse 走的是「掃過整個 ModelState」的聚合路徑,所以兩欄同時不合法時
    // 必須在同一份 fieldErrors 裡看到兩個 key(同屬性型別與跨屬性型別各一個代表)。
    [Theory]
    [InlineData(0, 0, "title 不可為空", "text 不可為空")]
    [InlineData(501, 0, "title 長度不可超過 500 字", "text 不可為空")]
    public async Task Create_BothFieldsInvalid_Returns400_WithBothFieldErrors(
        int titleLen, int textLen, string titleMessage, string textMessage)
    {
        var resp = await _factory.UserClient().PostAsJsonAsync("/api/documents",
            new { title = new string('a', titleLen), text = new string('b', textLen) });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        var fieldErrors = body["fieldErrors"]!.AsObject();
        Assert.Equal(titleMessage, fieldErrors["title"]!.GetValue<string>());
        Assert.Equal(textMessage, fieldErrors["text"]!.GetValue<string>());
        Assert.Equal(2, fieldErrors.Count); // 只聚合這兩個欄位,不得混入空 key 或其他雜訊
    }

    [Fact]
    public async Task List_Returns200()
    {
        var resp = await _factory.UserClient().GetAsync("/api/documents");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        var arr = body.AsArray();
        Assert.Single(arr);
        Assert.Equal(3, arr[0]!["chunk_count"]!.GetValue<int>());
        Assert.Equal("2026-07-11T00:00:00Z", arr[0]!["created_at"]!.GetValue<string>());
        Assert.Equal("ready", arr[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_Returns204()
    {
        var resp = await _factory.UserClient().DeleteAsync("/api/documents/doc-1");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns404_WhenMissing()
    {
        var resp = await _factory.UserClient().DeleteAsync("/api/documents/ghost");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("找不到文件：ghost", body["message"]!.GetValue<string>());
    }
}
