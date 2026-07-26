using System.Net;
using Backend.Api.Common;
using Backend.Api.Skills;
using static Backend.Api.Tests.StubHandler; // 共用的 Json(status, body) 回應工廠(Fakes.cs)

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

    // valid=true 的成功路徑同樣要守契約(比照姊妹類 WorkflowSkillPackageValidator.ValidateSuccessContract)。
    // 這三格原本 fail-open:空白 name 會寫入無名 skill;kind=agentic 會造出「agentic 但 package=null」
    // 的壞資料狀態(之後 /package 404、export 用 flow exporter 打包、restore 撞 409);
    // 非法 required_role 直接落地成無人認得的授權值。逐格斷言 detail,避免檢查被短路後仍然綠。
    [Theory]
    [InlineData("""{"valid":true,"errors":[],"skill":{"name":"   ","description":"季報問答"}}""", "skill.name")]
    [InlineData("""{"valid":true,"errors":[],"skill":{"name":"quarterly_qa","kind":"agentic"}}""", "skill.kind")]
    [InlineData("""{"valid":true,"errors":[],"skill":{"name":"quarterly_qa","required_role":"SUPERADMIN"}}""", "skill.required_role")]
    public async Task ValidWithContractViolation_Throws502(string body, string field)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(Json(HttpStatusCode.OK, body)));

        Assert.Equal(502, ex.Status);
        Assert.Contains("引擎回應違反契約", ex.Message);
        Assert.Contains(field, ex.Message);
    }

    // 驗證服務故障的三個等價類:HTTP 錯誤碼、無法解析的 body、完全沒有回應(傳輸例外)。
    // 全部 → 502(不是 422):不得把引擎不可達誤判成使用者的定義有問題。
    // 所有非 2xx 走同一行 `!resp.IsSuccessStatusCode`(無 4xx/5xx 分流)→ 一個代表值即可。
    [Fact]
    public async Task DownstreamHttpError_Throws502()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(
            () => Validate(Json(HttpStatusCode.ServiceUnavailable, "{}")));

        Assert.Equal(502, ex.Status);
        Assert.Contains("HTTP 503", ex.Message);
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
}
