using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Service;

namespace Platform.Web.Tests;

/// <summary>
/// correlationId 跨服務鏈路的 Platform 這一段。Platform 是權威來源:值取自本請求的 TraceIdentifier
/// (= ApiError body 的 correlationId),往 backend/workflow 出站時以 X-Correlation-Id 轉發,
/// 錯誤回應再以同名 header 回給呼叫端 —— 三者必須是同一串,否則使用者手上的編號對不回任何一行日誌。
/// </summary>
public sealed class CorrelationIdTests : IClassFixture<CorrelationIdTests.DownstreamFixture>
{
    private readonly DownstreamFixture _fixture;

    public CorrelationIdTests(DownstreamFixture fixture) => _fixture = fixture;

    // 一條路徑上的三個點必須同號:出站給 backend 的 header、回應 header、ApiError body。
    // 用 backend 500 → platform 502 這格,是因為只有錯誤回應才帶 X-Correlation-Id(成功回應刻意不加),
    // 而同一次請求裡出站早於回應寫出 —— 兩者同號才證明它真的是「本次請求」的編號而非每次重生。
    [Fact]
    public async Task OutboundBackendRequest_AndErrorResponse_ShareThisRequestsCorrelationId()
    {
        using var client = _fixture.CreateClient().WithToken(
            _fixture.IssueToken(capabilities: ["workflow.manage"]));

        var response = await client.GetAsync("/api/admin/operations/metrics");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var forwarded = _fixture.Backend.Header("X-Correlation-Id");
        Assert.False(string.IsNullOrWhiteSpace(forwarded));
        Assert.Equal(forwarded, Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        Assert.Equal(forwarded, (await response.ReadJsonAsync())["correlationId"]!.GetValue<string>());
    }

    // 不可信輸入:公開端點的瀏覽器可以送任何東西。Platform 是權威來源,一律用自己的 TraceIdentifier,
    // 絕不反射呼叫端帶進來的值(否則等於開一條把任意字串寫進三個服務日誌的通道)。
    [Fact]
    public async Task CallerSuppliedCorrelationId_IsIgnored_NotReflected()
    {
        using var client = _fixture.CreateClient().WithToken(
            _fixture.IssueToken(capabilities: ["workflow.manage"]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/operations/metrics");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "spoofed-by-browser");

        var response = await client.SendAsync(request);

        Assert.NotEqual("spoofed-by-browser", _fixture.Backend.Header("X-Correlation-Id"));
        Assert.NotEqual("spoofed-by-browser", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
    }

    /// <summary>保留真 BackendClient(含 correlation 轉發 handler),下游固定回 500 → 對外 502。</summary>
    public sealed class DownstreamFixture : TestWebAppFactory
    {
        public CapturingBackendHandler Backend { get; } = new();

        public DownstreamFixture() : base(new() { ["AGENT_WRITE_TOOLS_ENABLED"] = "true" })
            => Backend.Reset(HttpStatusCode.InternalServerError, "{}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackendClient>();
                services.AddHttpClient<BackendClient>().ConfigurePrimaryHttpMessageHandler(() => Backend);
            });
        }
    }
}
