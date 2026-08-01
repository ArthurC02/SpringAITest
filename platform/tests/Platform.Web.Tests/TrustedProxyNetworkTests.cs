using Platform.Web.Infrastructure;

namespace Platform.Web.Tests;

public sealed class TrustedProxyNetworkTests
{
    [Theory]
    [InlineData("10.253.254.0/28")]
    [InlineData("10.253.254.0/32")]
    [InlineData("0.0.0.0/0")]
    public void TryParse_ValidIPv4Cidr_ReturnsNetwork(string cidr)
    {
        var parsed = TrustedProxyNetwork.TryParse(cidr, out var network);

        Assert.True(parsed);
        Assert.NotNull(network);
        Assert.Equal(cidr, network.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-cidr")]
    [InlineData("10.253.254.0/33")]
    [InlineData("fd00::/64")]
    public void TryParse_MissingOrInvalidCidr_DoesNotTrustNetwork(string? cidr)
    {
        var parsed = TrustedProxyNetwork.TryParse(cidr, out var network);

        Assert.False(parsed);
        Assert.Null(network);
    }

    // Host bits set is NOT rejected: IPNetwork.TryParse accepts it and silently widens to the
    // network address, so a mistyped TRUSTED_PROXY_CIDR trusts the whole /28 rather than failing
    // closed. Pinned as characterization — the widening is the behaviour operators actually get.
    [Fact]
    public void TryParse_HostBitsSet_IsAcceptedAndWidenedToNetworkAddress()
    {
        var parsed = TrustedProxyNetwork.TryParse("10.253.254.5/28", out var network);

        Assert.True(parsed);
        Assert.Equal("10.253.254.0/28", network!.ToString());
    }
}