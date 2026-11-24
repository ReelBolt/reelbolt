using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class GeneratedClipDownloadHardeningTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        {
            Calls++;
            return Task.FromResult(f(r));
        }
    }

    private static (MiniMaxVideoGenerationClient Client, Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> f)
    {
        var h = new Handler(f);
        return (new MiniMaxVideoGenerationClient(new HttpClient(h) { BaseAddress = new Uri("https://api.minimax.io") }, "k"), h);
    }

    [Theory]
    [InlineData("http://cdn.example.com/a.mp4")]
    [InlineData("https://127.0.0.1/a.mp4")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.0.0.5/a.mp4")]
    [InlineData("https://[::1]/a.mp4")]
    [InlineData("https://localhost/a.mp4")]
    [InlineData("https://user:pw@cdn.example.com/a.mp4")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task Unsafe_download_urls_are_refused_without_any_request(string url)
    {
        var (client, handler) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await client.Invoking(c => c.DownloadAsync(url, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Html_content_type_is_refused()
    {
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html/>", System.Text.Encoding.UTF8, "text/html")
        });
        await client.Invoking(c => c.DownloadAsync("https://cdn.example.com/a.mp4", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Declared_oversize_is_refused()
    {
        var (client, _) = Make(_ =>
        {
            var content = new ByteArrayContent(new byte[1]);
            content.Headers.ContentType = new("video/mp4");
            content.Headers.ContentLength = MiniMaxVideoGenerationClient.MaxDownloadBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await client.Invoking(c => c.DownloadAsync("https://cdn.example.com/a.mp4", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Video_response_streams_through()
    {
        var (client, _) = Make(_ =>
        {
            var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
            content.Headers.ContentType = new("video/mp4");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        await using var s = await client.DownloadAsync("https://cdn.example.com/a.mp4", CancellationToken.None);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        ms.Length.Should().Be(3);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_addresses_are_rejected_by_the_connect_filter(string ip)
    {
        System.Net.IPAddress[] one = [System.Net.IPAddress.Parse(ip)];
        Action act = () => GeneratedClipUrlGuard.RequirePublic(one);
        act.Should().Throw<HttpRequestException>();
    }

    [Fact]
    public void Mixed_resolution_with_any_private_address_is_rejected_and_public_passes()
    {
        var pub = System.Net.IPAddress.Parse("93.184.216.34");
        GeneratedClipUrlGuard.RequirePublic([pub]).Should().ContainSingle();
        Action act = () => GeneratedClipUrlGuard.RequirePublic([pub, System.Net.IPAddress.Parse("10.0.0.1")]);
        act.Should().Throw<HttpRequestException>();
    }

    [Fact]
    public async Task Download_client_refuses_to_dial_loopback_at_connect_time()
    {
        using var client = GeneratedClipUrlGuard.CreateDownloadClient(TimeSpan.FromSeconds(5));
        // Bypasses EnsureAllowed on purpose: only the ConnectCallback stands in the way.
        await client.Invoking(c => c.GetAsync("https://127.0.0.1:1/x.mp4")).Should().ThrowAsync<HttpRequestException>();
    }
}
