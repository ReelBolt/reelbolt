using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Whether a provider endpoint is one this deployment runs itself (T-037): the only endpoints a self-hosted default may
/// be priced at zero on. Pure: it never resolves a name, so the answer is the same on every host and every boot.
/// </summary>
public static class SelfHostedEndpoint
{
    /// <summary>
    /// True when <paramref name="endpoint"/> is an absolute http or https URL whose host is local: an IP literal that
    /// <see cref="IsLocalAddress"/> accepts, <c>localhost</c> or <c>*.localhost</c>,
    /// <c>host.docker.internal</c>, or a name with no dot at all (a compose or cluster service name). A trailing dot
    /// counts as a dot, so <c>whisper.</c> is hosted. Userinfo, port and path are ignored; the host compares case-insensitively.
    /// </summary>
    public static bool IsLocal(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        string host;
        string idnHost;
        try
        {
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            // SEC-17: HttpClient connects to the IDN (ASCII/punycode) host, so that is the name judged. A host whose Unicode
            // form differs from its ASCII form (Unicode full stops such as U+3002 keep their dots out of the label count),
            // that carries non-ASCII characters, or that has an xn-- label is refused outright: not local, fail closed.
            host = uri.Host.Trim('[', ']');
            idnHost = uri.IdnHost.Trim('[', ']');
        }
        catch (Exception)
        {
            // SEC-19: Uri.IdnHost throws for a host it cannot normalise (U+FE52, U+2024, U+2488 and similar). Such a host
            // is not one this deployment runs, so it is not local. Judging an endpoint must never throw: callers include boot.
            return false;
        }

        if (!host.Equals(idnHost, StringComparison.OrdinalIgnoreCase)
            || !Ascii.IsValid(host)
            || !Ascii.IsValid(idnHost)
            || ("." + idnHost).Contains(".xn--", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(idnHost, out IPAddress? address))
        {
            return IsLocalAddress(address);
        }

        return idnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || idnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || idnHost.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || !idnHost.Contains('.');
    }

    /// <summary>
    /// T-015: an address this deployment can itself be running on, and nothing wider: loopback (127/8, ::1), RFC1918
    /// (10/8, 172.16/12, 192.168/16), CGNAT (100.64/10), link-local (169.254/16, fe80::/10) and IPv6 ULA (fc00::/7); an
    /// IPv4-mapped IPv6 address is judged by its IPv4 form. Deliberately NOT <see cref="GeneratedClipUrlGuard.IsPrivate"/>:
    /// the SSRF guard refuses every non-public range (documentation, benchmarking, Teredo, 6to4, NAT64, multicast,
    /// unspecified), and "refused as a download target" must not mean "priced at zero".
    /// </summary>
    public static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 127
                || b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && (b[1] & 0xC0) == 64)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        return IPAddress.IPv6Loopback.Equals(address)
            || (b[0] & 0xFE) == 0xFC
            || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
    }
}
