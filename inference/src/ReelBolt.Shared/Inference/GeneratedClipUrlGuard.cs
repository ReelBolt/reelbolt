using System.Net;
using System.Net.Sockets;

namespace ReelBolt.Shared.Inference;

/// <summary>Syntactic SSRF guard for vendor-supplied clip download URLs.</summary>
public static class GeneratedClipUrlGuard
{
    public static void EnsureAllowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Download URL is not an absolute URL.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Download URL must use https.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Download URL must not carry credentials.");

        // Uri.Host keeps a trailing dot ("postgres.", "db.svc.cluster.local."), which names the same host: drop it
        // so the single-label and internal-suffix tests below cannot be sidestepped with one.
        string host = uri.Host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.') && !IPAddress.TryParse(host.Trim('[', ']'), out _))
            throw new InvalidOperationException("Download URL host is not a public host.");

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IsPrivate(ip))
            throw new InvalidOperationException("Download URL host is a non-public address.");
    }

    /// <summary>
    /// Deny-by-default: true for every address that is not plainly public. The single check behind
    /// <see cref="EnsureAllowed"/> (literal hosts), <see cref="RequirePublic"/> and both connect callbacks.
    /// IPv4: every special-purpose prefix in <see cref="DeniedV4"/>. IPv6: only global unicast 2000::/3 is
    /// eligible, minus <see cref="DeniedV6"/>; any form that embeds an IPv4 address (mapped, compatible,
    /// NAT64, 6to4) is refused, and its embedded address is also judged by the IPv4 rules.
    /// </summary>
    public static bool IsPrivate(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return InAny(b, DeniedV4);
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return true;

        if (EmbeddedV4(b) is { } v4 && InAny(v4, DeniedV4)) return true;
        if ((b[0] & 0xE0) != 0x20) return true; // outside 2000::/3
        if (InAny(b, AllowedIetfV6)) return false;
        return InAny(b, DeniedV6);
    }

    private static readonly (byte[] Prefix, int Bits)[] DeniedV4 = Cidrs(
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15",
        "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4", "255.255.255.255/32");

    // Inside 2000::/3 except the NAT64 prefixes, which are listed so the intent is explicit.
    // 2001::/23 (IETF Protocol Assignments) is denied whole (S-10) bar AllowedIetfV6; its sub-blocks
    // Teredo 2001::/32, Benchmarking 2001:2::/48, ORCHID 2001:10::/28 and ORCHIDv2 2001:20::/28 all fall in it.
    private static readonly (byte[] Prefix, int Bits)[] DeniedV6 = Cidrs(
        "2001::/23", "2001::/32", "2001:db8::/32", "2001:10::/28", "2001:20::/28", "2002::/16",
        "64:ff9b::/96", "64:ff9b:1::/48", "3fff::/20");

    /// <summary>
    /// S-10: the only parts of 2001::/23 allowed, each a row of the IANA "IPv6 Special-Purpose Address
    /// Registry" (iana-ipv6-special-registry) whose "Globally Reachable" column is True:
    /// <list type="bullet">
    /// <item>2001:1::1/128 Port Control Protocol Anycast (RFC 7723) — True</item>
    /// <item>2001:1::2/128 Traversal Using Relays around NAT Anycast (RFC 8155) — True</item>
    /// <item>2001:1::3/128 DNS-SD Service Registration Protocol Anycast (RFC 9665) — True</item>
    /// <item>2001:3::/32 AMT (RFC 7450) — True</item>
    /// <item>2001:4:112::/48 AS112-v6 (RFC 7535) — True</item>
    /// <item>2001:30::/28 Drone Remote ID Protocol Entity Tags (DETs) Prefix (RFC 9374) — True</item>
    /// </list>
    /// Deliberately NOT allowed although reachable: ORCHIDv2 2001:20::/28 (RFC 7343, True) — an overlay
    /// identifier, never a host to fetch from. Denied as the registry says: 2001::/23 itself (False),
    /// Teredo 2001::/32 (N/A), Benchmarking 2001:2::/48 (False), deprecated ORCHID 2001:10::/28 (False),
    /// and every unassigned remainder.
    /// </summary>
    private static readonly (byte[] Prefix, int Bits)[] AllowedIetfV6 = Cidrs(
        "2001:1::1/128", "2001:1::2/128", "2001:1::3/128", "2001:3::/32", "2001:4:112::/48", "2001:30::/28");

    private static readonly (byte[] Prefix, int Bits)[] V4Embedding = Cidrs(
        "::ffff:0:0/96", "::/96", "64:ff9b::/96");

    /// <summary>The IPv4 address carried by a mapped, compatible, NAT64 (/96) or 6to4 IPv6 address, else null.</summary>
    private static byte[]? EmbeddedV4(byte[] v6)
    {
        if (InAny(v6, V4Embedding)) return v6[12..16];
        if (v6[0] == 0x20 && v6[1] == 0x02) return v6[2..6];
        return null;
    }

    private static bool InAny(byte[] address, (byte[] Prefix, int Bits)[] prefixes)
    {
        foreach (var (prefix, bits) in prefixes)
            if (prefix.Length == address.Length && InPrefix(address, prefix, bits)) return true;
        return false;
    }

    private static bool InPrefix(byte[] address, byte[] prefix, int bits)
    {
        int whole = bits / 8;
        for (int i = 0; i < whole; i++)
            if (address[i] != prefix[i]) return false;
        int rest = bits % 8;
        if (rest == 0) return true;
        int mask = 0xFF << (8 - rest) & 0xFF;
        return (address[whole] & mask) == (prefix[whole] & mask);
    }

    private static (byte[] Prefix, int Bits)[] Cidrs(params string[] cidrs) =>
        [.. cidrs.Select(c => c.Split('/')).Select(p => (IPAddress.Parse(p[0]).GetAddressBytes(), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];

    /// <summary>Returns the addresses to connect to; throws if ANY resolved address is non-public (rebinding defence).</summary>
    public static IPAddress[] RequirePublic(IReadOnlyCollection<IPAddress> resolved)
    {
        if (resolved.Count == 0)
            throw new HttpRequestException("Download host did not resolve.");
        foreach (var ip in resolved)
            if (IsPrivate(ip))
                throw new NonPublicAddressException();
        return resolved.ToArray();
    }

    /// <summary>
    /// HttpClient for vendor-supplied download URLs only: the connect step resolves the host and
    /// validates the IP actually dialled (covers DNS rebinding and every redirect hop); at most 3
    /// redirects, and the handler never follows an https->http downgrade.
    /// </summary>
    public static HttpClient CreateDownloadClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
            ConnectCallback = ConnectPublicOnlyAsync
        };
        var client = new HttpClient(handler);
        if (timeout is { } t) client.Timeout = t;
        return client;
    }

    /// <summary>
    /// Handler for URLs a USER typed (project file import): the same connect-time IP check as
    /// <see cref="CreateDownloadClient"/>, but it follows no redirect itself (the caller re-runs
    /// <see cref="EnsureAllowed"/> on every hop), never uses an ambient proxy (the check must see the
    /// real destination, not the proxy), sends no cookies and does not decompress.
    /// </summary>
    public static SocketsHttpHandler CreateUserUrlHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectPublicOnlyAsync
    };

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        IPAddress[] candidates = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);
        var addrs = RequirePublic(candidates);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A download host resolved to (or literally is) a non-public address. Derives from
/// <see cref="HttpRequestException"/> so callers that already catch that are unaffected; a caller that
/// must tell an SSRF refusal from a network failure catches this type.
/// </summary>
public sealed class NonPublicAddressException() : HttpRequestException("Download host resolves to a non-public address.");

/// <summary>A <see cref="LimitedReadStream"/> was read past its byte limit.</summary>
public sealed class StreamLimitExceededException(long limit) : InvalidOperationException($"The download exceeded the {limit}-byte limit.")
{
    public long Limit { get; } = limit;
}

/// <summary>Read-only stream that throws once more than <c>limit</c> bytes were read, and disposes its response.</summary>
public sealed class LimitedReadStream(Stream inner, long limit, IDisposable owner) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

    private int Track(int n)
    {
        _read += n;
        if (_read > limit) throw new StreamLimitExceededException(limit);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Track(await inner.ReadAsync(buffer, ct));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Track(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); owner.Dispose(); }
        base.Dispose(disposing);
    }
}
