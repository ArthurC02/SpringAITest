using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Platform.Service;
using Platform.Service.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Platform.Web.Tests;

/// <summary>
/// Agent Skill 匯入的「真管線」整合測試(第二次修法的釘子)。
/// 其餘 Skill 測試把 ISkillService 換成 FakeSkillService,所以真正的 controller → SkillService → BackendClient
/// 轉送路徑從沒被走過 —— 那正是第一次修法(ByteArrayContent 帶 Content-Length)通過單元測試卻仍在 live stack 壞掉的原因:
/// [ApiController] MVC pipeline 下 Request.Body 已被排空,backend 收到「格式正確但空」的 multipart(400 缺少 package)。
/// 本測試保留真 SkillService,只把 BackendClient 的下游換成一個會攔截轉送請求的 fake HttpMessageHandler,
/// 以真的 multipart/form-data POST 打 /api/skills/{name}/import,斷言轉送給 backend 的請求是「非空 multipart、
/// Content-Length > 0、內含 package 檔位且位元組等於上傳位元組」。舊的排空行為會讓 package 位元組不在轉送 body 裡 → 失敗。
/// </summary>
public sealed class SkillImportPipelineTests : IClassFixture<SkillImportPipelineTests.RealSkillServiceFactory>
{
    private readonly RealSkillServiceFactory _factory;

    public SkillImportPipelineTests(RealSkillServiceFactory factory) => _factory = factory;

    /// <summary>上傳的 package 位元組(含非文字位元組,確保「逐字送達」不是靠文字巧合)。</summary>
    private static readonly byte[] UploadedBytes =
        { 0x50, 0x4B, 0x03, 0x04, 0x53, 0x41, 0x4C, 0x45, 0x53, 0x00, 0x7F, 0xFE, 0x11 };

