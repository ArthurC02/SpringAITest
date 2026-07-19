using System.Net;
using System.Net.Sockets;

namespace Platform.Web.Infrastructure;

internal static class TrustedProxyNetwork
{
    internal static bool TryParse(string? cidr, out IPNetwork? network)
    {
        network = null;
        if (string.IsNullOrWhiteSpace(cidr)
            || !IPNetwork.TryParse(cidr, out var parsed)
            || parsed.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        network = parsed;
        return true;
    }
}