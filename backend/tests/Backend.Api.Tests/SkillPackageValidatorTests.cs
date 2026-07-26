using System.Net;
using System.Text;
using Backend.Api.Common;
using Backend.Api.Skills;

namespace Backend.Api.Tests;

/// <summary>
/// WorkflowSkillPackageValidator(真正打 workflow :8001 的 POST /skills/validate-package 的 client)。
/// AST-P0-011:Backend → Workflow 的驗證請求必須是 **multipart**,且帶 expected_name、internal token、
/// tenant/user/role 身分標頭;**不得**存在 base64/JSON 驗證路徑(唯一 transport)。
/// 契約同 flow validate:一律 200,結果在 body → 非 200 / 傳輸失敗 = 服務故障 → 502。
/// </summary>
public sealed class SkillPackageValidatorTests
{
    // 可辨識的原始 zip marker:若請求把 package 以 base64 編碼,body 就不會逐字出現此字串。
    private static readonly byte[] PackageBytes = Encoding.UTF8.GetBytes("PK-RAW-ZIP-MARKER-");

    private static WorkflowSkillPackageValidator Build(StubHandler stub)
        => new(new HttpClient(stub), "http://workflow:8001", "tok");

    private static Task<SkillPackageValidationResult> Validate(StubHandler stub)
        => Build(stub).ValidatePackageAsync(
            PackageBytes, "sales-helper.zip", "sales-helper", "demo-a", "admin-a", "ADMIN", CancellationToken.None);

    private static Task<SkillPackageValidationResult> ValidateDerived(StubHandler stub)
        => Build(stub).ValidatePackageAsync(
            PackageBytes, "sales-helper.zip", expectedName: null,
            "demo-a", "admin-a", "ADMIN", CancellationToken.None);

    private static StubHandler Json(HttpStatusCode status, string body)
        => new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    private const string ValidBody =
        """
        {"valid":true,"errors":[],
         "skill":{"name":"sales-helper","description":"銷售小幫手","required_role":"USER","kind":"agentic"},
         "canonical_definition":"name: sales-helper\ndescription: 銷售小幫手\nmetadata:\n  kind: agentic\n  required_role: USER\n  timeout_seconds: \"30\"\n  input_schema: \"{}\"\n",
         "package_manifest":{"entries":["SKILL.md"],"sha256":"abc"}}
        """;

    // ---- AST-P0-011:multipart + expected_name + internal token + 身分標頭;無 base64 ----

    [Fact]
    public async Task Request_IsMultipart_WithExpectedNameTokenAndIdentityHeaders_NoBase64()
    {
        var stub = Json(HttpStatusCode.OK, ValidBody);

        await Validate(stub);

        var req = stub.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://workflow:8001/skills/validate-package", req.RequestUri!.ToString());

        // transport 必須是 multipart/form-data(唯一路徑),不得是 application/json base64。
        Assert.IsType<MultipartFormDataContent>(req.Content);
        Assert.Equal("multipart/form-data", req.Content.Headers.ContentType!.MediaType);

        // 內部信任邊界:token + 三個身分 header 如實帶上。
        Assert.Equal("tok", stub.Header("X-Internal-Token"));
        Assert.Equal("demo-a", stub.Header("X-Tenant-Id"));
        Assert.Equal("admin-a", stub.Header("X-User-Id"));
        Assert.Equal("ADMIN", stub.Header("X-User-Role"));

        // multipart body(handler 於 dispose 前已捕捉)含 package part(application/zip)與 expected_name part。
        // 去掉可能的引號後比對欄位名,兼容框架是否加引號。
        var unquoted = stub.LastBody.Replace("\"", string.Empty);
        Assert.Contains("name=package", unquoted);
        Assert.Contains("name=expected_name", unquoted);
        Assert.Contains("sales-helper", stub.LastBody);
        Assert.Contains("application/zip", stub.LastBody);

        // 原始 zip bytes 逐字出現(未經 base64 編碼)→ 證明沒有 base64/JSON 驗證路徑。
        Assert.Contains("-RAW-ZIP-MARKER-", stub.LastBody);
        Assert.DoesNotContain("package_base64", stub.LastBody);
        Assert.DoesNotContain("application/json", stub.LastBody);
    }

