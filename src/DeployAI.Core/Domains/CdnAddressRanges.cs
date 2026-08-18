using System.Net;
using System.Net.Sockets;

namespace DeployAI.Core.Domains;

/// <summary>
/// The published edge ranges of the CDNs a user is most likely to have in front of their domain,
/// used only to turn "this does not point at your server" into "this is proxied, and the proxy is
/// why the certificate cannot be issued".
/// </summary>
/// <remarks>
/// A hardcoded list goes stale, which is tolerable here precisely because nothing depends on it
/// being complete: an address outside the list is reported as an ordinary mismatch, which is still
/// true. It only ever upgrades a correct-but-vague message to a correct-and-specific one.
/// </remarks>
public static class CdnAddressRanges
{
    // Cloudflare's published IPv4 ranges (https://www.cloudflare.com/ips-v4). Cloudflare is the
    // case that matters: its proxy is on by default for new records, and an orange-clouded record
    // makes Traefik's HTTP-01 challenge fail every time with no visible cause.
    private static readonly (string Network, int Prefix)[] Ranges =
    [
        ("173.245.48.0", 20),
        ("103.21.244.0", 22),
        ("103.22.200.0", 22),
        ("103.31.4.0", 22),
        ("141.101.64.0", 18),
        ("108.162.192.0", 18),
        ("190.93.240.0", 20),
        ("188.114.96.0", 20),
        ("197.234.240.0", 22),
        ("198.41.128.0", 17),
        ("162.158.0.0", 15),
        ("104.16.0.0", 13),
        ("104.24.0.0", 14),
        ("172.64.0.0", 13),
        ("131.0.72.0", 22)
    ];

    /// <summary>Whether this address belongs to a CDN edge rather than to anyone's own server.</summary>
    public static bool IsKnownProxyAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var value = ToUInt32(parsed);
        foreach (var (network, prefix) in Ranges)
        {
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if ((value & mask) == (ToUInt32(IPAddress.Parse(network)) & mask))
            {
                return true;
            }
        }

        return false;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
