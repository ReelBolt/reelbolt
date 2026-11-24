using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Csam;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The production <see cref="ArachnidShieldClient"/> over a real <see cref="HttpMessageHandler"/>:
/// paths, body shapes, auth, response mapping and the one-host rule are all pinned offline.
/// </summary>
public class ArachnidShieldClientTests
{
    [Fact]
    public async Task ScanPdqAsync_PostsTheBase64Hash_AndMapsTheResult()
    {
        CsamFakeServer server = new()
        {
            PdqClassification = "harmful-abusive-material",
            PdqMatchType = "near",
            PdqNearMatches = 3
        };
        CsamRecordingHandler handler = new(server);
        CsamOptions options = CsamTestSupport.EnabledOptions();
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));
        string hash = Convert.ToBase64String(new byte[32]);

        ArachnidScanResult result = await client.ScanPdqAsync(hash, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/pdq");
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Basic");
        handler.Requests[0].Headers.Authorization.Parameter.Should().Be(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                $"{CsamTestSupport.Username}:{CsamTestSupport.Password}")));

        using JsonDocument body = JsonDocument.Parse(handler.Bodies[0]);
        body.RootElement.GetProperty("hashes")[0].GetString().Should().Be(hash);

        result.Classification.Should().Be("harmful-abusive-material");
        result.MatchType.Should().Be("near");
        result.NearMatchCount.Should().Be(3);
    }

    [Fact]
    public async Task ScanMediaAsync_PostsRawBytesWithTheirContentType_AndReadsTheSha()
    {
        CsamFakeServer server = new() { MediaClassification = "csam", MediaMatchType = "exact", MediaSha256Hex = "ffee" };
        CsamRecordingHandler handler = new(server);
        CsamOptions options = CsamTestSupport.EnabledOptions();
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));
        byte[] bytes = [1, 2, 3, 4, 5];

        await using MemoryStream stream = new(bytes);
        ArachnidScanResult result = await client.ScanMediaAsync(stream, "video/mp4", CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/media");
        handler.Requests[0].Content!.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        handler.Bodies[0].Should().Equal(bytes);

        result.Classification.Should().Be("csam");
        result.MatchType.Should().Be("exact");
        result.Sha256Hex.Should().Be("ffee");
    }

    [Fact]
    public async Task GetDatabaseVersionAsync_ReadsLastUpdatedAt()
    {
        CsamRecordingHandler handler = new();
        CsamOptions options = CsamTestSupport.EnabledOptions();
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));

        string? version = await client.GetDatabaseVersionAsync(CancellationToken.None);

        version.Should().Be("2026-10-01T00:00:00Z");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/version");
    }

    [Fact]
    public async Task EveryRequest_UsesOnlyTheConfiguredBaseAddress()
    {
        CsamRecordingHandler handler = new();
        CsamOptions options = CsamTestSupport.EnabledOptions();
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));

        await client.ScanPdqAsync(Convert.ToBase64String(new byte[32]), CancellationToken.None);
        await using (MemoryStream stream = new([1, 2, 3]))
            await client.ScanMediaAsync(stream, "image/jpeg", CancellationToken.None);
        await client.GetDatabaseVersionAsync(CancellationToken.None);

        handler.Requests.Should().HaveCount(3);
        handler.Requests.Select(r => r.RequestUri!.Host).Distinct()
            .Should().Equal(new[] { "shield.test" }, "the configured Arachnid base URL is the only host");
    }

    [Fact]
    public async Task WithoutCredentials_NoAuthorizationHeaderIsSent()
    {
        CsamRecordingHandler handler = new();
        CsamOptions options = CsamTestSupport.EnabledOptions(o =>
        {
            o.Username = null;
            o.Password = null;
        });
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));

        await client.ScanPdqAsync(Convert.ToBase64String(new byte[32]), CancellationToken.None);

        handler.Requests[0].Headers.Authorization.Should().BeNull();
        handler.Requests[0].Headers.Contains("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task ANonSuccessStatus_ThrowsHttpRequestExceptionWithTheStatus()
    {
        CsamFakeServer server = new() { Override = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        CsamRecordingHandler handler = new(server);
        CsamOptions options = CsamTestSupport.EnabledOptions();
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));

        Func<Task> act = () => client.ScanPdqAsync(Convert.ToBase64String(new byte[32]), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task AMalformedBody_ThrowsArachnidProtocolException()
    {
        CsamFakeServer server = new() { Override = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") } };
        CsamRecordingHandler handler = new(server);
        CsamOptions options = CsamTestSupport.EnabledOptions();
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Microsoft.Extensions.Options.Options.Create(options));

        Func<Task> act = () => client.ScanPdqAsync(Convert.ToBase64String(new byte[32]), CancellationToken.None);

        await act.Should().ThrowAsync<ArachnidProtocolException>();
    }
}
