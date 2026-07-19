using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class TrustedProxyNetworkTests
{
    [Fact]
    public void TryParse_ValidIPv4Cidr_ReturnsNetwork()
    {
        var parsed = TrustedProxyNetwork.TryParse("10.253.254.0/28", out var network);

        Assert.True(parsed);
        Assert.NotNull(network);
        Assert.Equal("10.253.254.0/28", network.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cidr")]
    [InlineData("10.253.254.0/33")]
    [InlineData("fd00::/64")]
    public void TryParse_MissingOrInvalidCidr_DoesNotTrustNetwork(string? cidr)
    {
        var parsed = TrustedProxyNetwork.TryParse(cidr, out var network);

        Assert.False(parsed);
        Assert.Null(network);
    }
}