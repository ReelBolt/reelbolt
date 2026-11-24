#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// T-013: <see cref="GeneratedClipUrlGuard.IsPrivate"/> is deny-by-default. IPv4 rejects every special-purpose
/// prefix; IPv6 accepts only global unicast 2000::/3 minus Teredo, documentation, ORCHID and 6to4, so every form
/// that embeds an IPv4 address (mapped, compatible, NAT64, 6to4) is refused. Literal addresses only: no DNS, no dial.
/// </summary>
public sealed class GeneratedClipUrlGuardAddressTests
{
    [Theory]
    [InlineData("0.0.0.0")]               // 0/8
    [InlineData("0.1.2.3")]               // 0/8
    [InlineData("10.0.0.1")]              // 10/8
    [InlineData("10.255.255.255")]        // 10/8
    [InlineData("100.64.0.1")]            // 100.64/10 CGNAT
    [InlineData("100.127.255.254")]       // 100.64/10 CGNAT
    [InlineData("127.0.0.1")]             // 127/8
    [InlineData("127.255.255.254")]       // 127/8
    [InlineData("169.254.169.254")]       // 169.254/16
    [InlineData("172.16.0.1")]            // 172.16/12
    [InlineData("172.31.255.254")]        // 172.16/12
    [InlineData("192.0.0.1")]             // 192.0.0/24
    [InlineData("192.0.2.1")]             // 192.0.2/24 TEST-NET-1
    [InlineData("192.88.99.1")]           // 192.88.99/24 6to4 relay anycast
    [InlineData("192.168.1.1")]           // 192.168/16
    [InlineData("198.18.0.1")]            // 198.18/15
    [InlineData("198.19.255.254")]        // 198.18/15
    [InlineData("198.51.100.1")]          // 198.51.100/24 TEST-NET-2
    [InlineData("203.0.113.1")]           // 203.0.113/24 TEST-NET-3
    [InlineData("224.0.0.1")]             // 224/4 multicast
    [InlineData("239.255.255.255")]       // 224/4 multicast
    [InlineData("240.0.0.1")]             // 240/4 reserved
    [InlineData("255.255.255.254")]       // 240/4 reserved
    [InlineData("255.255.255.255")]       // limited broadcast
    public void A_special_purpose_IPv4_address_is_rejected(string ip) =>
        GeneratedClipUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("::")]                                      // ::/128 unspecified
    [InlineData("::1")]                                     // loopback
    [InlineData("fe80::1")]                                 // fe80::/10 link-local
    [InlineData("fec0::1")]                                 // fec0::/10 site-local (deprecated)
    [InlineData("fc00::1")]                                 // fc00::/7 ULA
    [InlineData("fd00::1")]                                 // fc00::/7 ULA
    [InlineData("ff02::1")]                                 // ff00::/8 multicast
    [InlineData("100::1")]                                  // 100::/64 discard
    [InlineData("::ffff:0:a00:1")]                          // ::ffff:0:0:0/96 IPv4-translated
    [InlineData("2001::1")]                                 // 2001::/32 Teredo
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]    // 2001::/32 Teredo
    [InlineData("2001:db8::1")]                             // 2001:db8::/32 documentation
    [InlineData("2001:db8:ffff::1")]                        // 2001:db8::/32 documentation
    [InlineData("2001:10::1")]                              // 2001:10::/28 ORCHID
    [InlineData("2001:1f::1")]                              // 2001:10::/28 ORCHID
    [InlineData("2001:20::1")]                              // 2001:20::/28 ORCHIDv2
    [InlineData("2001:2f::1")]                              // 2001:20::/28 ORCHIDv2
    [InlineData("2002::1")]                                 // 2002::/16 6to4
    [InlineData("2002:808:808::1")]                         // 6to4 of public 8.8.8.8: rejected regardless
    [InlineData("64:ff9b::808:808")]                        // 64:ff9b::/96 NAT64 of public 8.8.8.8
    [InlineData("64:ff9b:1::1")]                            // 64:ff9b:1::/48 local-use NAT64
    [InlineData("3fff::1")]                                 // 3fff::/20 documentation
    [InlineData("3fff:fff::1")]                             // 3fff::/20 documentation
    [InlineData("::ffff:8.8.8.8")]                          // IPv4-mapped of a public address: outside 2000::/3
    [InlineData("::8.8.8.8")]                               // IPv4-compatible of a public address: outside 2000::/3
    public void A_non_global_or_special_purpose_IPv6_address_is_rejected(string ip) =>
        GeneratedClipUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().BeTrue();

    /// <summary>
    /// S-10: the rest of IETF 2001::/23 is denied too (IANA IPv6 Special-Purpose Address Registry:
    /// "Globally Reachable" False or N/A), above all 2001:2::/48 benchmarking.
    /// </summary>
    [Theory]
    [InlineData("2001:2::1")]                // 2001:2::/48 Benchmarking (not globally reachable)
    [InlineData("2001:2:0:ffff::1")]         // 2001:2::/48 Benchmarking
    [InlineData("2001:1::4")]                // 2001:1::/32 outside the three anycast /128s
    [InlineData("2001:1::")]                 // 2001:1::/32 outside the three anycast /128s
    [InlineData("2001:4::1")]                // 2001:4::/32 outside AS112-v6 2001:4:112::/48
    [InlineData("2001:5::1")]                // unassigned in 2001::/23
    [InlineData("2001:40::1")]               // unassigned in 2001::/23
    [InlineData("2001:100::1")]              // unassigned in 2001::/23
    [InlineData("2001:1ff:ffff::1")]         // last /48 of 2001::/23
    public void The_rest_of_the_IETF_protocol_assignment_block_is_rejected(string ip) =>
        GeneratedClipUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().BeTrue();

    /// <summary>S-10: the 2001::/23 assignments IANA lists as globally reachable stay allowed.</summary>
    [Theory]
    [InlineData("2001:1::1")]                // Port Control Protocol Anycast
    [InlineData("2001:1::2")]                // TURN Anycast
    [InlineData("2001:1::3")]                // DNS-SD Service Registration Protocol Anycast
    [InlineData("2001:3::1")]                // 2001:3::/32 AMT
    [InlineData("2001:4:112::1")]            // 2001:4:112::/48 AS112-v6
    [InlineData("2001:30::1")]               // 2001:30::/28 Drone Remote ID (DET)
    [InlineData("2001:200::1")]              // just above 2001::/23
    public void A_globally_reachable_IETF_assignment_is_allowed(string ip) =>
        GeneratedClipUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().BeFalse();

    [Theory]
    [InlineData("::ffff:127.0.0.1")]         // mapped
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::127.0.0.1")]              // compatible
    [InlineData("::10.0.0.1")]
    [InlineData("::169.254.169.254")]
    [InlineData("64:ff9b::127.0.0.1")]       // NAT64 well-known prefix
    [InlineData("64:ff9b::10.0.0.1")]
    [InlineData("64:ff9b::169.254.169.254")]
    [InlineData("64:ff9b:1::a00:1")]         // NAT64 local-use prefix
    [InlineData("2002:7f00:1::1")]           // 6to4 of 127.0.0.1
    [InlineData("2002:a00:1::1")]            // 6to4 of 10.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]        // 6to4 of 169.254.169.254
    public void An_IPv6_form_embedding_an_internal_IPv4_address_is_rejected(string ip)
    {
        GeneratedClipUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().BeTrue();

        Action literal = () => GeneratedClipUrlGuard.EnsureAllowed($"https://[{ip}]/clip.mp4");
        literal.Should().Throw<InvalidOperationException>();

        Action resolved = () => GeneratedClipUrlGuard.RequirePublic([IPAddress.Parse(ip)]);
        resolved.Should().Throw<NonPublicAddressException>();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]           // just below 172.16/12
    [InlineData("172.32.0.1")]               // just above 172.16/12
    [InlineData("100.63.255.255")]           // just below 100.64/10
    [InlineData("100.128.0.1")]              // just above 100.64/10
    [InlineData("198.17.255.255")]           // just below 198.18/15
    [InlineData("198.20.0.1")]               // just above 198.18/15
    [InlineData("223.255.255.254")]          // just below 224/4
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001::")]
    [InlineData("2001:4860:4860::8888")]     // 2001:4860:: is not Teredo (2001::/32)
    [InlineData("2001:30::1")]               // just above ORCHIDv2
    [InlineData("3fff:1000::1")]             // just above 3fff::/20
    public void A_public_address_is_allowed(string ip)
    {
        GeneratedClipUrlGuard.IsPrivate(IPAddress.Parse(ip)).Should().BeFalse();
        GeneratedClipUrlGuard.RequirePublic([IPAddress.Parse(ip)]).Should().ContainSingle();

        string host = ip.Contains(':') ? $"[{ip}]" : ip;
        Action literal = () => GeneratedClipUrlGuard.EnsureAllowed($"https://{host}/clip.mp4");
        literal.Should().NotThrow();
    }

    [Fact]
    public async Task Both_connect_callbacks_refuse_an_IPv4_compatible_loopback_literal_before_dialling()
    {
        // ::127.0.0.1 is not routable, so even a broken guard cannot reach a real host; a correct one refuses it
        // as a NonPublicAddressException rather than a socket failure.
        const string url = "https://[::127.0.0.1]:1/clip.mp4";

        using HttpClient download = GeneratedClipUrlGuard.CreateDownloadClient(TimeSpan.FromSeconds(5));
        (await CaptureAsync(() => download.GetAsync(url))).Should().BeTrue("the generated-clip client checks the dialled IP");

        using var user = new HttpClient(GeneratedClipUrlGuard.CreateUserUrlHandler()) { Timeout = TimeSpan.FromSeconds(5) };
        (await CaptureAsync(() => user.GetAsync(url))).Should().BeTrue("the URL-import client checks the dialled IP");
    }

    private static async Task<bool> CaptureAsync(Func<Task> send)
    {
        try
        {
            await send();
            return false;
        }
        catch (Exception e)
        {
            for (Exception? x = e; x is not null; x = x.InnerException)
                if (x is NonPublicAddressException) return true;
            return false;
        }
    }
}
