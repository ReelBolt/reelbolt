#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// S-50 / S-57: a hostile queue (a BYO fal row at an attacker host, or a compromised answer) hands back
/// status_url / response_url values pointing at internal addresses or other hosts. FalQueueClient, which the
/// audio, video and image arms share, must never send a request to any of them, and must not echo the body of
/// whatever answered into the tenant-visible error. No live network: every answer comes from a stub handler.
/// </summary>
public class FalQueueOriginPinTests
{
    private const string Base = "https://queue.fal.run";
    private const string InternalSecret = "AKIA-INTERNAL-METADATA-SECRET";

    private sealed class HostileHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Hostile tracking URLs: internal literals, a foreign host, a downgrade, another port, credentials.</summary>
    public static TheoryData<string, string> HostileUrls => new()
    {
        { "https://169.254.169.254/latest/meta-data/iam", "https://queue.fal.run/m/requests/r" },
        { "https://queue.fal.run/m/requests/r/status", "http://10.0.0.5/admin" },
        { "https://attacker.example/s", "https://attacker.example/r" },
        { "http://queue.fal.run/m/requests/r/status", "https://queue.fal.run/m/requests/r" },
        { "https://queue.fal.run:8443/m/requests/r/status", "https://queue.fal.run/m/requests/r" },
        { "https://user:pw@queue.fal.run/m/requests/r/status", "https://queue.fal.run/m/requests/r" },
        { "https://[::1]/s", "https://queue.fal.run/m/requests/r" },
        { "https://queue.fal.run.attacker.example/s", "https://queue.fal.run/m/requests/r" },
    };

    /// <summary>
    /// A queue that submits with the given tracking URLs; anything that is not the submit answers with an
    /// internal secret, so an echo of a followed URL's body is detectable.
    /// </summary>
    private static HostileHandler Hostile(string statusUrl, string responseUrl, string completedBody = "{\"status\":\"COMPLETED\"}") =>
        new(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json("{\"request_id\":\"r\",\"status_url\":\"" + statusUrl + "\",\"response_url\":\"" + responseUrl + "\"}");
            }

            string url = request.RequestUri!.ToString();
            if (url == statusUrl && request.RequestUri.Host == "queue.fal.run" && request.RequestUri.Scheme == "https"
                && request.RequestUri.IsDefaultPort && string.IsNullOrEmpty(request.RequestUri.UserInfo))
            {
                return Json(completedBody);
            }

