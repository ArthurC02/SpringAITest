using System.Net;
using System.Text;
using Backend.Api.Common;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// WorkflowSkillValidator(真正打 workflow :8001 的 client)。契約:一律回 200,結果在 body。
/// 因此「HTTP 錯誤碼」與「傳輸失敗」都不是「定義不合法」,而是驗證服務故障 → 502,絕不放行未驗證的定義。
/// </summary>
public sealed class SkillValidatorTests
{
    private const string Yaml = "name: quarterly_qa\nflow:\n  - node: query_intake\n";

    private static WorkflowSkillValidator Build(StubHandler stub)
        => new(new HttpClient(stub), "http://workflow:8001", "tok");

    private static Task<SkillValidationResult> Validate(StubHandler stub)
        => Build(stub).ValidateAsync(Yaml, "demo-a", "admin-a", "ADMIN", CancellationToken.None);

    private static StubHandler Json(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task Valid_ReturnsMetadata_AndSendsDefinitionWithIdentityHeaders()
    {
        var stub = Json(HttpStatusCode.OK,
            """{"valid":true,"errors":[],"skill":{"name":"quarterly_qa","description":"季報問答","required_role":"ADMIN"}}""");

        var result = await Validate(stub);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
        Assert.Equal("quarterly_qa", result.Skill!.Name);
        Assert.Equal("季報問答", result.Skill.Description);
        Assert.Equal("ADMIN", result.Skill.RequiredRole);

        // 服務間信任邊界:X-Internal-Token + 三個身分 header 都要如實帶上。
        Assert.Equal("http://workflow:8001/skills/validate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest.Method);
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
        Assert.Contains("\"definition\"", stub.LastBody);
    }

    [Fact]
    public async Task Invalid_ReturnsEngineErrorCodes_WithLines()
    {
        var stub = Json(HttpStatusCode.OK,
            """{"valid":false,"errors":[{"code":"unbounded_loop","message":"loop 缺少 max_iterations","line":7}]}""");

        var result = await Validate(stub);

        Assert.False(result.Valid);
        Assert.Null(result.Skill);
        var error = Assert.Single(result.Errors);
        Assert.Equal("unbounded_loop", error.Code);
        Assert.Equal("loop 缺少 max_iterations", error.Message);
        Assert.Equal(7, error.Line);
    }

    [Fact] // required_role 缺省 → USER(規格 §3.1 的預設值)。
    public async Task Valid_MissingRequiredRole_DefaultsToUser()
    {
        var stub = Json(HttpStatusCode.OK, """{"valid":true,"errors":[],"skill":{"name":"quarterly_qa"}}""");

        var result = await Validate(stub);

        Assert.Equal("USER", result.Skill!.RequiredRole);
        Assert.Equal(string.Empty, result.Skill.Description);
    }

    [Fact] // valid=true 卻沒帶 skill → 引擎違約;backend 無從得知 name → 502,不猜、不寫入。
    public async Task ValidWithoutSkillMetadata_Throws502()
    {
        var stub = Json(HttpStatusCode.OK, """{"valid":true,"errors":[]}""");

        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(stub));

        Assert.Equal(502, ex.Status);
        Assert.Contains("缺少 skill 中繼資料", ex.Message);
    }

    // 驗證服務故障的三個等價類:HTTP 錯誤碼、無法解析的 body、完全沒有回應(傳輸例外)。
    // 全部 → 502(不是 422):不得把引擎不可達誤判成使用者的定義有問題。
    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task DownstreamHttpError_Throws502(int status)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(Json((HttpStatusCode)status, "{}")));

        Assert.Equal(502, ex.Status);
        Assert.Contains("HTTP " + status, ex.Message);
    }

    [Fact]
    public async Task DownstreamGarbageBody_Throws502()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(Json(HttpStatusCode.OK, "not json at all")));

        Assert.Equal(502, ex.Status);
        Assert.Contains("Skill 驗證服務呼叫失敗", ex.Message);
    }

    [Fact]
    public async Task TransportFailure_Throws502()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("連線被拒"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(stub));

        Assert.Equal(502, ex.Status);
        Assert.Contains("連線被拒", ex.Message);
    }

    /// <summary>可控回應、可捕捉最後一次請求(含 body)的 HttpMessageHandler。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _responder(request);
        }

        public string Header(string name) => LastRequest!.Headers.GetValues(name).Single();
    }
}
