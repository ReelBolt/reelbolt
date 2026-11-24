using System.Net;
using System.Net.Sockets;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// Whether the address presigned URLs are signed against can be reached by a pool node, which sits on the
/// public internet and not on the private network. Judged from the address text alone; nothing is resolved
/// or called.
/// </summary>
public static class PoolStorageReachability
{
    /// <summary>True for an https address whose host is a public IP literal or a dotted public-looking name.</summary>
    public static bool IsPublicHttps(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            return IsPublicAddress(ip);
        }

        if (!host.Contains('.') || host.EndsWith('.'))
        {
            return false; // a compose or intranet name such as "garage"
        }

        string[] privateSuffixes = [".local", ".localhost", ".internal", ".lan", ".home", ".corp", ".intranet", ".private", ".invalid", ".test", ".example"];
        return !privateSuffixes.Any(host.EndsWith);
    }

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] v6 = ip.GetAddressBytes();
            bool uniqueLocal = (v6[0] & 0xFE) == 0xFC;
            return !uniqueLocal && !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast;
        }

        byte[] b = ip.GetAddressBytes();
        return !(b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || b[0] == 0 || b[0] >= 224);
    }
}