            return Json("{\"leak\":\"" + InternalSecret + "\"}", HttpStatusCode.InternalServerError);
        });

    private static void OnlyTheSubmitAndPinnedUrlsWereRequested(HostileHandler api, params string[] allowed)
    {
        foreach (HttpRequestMessage request in api.Requests)
        {
            Uri uri = request.RequestUri!;
            if (request.Method == HttpMethod.Post) continue;
            allowed.Should().Contain(uri.ToString(), "a vendor-supplied URL outside the configured queue origin must never be requested");
        }
    }

    private static bool Pinned(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" && uri.Host == "queue.fal.run"
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    // ================================================================ audio (S-50)

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public async Task Audio_never_follows_a_tracking_url_outside_the_queue_origin(string statusUrl, string responseUrl)
    {
        HostileHandler api = Hostile(statusUrl, responseUrl);
        FalAiAudioGenerationClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(Base) }, "fal-key",
            downloadClient: new HttpClient(api), pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        Func<Task> act = () => client.GenerateAsync(
            new AudioGenerationRequest(AudioGenerationKind.Music, "calm piano", 30, Model: "fal-ai/elevenlabs/music",
                Instrumental: true, OutputFormat: "mp3_44100_128"),
            CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Message.Should().NotContain(InternalSecret);
        ex.Message.Should().NotContain("169.254").And.NotContain("10.0.0.5").And.NotContain("attacker");
        OnlyTheSubmitAndPinnedUrlsWereRequested(api, [.. new[] { statusUrl, responseUrl }.Where(Pinned)]);
    }

    [Fact]
    public async Task Audio_does_not_echo_a_failed_result_body_into_the_tenant_error()
    {
        HostileHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("{\"request_id\":\"r\",\"status_url\":\"https://queue.fal.run/m/requests/r/status\",\"response_url\":\"https://queue.fal.run/m/requests/r\"}")
            : request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
                ? Json("{\"status\":\"COMPLETED\"}")
                : Json("{\"leak\":\"" + InternalSecret + "\"}", HttpStatusCode.InternalServerError));
        FalAiAudioGenerationClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(Base) }, "fal-key",
            downloadClient: new HttpClient(api), pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        Func<Task> act = () => client.GenerateAsync(
            new AudioGenerationRequest(AudioGenerationKind.Music, "calm piano", 30, Model: "fal-ai/elevenlabs/music",
                Instrumental: true, OutputFormat: "mp3_44100_128"),
            CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Message.Should().Contain("500").And.NotContain(InternalSecret);
    }

    [Fact]
    public async Task A_row_endpoint_on_an_internal_literal_is_refused_even_when_the_urls_are_same_origin()
    {
        const string internalBase = "https://10.1.2.3";
        HostileHandler api = new(request => request.Method == HttpMethod.Post
            ? Json("{\"request_id\":\"r\",\"status_url\":\"https://10.1.2.3/m/requests/r/status\",\"response_url\":\"https://10.1.2.3/m/requests/r\"}")
            : Json("{\"leak\":\"" + InternalSecret + "\"}"));
        FalAiAudioGenerationClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(internalBase) }, "fal-key",
            downloadClient: new HttpClient(api), pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        Func<Task> act = () => client.GenerateAsync(
            new AudioGenerationRequest(AudioGenerationKind.Music, "calm piano", 30, Model: "fal-ai/elevenlabs/music",
                Instrumental: true, OutputFormat: "mp3_44100_128"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Message.Should().NotContain(InternalSecret);
        api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Post, "no tracking URL on a non-public host is followed");
    }

    // ================================================================ video (S-57)

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public async Task Video_never_follows_a_tracking_url_outside_the_queue_origin(string statusUrl, string responseUrl)
    {
        HostileHandler api = Hostile(statusUrl, responseUrl);
        FalAiVideoGenerationClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(Base) }, "fal-key",
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        Func<Task> act = async () =>
        {
            ExternalJobHandle handle = await client.SubmitAsync(
                new VideoGenerationRequest("fal-ai/vidu/q4/image-to-video", "a slow pan", 5, "720p",
                    FalVideoCatalog.NativeToken, [new VideoGenerationImageInput("image/png", [0x89, 0x50, 0x4E, 0x47])]),
                CancellationToken.None);
            ExternalJobStatus status = await client.PollAsync(handle, CancellationToken.None);
            status.State.Should().NotBe(ExternalJobState.Succeeded);
        };

        Exception ex = (await act.Should().ThrowAsync<Exception>()).Which;
        ex.Message.Should().NotContain(InternalSecret);
        OnlyTheSubmitAndPinnedUrlsWereRequested(api, [.. new[] { statusUrl, responseUrl }.Where(Pinned)]);
    }

    [Fact]
    public async Task Video_refuses_a_persisted_handle_whose_urls_leave_the_queue_origin()
    {
        HostileHandler api = new(_ => Json("{\"leak\":\"" + InternalSecret + "\"}"));
        FalAiVideoGenerationClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(Base) }, "fal-key",
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        Func<Task> act = () => client.PollAsync(
            new ExternalJobHandle("r|https://169.254.169.254/s|https://169.254.169.254/r"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain(InternalSecret);
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ image (S-57)

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public async Task Image_never_follows_a_tracking_url_outside_the_queue_origin(string statusUrl, string responseUrl)
    {
        HostileHandler api = Hostile(statusUrl, responseUrl);
        FalAiImageClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(Base) }, "fal-key", new HttpClient(api),
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);

        Func<Task> act = () => client.GenerateAsync(new ImageGenerationRequest("ideogram/v4.5", "BIG TEXT"), CancellationToken.None);

        Exception ex = (await act.Should().ThrowAsync<Exception>()).Which;
        ex.Message.Should().NotContain(InternalSecret);
        OnlyTheSubmitAndPinnedUrlsWereRequested(api, [.. new[] { statusUrl, responseUrl }.Where(Pinned)]);
    }

    [Theory]
    [MemberData(nameof(HostileUrls))]
    public async Task The_image_probe_never_follows_a_tracking_url_outside_the_queue_origin(string statusUrl, string responseUrl)
    {
        HostileHandler api = Hostile(statusUrl, responseUrl);
        FalAiImageClient client = new(
            new HttpClient(api) { BaseAddress = new Uri(Base) }, "fal-key", new HttpClient(api),
            pollInterval: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask, probeModel: "ideogram/v4.5");

        ImageProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.IsOk.Should().BeFalse();
        (result.Reason ?? string.Empty).Should().NotContain(InternalSecret);
        OnlyTheSubmitAndPinnedUrlsWereRequested(api, [.. new[] { statusUrl, responseUrl }.Where(Pinned)]);
    }
}