    private static MultipartFormDataContent Package(string filename = "sales-helper.zip")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(UploadedBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "package", filename);
        return content;
    }

    // ADMIN 以真 multipart POST → 200,且轉送給 backend 的請求帶著非空 package 檔位(位元組逐字一致)。
    [Fact]
    public async Task Import_ForwardsNonEmptyPackagePart_ToBackend()
    {
        _factory.Backend.Reset(HttpStatusCode.OK,
            """{"name":"sales-helper","description":"匯入的代理技能","required_role":"USER","enabled":true,"current_revision":1,"kind":"agentic"}""");

        var resp = await _factory.AdminClient().PostAsync("/api/skills/sales-helper/import", Package());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.ReadJsonAsync();
        Assert.Equal("sales-helper", body["name"]!.GetValue<string>());
        Assert.Equal("agentic", body["kind"]!.GetValue<string>());

        // 轉送確實抵達 backend 的 import 端點。
        Assert.Equal("/api/skills/sales-helper/import", _factory.Backend.Path);

        // 身分/信任邊界 header 如實轉發(取自已驗證的 JWT claims)。
        Assert.Equal("demo-a", _factory.Backend.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", _factory.Backend.Header("X-User-Id"));
        Assert.Equal("ADMIN", _factory.Backend.Header("X-User-Role"));
        Assert.False(string.IsNullOrEmpty(_factory.Backend.Header("X-Internal-Token")));

        // 轉送的是非空 multipart,帶 Content-Length(非 chunked),> 0。
        Assert.StartsWith("multipart/form-data", _factory.Backend.ContentType);
        Assert.NotNull(_factory.Backend.ContentLength);
        Assert.True(_factory.Backend.ContentLength > 0);
        Assert.NotEmpty(_factory.Backend.Body!);

        // 關鍵斷言(舊排空行為在此失敗):轉送 body 內含一個名為 package 的檔位,且其位元組逐字等於上傳位元組。
        var forwarded = _factory.Backend.Body!;
        Assert.Contains("name=package", Encoding.UTF8.GetString(forwarded));
        Assert.True(IndexOf(forwarded, UploadedBytes) >= 0,
            "轉送給 backend 的 multipart 必須含上傳的 package 位元組(舊 Request.Body 排空 → 空 package → 找不到)。");
    }

    [Fact]
    public async Task ImportDerived_ForwardsToAdditiveBackendRoute_WithIdentityAndBytes()
    {
        _factory.Backend.Reset(HttpStatusCode.OK,
            """{"name":"server-derived","description":"d","required_role":"USER","enabled":true,"current_revision":1,"kind":"agentic"}""");

        var response = await _factory.AdminClient().PostAsync(
            "/api/skills/import", Package("derived.zip"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/skills/import", _factory.Backend.Path);
        Assert.Equal("demo-a", _factory.Backend.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", _factory.Backend.Header("X-User-Id"));
        Assert.Equal("ADMIN", _factory.Backend.Header("X-User-Role"));
        Assert.False(string.IsNullOrEmpty(_factory.Backend.Header("X-Internal-Token")));
        Assert.True(IndexOf(_factory.Backend.Body!, UploadedBytes) >= 0);
    }

    // 缺檔的兩個 null 分支(SkillController.ReadPackageAsync / PackageFileName):ADMIN 送出沒有 package 檔位的
    // multipart 時,platform 不得自己回 400,而是轉送「空 bytes + package.zip」讓 backend 依角色 → 檔案的順序判。
    // (USER 早已被 [AdminOnly] 擋掉,所以這條路徑只有 ADMIN 走得到。)
    [Fact]
    public async Task Import_NoFilePart_ForwardsEmptyPackageAndDefaultFileName()
    {
        _factory.Backend.Reset(HttpStatusCode.BadRequest,
            """{"timestamp":"2026-07-25T00:00:00Z","status":400,"message":"缺少 package 檔案","fieldErrors":{}}""");
        using var noFile = new MultipartFormDataContent();
        noFile.Add(new StringContent("ignored"), "note");

        var response = await _factory.AdminClient().PostAsync(
            "/api/skills/sales-helper/import", noFile);

        // backend 才是判定者:platform 原樣把它的 400 帶回去。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("缺少 package 檔案", (await response.ReadJsonAsync())["message"]!.GetValue<string>());

        // 轉送出去的仍是一份合法 multipart,含名為 package、檔名 package.zip 的空檔位。
        Assert.Equal("/api/skills/sales-helper/import", _factory.Backend.Path);
        var forwarded = Encoding.UTF8.GetString(_factory.Backend.Body!);
        Assert.Contains("name=package", forwarded);
        Assert.Contains("package.zip", forwarded);
        Assert.DoesNotContain("ignored", forwarded);
    }

    /// <summary>在 haystack 位元組序列中尋找 needle 的起始索引;找不到回 -1。</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }

            if (match) return i;
        }

        return -1;
    }

    /// <summary>
    /// 保留真 SkillService、把 BackendClient 的主要處理常式換成攔截用的 fake handler。
    /// 沿用基底工廠對其他 backend 相依/LLM/mem0 的 fake 佈線(app 才能開機且不打真 :8002/LiteLLM)。
    /// </summary>
    public sealed class RealSkillServiceFactory : TestWebAppFactory
    {
        public CapturingBackendHandler Backend { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // 換回真 SkillService(基底工廠把它換成 FakeSkillService);其 BackendClient 走攔截 handler。
                services.RemoveAll<ISkillService>();
                services.AddScoped<ISkillService, SkillService>();
                services.AddHttpClient<BackendClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => Backend);
            });
        }
    }

    /// <summary>攔截並緩存轉送給 backend 的請求(路徑、Content-Type、Content-Length、body 位元組、header)。</summary>
    public sealed class CapturingBackendHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _responseJson = "{}";
        private HttpRequestMessage? _last;

        public string? Path { get; private set; }
        public string? ContentType { get; private set; }
        public long? ContentLength { get; private set; }
        public byte[]? Body { get; private set; }

        public void Reset(HttpStatusCode status, string responseJson)
        {
            _status = status;
            _responseJson = responseJson;
        }

        public string? Header(string name)
            => _last is not null && _last.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _last = request;
            Path = request.RequestUri!.AbsolutePath;
            if (request.Content is not null)
            {
                // 先取 body(觸發序列化),再讀 Content-Length header(此時 MultipartFormDataContent 已算出長度)。
                Body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                ContentType = request.Content.Headers.ContentType?.ToString();
                ContentLength = request.Content.Headers.ContentLength;
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
