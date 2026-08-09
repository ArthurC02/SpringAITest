using Backend.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Backend.Api.Tests;

/// <summary>
/// X-User-Capabilities 是與 X-User-Groups 同一條信任邊界上的內部 header:畸形值必須 fail closed
/// (400,拒絕整個 header),而不是靜默跳過壞值或放行部分授權集合。
/// 名稱文法刻意不驗 —— RequireCapability 是 Ordinal 精確比對,只鎖數量/長度/位元組/控制字元。
/// </summary>
public sealed class IdentityCapabilityHeaderTests
{
    [Fact]
    public void AbsentHeader_IsTheNormalEmptyGrantPath()
        => Assert.Empty(new DefaultHttpContext().Request.UserCapabilities());

    [Fact]
    public void SingleWellFormedValue_ParsesTrimsAndDedupes()
    {
        var request = Request("  workflow.manage   tool.use:search workflow.manage ");

        Assert.Equal(["workflow.manage", "tool.use:search"], request.UserCapabilities());
    }

    [Fact]
    public void RepeatedHeader_IsRejectedWholesale()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdentityHeaders.CapabilitiesHeader] =
            new StringValues(["workflow.manage", "tool.use:search"]);

        AssertRejected(context.Request);
    }

    [Theory]
    [InlineData("workflow.manage\nreporting.read")]
    [InlineData("workflow.manage \n reporting.read")]
    [InlineData("workflow.manage\u0000")]
    [InlineData("workflow.manage\treporting.read")]
    public void ControlCharacters_AreRejectedWholesale(string raw)
        => AssertRejected(Request(raw));

    [Fact]
    public void EntryCount_AtLimitAccepted_OverLimitRejected()
    {
        Assert.Equal(
            IdentityHeaders.MaxCallerCapabilities,
            Request(Capabilities(IdentityHeaders.MaxCallerCapabilities)).UserCapabilities().Count);

        AssertRejected(Request(Capabilities(IdentityHeaders.MaxCallerCapabilities + 1)));
    }

    [Fact]
    public void EntryLength_AtLimitAccepted_OverLimitRejected()
    {
        Assert.Equal(
            [new string('c', IdentityHeaders.MaxCapabilityLength)],
            Request(new string('c', IdentityHeaders.MaxCapabilityLength)).UserCapabilities());

        AssertRejected(Request(new string('c', IdentityHeaders.MaxCapabilityLength + 1)));
    }

    [Fact]
    public void WireBytes_AtLimitAccepted_OverLimitRejected()
    {
        // 17 段 × 240 字元 + 16 個分隔空白 = 4096 bytes:段數(17≤64)與段長(240≤256)都遠在界內,
        // 只有 wire 位元組數壓在邊界上,+1 後也只有位元組數這一項越界。
        var atLimit = string.Join(
            ' ',
            Enumerable.Range(0, 17).Select(i => i.ToString("D3") + new string('c', 237)));
        Assert.Equal(IdentityHeaders.MaxCapabilitiesWireUtf8Bytes, atLimit.Length);
        Assert.Equal(17, Request(atLimit).UserCapabilities().Count);

        AssertRejected(Request(atLimit + "c"));
    }

    private static void AssertRejected(HttpRequest request)
    {
        var error = Assert.Throws<ApiException>(() => request.UserCapabilities());
        Assert.Equal(StatusCodes.Status400BadRequest, error.Status);
        Assert.Equal(
            "X-User-Capabilities contains an invalid authenticated capability set",
            error.Message);
    }

    private static string Capabilities(int count)
        => string.Join(' ', Enumerable.Range(0, count).Select(i => $"tool.use:t{i}"));

    private static HttpRequest Request(string raw)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[IdentityHeaders.CapabilitiesHeader] = raw;
        return context.Request;
    }
}
