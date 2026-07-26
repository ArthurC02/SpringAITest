using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Api.Agents;
using Backend.Api.Common;

namespace Backend.Api.Tests;

public sealed class BusinessRuleValidatorTests
{
    private static readonly JsonElement Rules =
        JsonDocument.Parse("""{"version":1,"rules":[]}""").RootElement.Clone();
    private static readonly BusinessRuleReferenceCatalog References =
        new(new[] { "bound-skill" }, new[] { "safe-tool" });

    private static WorkflowBusinessRuleValidator Build(StubHandler stub)
        => new(new HttpClient(stub), "http://workflow:8001", "tok");

    [Fact]
    public async Task Validate_PostsExactContractAndIdentity_ReturnsCanonicalAst()
    {
        var stub = Json(HttpStatusCode.OK,
            """{"valid":true,"canonicalRuleSet":{"version":1,"rules":[]},"errors":[]}""");

        var result = await Build(stub).ValidateAsync(
            "pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default);

        Assert.True(result.Valid);
        Assert.Equal(1, result.CanonicalRuleSet!.Value.GetProperty("version").GetInt32());
        Assert.Equal("http://workflow:8001/business-rules/validate", stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));
        using var sent = JsonDocument.Parse(stub.LastBody);
        Assert.Equal("pre-action", sent.RootElement.GetProperty("gate").GetString());
        Assert.Equal(JsonValueKind.Object, sent.RootElement.GetProperty("ruleSet").ValueKind);
        Assert.Equal(
            "bound-skill",
            sent.RootElement.GetProperty("referenceCatalog").GetProperty("skills")[0].GetString());
        Assert.Equal(
            "safe-tool",
            sent.RootElement.GetProperty("referenceCatalog").GetProperty("tools")[0].GetString());
    }

    [Fact]
    public async Task Invalid_PreservesPathCodeAndMessage()
    {
        var stub = Json(HttpStatusCode.OK,
            """{"valid":false,"errors":[{"path":"$.rules[0].when","code":"operator_type_mismatch","message":"型別不符"}]}""");

        var result = await Build(stub).ValidateAsync(
            "pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default);

        Assert.False(result.Valid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("$.rules[0].when", error.Path);
        Assert.Equal("operator_type_mismatch", error.Code);
        Assert.Equal("型別不符", error.Message);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task DownstreamHttpFailure_FailsClosedAs502(int status)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Build(Json((HttpStatusCode)status, "{}"))
            .ValidateAsync("pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default));

        Assert.Equal(502, ex.Status);
        Assert.Contains("HTTP " + status, ex.Message);
    }

    [Fact]
    public async Task ValidWithoutCanonicalRuleSet_IsContractFailure502()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Build(Json(
                HttpStatusCode.OK, """{"valid":true,"errors":[]}"""))
            .ValidateAsync("pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default));

        Assert.Equal(502, ex.Status);
        Assert.Contains("canonicalRuleSet", ex.Message);
    }

    [Theory]
    [InlineData("""{"valid":true,"canonicalRuleSet":{},"errors":[]}""")]
    [InlineData("""{"valid":true,"canonicalRuleSet":{"version":2,"rules":[]},"errors":[]}""")]
    [InlineData("""{"valid":true,"canonicalRuleSet":{"version":1},"errors":[]}""")]
    [InlineData("""{"valid":true,"canonicalRuleSet":[],"errors":[]}""")]                        // 非 object
    [InlineData("""{"valid":true,"canonicalRuleSet":{"version":"1","rules":[]},"errors":[]}""")] // version 非數字
    [InlineData("""{"valid":true,"canonicalRuleSet":{"version":1,"rules":{}},"errors":[]}""")]   // rules 非陣列
    public async Task ValidWithIncompleteCanonicalEnvelope_IsContractFailure502(string response)
    {
        var sut = Build(Json(HttpStatusCode.OK, response));

        var ex = await Assert.ThrowsAsync<ApiException>(() => sut.ValidateAsync(
            "pre-action", Rules, References, "tenant-a", "user-a", "ADMIN", CancellationToken.None));

        Assert.Equal(502, ex.Status);
        Assert.Contains("version/rules", ex.Message);
    }

    [Fact]
    public async Task InvalidWithoutErrors_IsContractFailure502()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Build(Json(
                HttpStatusCode.OK, """{"valid":false,"errors":[]}"""))
            .ValidateAsync("pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default));

        Assert.Equal(502, ex.Status);
        Assert.Contains("缺少 errors", ex.Message);
    }

    /// <summary>
    /// 「引擎不可達」的真實形態:除了 HTTP 錯誤碼,還有**完全沒有可用回應**的等價類 —
    /// 傳輸層例外、200 但 body 不是 JSON、200 但 body 是字面 null。三者都必須 fail closed 成 502。
    /// </summary>
    [Fact]
    public async Task RuleEngine_TransportFailureAndMalformedBody_FailClosedAs502()
    {
        var transport = await Assert.ThrowsAsync<ApiException>(() =>
            Build(new StubHandler(_ => throw new HttpRequestException("連線被拒")))
                .ValidateAsync("pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default));
        Assert.Equal(502, transport.Status);
        Assert.Contains("連線被拒", transport.Message);

        var malformed = await Assert.ThrowsAsync<ApiException>(() =>
            Build(Json(HttpStatusCode.OK, "<html>bad gateway</html>"))
                .ValidateAsync("pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default));
        Assert.Equal(502, malformed.Status);

        var empty = await Assert.ThrowsAsync<ApiException>(() =>
            Build(Json(HttpStatusCode.OK, "null"))
                .ValidateAsync("pre-action", Rules, References, "demo-a", "admin-a", "ADMIN", default));
        Assert.Equal(502, empty.Status);
        Assert.Contains("回應內容為空", empty.Message);
    }

    private static StubHandler Json(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

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
            LastBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }

        public string Header(string name) => LastRequest!.Headers.GetValues(name).Single();
    }
}