    [Fact]
    public async Task DerivedNameRequest_OmitsExpectedName_AndAcceptsCanonicalIdentity()
    {
        var stub = Json(HttpStatusCode.OK, ValidBody);

        var result = await ValidateDerived(stub);

        Assert.True(result.Valid);
        Assert.Equal("sales-helper", result.Skill!.Name);
        var unquoted = stub.LastBody.Replace("\"", string.Empty);
        Assert.Contains("name=package", unquoted);
        Assert.DoesNotContain("name=expected_name", unquoted);
    }

    [Fact]
    public async Task DerivedNameResponse_CanonicalIdentityMismatch_Throws502()
    {
        const string body =
            """
            {"valid":true,"errors":[],
             "skill":{"name":"sales-helper","description":"d","required_role":"USER","kind":"flow"},
             "canonical_definition":"name: other\ndescription: d\nflow: []\n"}
            """;

        var error = await Assert.ThrowsAsync<ApiException>(
            () => ValidateDerived(Json(HttpStatusCode.OK, body)));

        Assert.Equal(502, error.Status);
        Assert.Contains("canonical_definition.name", error.Message);
    }

    // ---- valid=true:回 metadata(含 kind)+ canonical_definition ----

    [Fact]
    public async Task Valid_ReturnsMetadataKindAndCanonicalDefinition()
    {
        var result = await Validate(Json(HttpStatusCode.OK, ValidBody));

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
        Assert.Equal("sales-helper", result.Skill!.Name);
        Assert.Equal("銷售小幫手", result.Skill.Description);
        Assert.Equal("USER", result.Skill.RequiredRole);
        Assert.Equal("agentic", result.Skill.Kind);
        Assert.Contains("name: sales-helper", result.CanonicalDefinition);
    }

    // description 空/缺席不是違約:definition-only 寫入允許缺席、SkillExporter 匯出成 `description: ""`,
    // 要求非空會讓 backend 自己匯出的 zip 匯不回來。export → import 的整條 round trip 由
    // SkillExportTests.Export_BlankDescription_RoundTripsBackThroughPackageImportValidation 背書。
    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task Valid_BlankOrMissingDescription_IsAccepted_AsEmptyString(string descriptionJson)
    {
        var body =
            $$"""
              {"valid":true,"errors":[],
               "skill":{"name":"sales-helper","description":{{descriptionJson}},"required_role":"USER","kind":"flow"},
               "canonical_definition":"name: sales-helper\ndescription: \"\"\nflow: []\n"}
              """;

        var result = await Validate(Json(HttpStatusCode.OK, body));

        Assert.True(result.Valid);
        Assert.Equal(string.Empty, result.Skill!.Description);
    }

    [Fact]
    public async Task Valid_FlowRequiresExplicitKindAndRole()
    {
        var body =
            """
            {"valid":true,"errors":[],
             "skill":{"name":"sales-helper","description":"d","required_role":"USER","kind":"flow"},
             "canonical_definition":"name: sales-helper\ndescription: d\nflow: []\n"}
            """;

        var result = await Validate(Json(HttpStatusCode.OK, body));

        Assert.Equal("flow", result.Skill!.Kind);
        Assert.Equal("USER", result.Skill.RequiredRole);
    }

    // ---- valid=false:回錯誤碼,無可寫入的 metadata/definition ----

    [Fact]
    public async Task Invalid_ReturnsErrorCodes_AndNoSkillOrCanonical()
    {
        var body =
            """
            {"valid":false,"errors":[{"code":"path_traversal","message":"非法路徑：../x","line":null}]}
            """;

        var result = await Validate(Json(HttpStatusCode.OK, body));

        Assert.False(result.Valid);
        Assert.Null(result.Skill);
        Assert.Null(result.CanonicalDefinition);
        var error = Assert.Single(result.Errors);
        Assert.Equal("path_traversal", error.Code);
    }

