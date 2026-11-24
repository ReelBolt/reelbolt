#nullable enable

using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using System;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// T-037: <see cref="SelfHostedEndpoint.IsLocal"/> decides whether a zero-priced self-hosted default may stay at zero. It is
/// pure (no DNS): a name is local only by its shape (localhost, a single label, host.docker.internal) and an IP literal by
/// <c>GeneratedClipUrlGuard.IsPrivate</c>. Anything else, including a public hostname, is hosted.
/// </summary>
public sealed class SelfHostedEndpointTests
{
    [Theory]
    [InlineData("http://whisper:8000/v1")]
    [InlineData("http://embeddings:80/v1")]
    [InlineData("http://127.0.0.1:9003/v1")]
    [InlineData("http://10.0.0.5/v1")]
    [InlineData("http://[fd00::1]/v1")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.5]/")]
    [InlineData("http://decision:8791")]
    [InlineData("http://host.docker.internal:9002/v1")]
    [InlineData("http://u:p@WHISPER:8000/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://gpu.localhost:8000/v1")]
    public void A_local_endpoint_is_local(string endpoint) =>
        SelfHostedEndpoint.IsLocal(endpoint).Should().BeTrue();

    [Theory]
    [InlineData("https://api.deepinfra.com/v1/openai")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://gateway.ai.cloudflare.com/v1/a/g/openai")]
    [InlineData("https://api.together.xyz/v1")]
    [InlineData("http://whisper.internal.example.com/v1")]
    [InlineData("http://whisper./v1")]
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://[2606:4700::1]/")]
    [InlineData("http://u@api.openai.com/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("ftp://whisper/")]
    [InlineData("whisper:8000")]
    [InlineData("https://api。openai。com/v1")]
    [InlineData("https://api．openai．com/v1")]
    [InlineData("https://api｡openai｡com/v1")]
    [InlineData("https://xn--wgv71a119e.example/v1")]
    [InlineData("http://whisperé/")]
    public void A_hosted_or_unusable_endpoint_is_not_local(string? endpoint) =>
        SelfHostedEndpoint.IsLocal(endpoint).Should().BeFalse();

    /// <summary>
    /// T-015: "local" is its own narrow set (loopback, RFC1918, CGNAT, link-local, ULA), not everything the SSRF guard
    /// refuses: documentation, benchmarking, Teredo, 6to4, NAT64, multicast and unspecified literals are not run by this
    /// deployment, so they are never priced at zero.
    /// </summary>
    [Theory]
    [InlineData("http://[2001:db8::1]/v1")]
    [InlineData("http://192.0.2.1/v1")]
    [InlineData("http://198.51.100.7/v1")]
    [InlineData("http://203.0.113.9/v1")]
    [InlineData("http://198.18.0.1/v1")]
    [InlineData("http://[2001::1]/v1")]
    [InlineData("http://[2002:a00:5::1]/v1")]
    [InlineData("http://[64:ff9b::a00:5]/v1")]
    [InlineData("http://[3fff::1]/v1")]
    [InlineData("http://0.0.0.0/v1")]
    [InlineData("http://224.0.0.1/v1")]
    [InlineData("http://255.255.255.255/v1")]
    [InlineData("http://[::]/v1")]
    [InlineData("http://[ff02::1]/v1")]
    public void A_special_purpose_literal_that_is_not_run_here_is_not_local(string endpoint) =>
        SelfHostedEndpoint.IsLocal(endpoint).Should().BeFalse();

    [Theory]
    [InlineData("http://172.16.0.1/v1")]
    [InlineData("http://172.31.255.254/v1")]
    [InlineData("http://192.168.1.10/v1")]
    [InlineData("http://100.64.0.1/v1")]
    [InlineData("http://100.127.255.254/v1")]
    [InlineData("http://169.254.1.1/v1")]
    [InlineData("http://[fe80::1]/v1")]
    [InlineData("http://[fc00::1]/v1")]
    [InlineData("http://[::ffff:192.168.1.10]/v1")]
    public void A_private_or_loopback_literal_is_local(string endpoint) =>
        SelfHostedEndpoint.IsLocal(endpoint).Should().BeTrue();

    [Theory]
    [InlineData("http://whisper:8000/v1")]
    [InlineData("http://embeddings:80/v1")]
    [InlineData("http://127.0.0.1:9003/v1")]
    [InlineData("http://[fd00::1]/v1")]
    [InlineData("http://localhost/v1")]
    public void An_ascii_local_endpoint_stays_local(string endpoint) =>
        SelfHostedEndpoint.IsLocal(endpoint).Should().BeTrue();

    /// <summary>SEC-19: a host whose IDN form cannot be read (Uri.IdnHost throws) is not local, and judging it never throws.</summary>
    [Theory]
    [InlineData("http://api﹒openai﹒com/v1")]
    [InlineData("http://api․openai․com/v1")]
    [InlineData("http://⒈example.com/v1")]
    [InlineData("http://api﹒openai․com⒈/v1")]
    public void A_host_whose_idn_form_cannot_be_read_is_not_local_and_does_not_throw(string endpoint)
    {
        Action judge = () => SelfHostedEndpoint.IsLocal(endpoint);

        judge.Should().NotThrow();
        SelfHostedEndpoint.IsLocal(endpoint).Should().BeFalse();
    }
}
