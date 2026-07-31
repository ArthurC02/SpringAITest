using System.Net;
using System.Text.Json;
using Backend.Api.Agents;
using Backend.Api.Common;
using static Backend.Api.Tests.StubHandler; // 共用的 Json(status, body) 回應工廠(Fakes.cs)

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
    [InlineData("""{"valid":true,"canonicalRuleSet":{"version":99999999999,"rules":[]},"errors":[]}""")] // version 是數字但塞不進 Int32
    [InlineData("""{"valid":true,"canonicalRuleSet":{"version":1,"rules":{}},"errors":[]}""")]   // rules 非陣列
    public async Task ValidWithIncompleteCanonicalEnvelope_IsContractFailure502(string response)
    {
        var sut = Build(Json(HttpStatusCode.OK, response));

        var ex = await Assert.ThrowsAsync<ApiException>(() => sut.ValidateAsync(
            "pre-action", Rules, References, "tenant-a", "user-a", "ADMIN", CancellationToken.None));

        Assert.Equal(502, ex.Status);
        Assert.Contains("version/rules", ex.Message);
    }

    // 空陣列與「連 errors key 都沒有」(Errors 反序列化成 null)是同一個 OR 的兩個等價類:
    // valid=false 卻拿不出任何錯誤 = 引擎契約被違反,不是可回報給使用者的驗證失敗。
    [Theory]
    [InlineData("""{"valid":false,"errors":[]}""")]
    [InlineData("""{"valid":false}""")]
    public async Task InvalidWithoutErrors_IsContractFailure502(string response)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Build(Json(HttpStatusCode.OK, response))
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

    /// <summary>
    /// userId/role 是簽章上可空的:null 呼叫者身分必須送出**空字串** header,而不是讓 header 消失
    /// (下游看不見 header 與看見空值是不同的授權輸入);tenant 則不受影響照送。
    /// </summary>
    [Fact]
    public async Task Validate_NullCallerIdentity_SendsEmptyUserAndRoleHeaders()
    {
        var stub = Json(HttpStatusCode.OK,
            """{"valid":true,"canonicalRuleSet":{"version":1,"rules":[]},"errors":[]}""");

        var result = await Build(stub).ValidateAsync(
            "pre-action", Rules, References, "demo-a", null, null, default);

        Assert.True(result.Valid);
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal(string.Empty, stub.Header("X-User-Id"));
        Assert.Equal(string.Empty, stub.Header("X-User-Role"));
    }
}