    // ---- 服務故障三等價類:HTTP 錯誤碼、無法解析 body、傳輸例外(沒有回應)→ 全部 502 ----

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
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(Json(HttpStatusCode.OK, "not json")));

        Assert.Equal(502, ex.Status);
        Assert.Contains("Skill 套件驗證服務呼叫失敗", ex.Message);
    }

    [Fact]
    public async Task TransportFailure_Throws502()
    {
        var stub = new StubHandler(_ => throw new HttpRequestException("連線被拒"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(stub));

        Assert.Equal(502, ex.Status);
        Assert.Contains("連線被拒", ex.Message);
    }

    [Fact] // valid=true 卻缺 canonical_definition → 引擎違約;backend 無從寫入 → 502。
    public async Task ValidWithoutCanonical_Throws502()
    {
        var body = """{"valid":true,"errors":[],"skill":{"name":"sales-helper"}}""";

        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(Json(HttpStatusCode.OK, body)));

        Assert.Equal(502, ex.Status);
        Assert.Contains("canonical_definition", ex.Message);
    }

    // 每格帶自己的 detail 片段:只斷言「引擎回應違反契約」的話,任何一項檢查被短路掉
    // (或檢查順序被改成先撞別項)測試照樣綠 —— 那等於沒測到「哪一格」在守。
    // 具名路徑(expected_name="sales-helper")下 :144-148 的 name 檢查先拋,
    // 因此 canonical.name 不一致那格在此不可達,由 DerivedNameResponse_CanonicalIdentityMismatch_Throws502 覆蓋。
    public static TheoryData<string, string> InvalidSuccessContracts => new()
    {
        {
            // route name mismatch
            """
            {"valid":true,"errors":[],"skill":{"name":"other","description":"d","required_role":"USER","kind":"flow"},"canonical_definition":"name: other\ndescription: d\nflow: []\n"}
            """,
            "skill.name 必須等於 expected_name 'sales-helper'"
        },
        {
            // missing kind (must not default)
            """
            {"valid":true,"errors":[],"skill":{"name":"sales-helper","description":"d","required_role":"USER"},"canonical_definition":"name: sales-helper\ndescription: d\nflow: []\n"}
            """,
            "skill.kind 必須是 flow 或 agentic"
        },
        {
            // invalid kind enum
            """
            {"valid":true,"errors":[],"skill":{"name":"sales-helper","description":"d","required_role":"USER","kind":"script"},"canonical_definition":"name: sales-helper\ndescription: d\nflow: []\n"}
            """,
            "skill.kind 必須是 flow 或 agentic"
        },
        {
            // invalid required_role enum
            """
            {"valid":true,"errors":[],"skill":{"name":"sales-helper","description":"d","required_role":"SUPERADMIN","kind":"flow"},"canonical_definition":"name: sales-helper\ndescription: d\nflow: []\n"}
            """,
            "skill.required_role 必須是 USER 或 ADMIN"
        },
        {
            // canonical kind mismatch
            """
            {"valid":true,"errors":[],"skill":{"name":"sales-helper","description":"d","required_role":"USER","kind":"agentic"},"canonical_definition":"name: sales-helper\ndescription: d\nflow: []\n"}
            """,
            "canonical_definition kind 與 skill.kind 不一致"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidSuccessContracts))]
    public async Task ValidResponseContractViolation_Throws502(string body, string detail)
    {
        var error = await Assert.ThrowsAsync<ApiException>(
            () => Validate(Json(HttpStatusCode.OK, body)));

        Assert.Equal(502, error.Status);
        Assert.Equal("Skill 套件驗證服務呼叫失敗：引擎回應違反契約（" + detail + "）", error.Message);
    }

    /// <summary>可控回應、可捕捉最後一次請求(含 multipart body 字串)的 HttpMessageHandler。</summary>
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
