using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backend.Api.BusinessWorkflows;
using Backend.Api.Skills;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Backend.Api.Tests;

/// <summary>
/// correlationId 跨服務鏈路的 Backend 這一段:接收 Platform 的 X-Correlation-Id、當成本請求的
/// correlationId 寫進 ApiError body 與回應 header、再原樣轉發給 workflow。三個服務同號才有意義,
/// 所以三個點在同一條測試裡一起斷言。不合格的入站值一律降級成本機自產編號 —— 絕不回錯:
/// 追蹤編號壞掉不該把可觀測性問題升級成功能故障。
/// </summary>
public sealed class CorrelationIdTests : IClassFixture<CorrelationIdTests.RealValidatorFactory>
{
    private const string HeaderName = "X-Correlation-Id";

    private readonly RealValidatorFactory _factory;

    public CorrelationIdTests(RealValidatorFactory factory) => _factory = factory;

    private static JsonObject Definition() => new()
    {
        ["definition"] = "name: correlated_flow\nflow:\n  - node: query_intake\n",
    };

    private HttpClient Admin()
        => _factory.CreateInternalClient().WithTenant("demo-a").WithRole("ADMIN").WithUser("admin-a");

    // 鏈路的三個點:轉發給 workflow 的出站 header、回應 header、ApiError body 的 correlationId。
    [Fact]
    public async Task InboundCorrelationId_ReachesWorkflow_AndBothErrorChannels()
    {
        const string correlationId = "0HMV9C49II9CK:00000042";
        var client = Admin();
        client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, correlationId);

        var response = await client.PostAsJsonAsync("/api/business-workflows", Definition());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(correlationId, _factory.Workflow.Header(HeaderName));
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(HeaderName)));
        Assert.Equal(correlationId, (await response.ReadJsonAsync())["correlationId"]!.GetValue<string>());
    }

    // 上限 128 的 on-point:剛好 128 字元仍是合格值(off-point 129 在下面的不合格等價類裡)。
    [Fact]
    public async Task CorrelationIdOfExactly128Chars_IsAccepted()
    {
        var correlationId = new string('c', 128);
        var client = Admin();
        client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, correlationId);

        var response = await client.PostAsJsonAsync("/api/business-workflows", Definition());

        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(HeaderName)));
        Assert.Equal(correlationId, _factory.Workflow.Header(HeaderName));
    }

    // 不合格值一律降級成本機自產的 TraceIdentifier:不回錯,也絕不把壞值原樣反射回去或往下游轉發 ——
    // 這個值會被 TryAddWithoutValidation 放進出站 header,含控制字元(CR/LF)就是 header 注入。
    // 三個代表值;空值/純空白等價類由 AgentRunsApiTests 在同一個共用檢查上以 Idempotency-Key 覆蓋。
    [Theory]
    [InlineData("duplicate")]     // 恰一個值:重複 header
    [InlineData("too-long")]      // 129 字元(off-point;on-point 128 見上一條)
    [InlineData("control-char")]  // 安全語義:出站 header 注入
    public async Task InvalidCorrelationId_FallsBackToLocalTraceIdentifier(string kind)
    {
        var client = Admin();
        switch (kind)
        {
            case "duplicate":
                client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, "bogus-1");
                client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, "bogus-2");
                break;
            case "too-long":
                client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, "bogus" + new string('z', 124));
                break;
            default:
                client.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, "bogus\u0007id");
                break;
        }

        var response = await client.PostAsJsonAsync("/api/business-workflows", Definition());

        var echoed = Assert.Single(response.Headers.GetValues(HeaderName));
        Assert.False(string.IsNullOrWhiteSpace(echoed));
        Assert.DoesNotContain("bogus", echoed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(echoed, (await response.ReadJsonAsync())["correlationId"]!.GetValue<string>());
        Assert.Equal(echoed, _factory.Workflow.Header(HeaderName));
    }

    // 完全沒帶 header 的既有行為不變:ApiError 仍是完整六欄且 correlationId 非空(自產 TraceIdentifier)。
    [Fact]
    public async Task NoCorrelationIdHeader_StillProducesLocalCorrelationId()
    {
        var response = await Admin().PostAsJsonAsync("/api/business-workflows", Definition());

        (await response.ReadJsonAsync()).AssertApiError(422, "unprocessable_entity");
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(response.Headers.GetValues(HeaderName))));
    }

    /// <summary>
    /// 把 <see cref="ISkillValidator"/> 換回真的 <see cref="WorkflowSkillValidator"/>(基底工廠用 fake 取代了它),
    /// 但 "skill-validator" 這顆 named client 的 primary handler 換成可斷言的 stub —— Program.cs 掛在同一顆
    /// client 上的 correlation 轉發 handler 因此仍在鏈上,測到的是真實接線而不是測試自組的管線。
    /// stub 固定回 valid=false,所以每個案例都停在 422、不寫入任何資料(class fixture 共用亦不互相汙染)。
    /// </summary>
    public sealed class RealValidatorFactory : TestWebAppFactory
    {
        public StubHandler Workflow { get; } =
            StubHandler.Json(HttpStatusCode.OK, """{"valid":false,"errors":[]}""");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("skill-validator").ConfigurePrimaryHttpMessageHandler(() => Workflow);
                services.RemoveAll<ISkillValidator>();
                services.AddScoped<ISkillValidator>(sp => new WorkflowSkillValidator(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("skill-validator"),
                    "http://workflow.test",
                    InternalToken));
            });
        }
    }
}
