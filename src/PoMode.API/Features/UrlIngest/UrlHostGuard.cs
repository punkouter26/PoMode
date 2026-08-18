using System.Net;
using System.Net.Sockets;

namespace PoMode.API.Features.UrlIngest;

/// <summary>
/// SSRF guard for URL ingest: the endpoint's whole purpose is fetching an arbitrary public page,
/// so the one thing it must never do is reach *inward* — loopback, RFC1918, link-local (cloud
/// metadata), or IPv6 equivalents. DNS is resolved once here and again by yt-dlp, so a rebinding
/// attacker could still race the second lookup; this is defense-in-depth for a dev-facing tool,
/// not a hostile-multitenant boundary. <c>UrlIngest:AllowPrivateHosts=true</c> disables the guard
/// for tests and deliberate LAN use.
/// </summary>
public static class UrlHostGuard
{
    public static async Task<bool> PointsAtPrivateNetworkAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.IdnHost;
        if (IPAddress.TryParse(host, out var literal))
        {
            return IsPrivate(literal);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Any(IsPrivate);
        }
        catch (SocketException)
        {
            // Unresolvable: nothing to reach inward at, and yt-dlp will fail with its own message.
            return false;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }
}
