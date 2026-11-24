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
/// fal.ai hosted voice isolation replayed against hand-written fixtures (no live call: the fal key does not exist yet).
/// The submit and status documents are the queue's own recorded shape; the result documents follow the OpenAPI output
/// schema (<c>audio.url</c>) read 2026-11-02 and are UNVERIFIED against a real run.
/// </summary>
public class FalAiAudioIsolationClientTests
{
    private const string Model = "fal-ai/elevenlabs/audio-isolation";
    private const string Source = "https://storage.example.test/source/interview.wav?X-Amz-Signature=abc";
    private const string ResultUrl = "https://v3.fal.media/files/zebra/isolated_voice.mp3";

    private static AudioStubHandler Queue(
        string result = "fal-isolation-result.json", HttpStatusCode resultStatus = HttpStatusCode.OK,
        string? completed = null, HttpStatusCode submitStatus = HttpStatusCode.OK, string submitBody = "",
        Func<HttpRequestMessage, HttpResponseMessage?>? cdn = null)
    {
        Queue<string> statuses = new(["fal-music-status-in-queue.json", "fal-music-status-in-progress.json"]);
        return new AudioStubHandler(request =>
        {
            if (request.RequestUri!.Host != "queue.fal.run")
            {
                return cdn?.Invoke(request) ?? AudioFixtures.Audio(AudioFixtures.Silence);
            }

            if (request.Method == HttpMethod.Post)
            {
                return submitStatus == HttpStatusCode.OK
                    ? AudioFixtures.Json(AudioFixtures.Text("fal-music-submit.json"))
                    : AudioFixtures.Json(submitBody, submitStatus);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                return AudioFixtures.Json(statuses.Count > 0
                    ? AudioFixtures.Text(statuses.Dequeue())
                    : completed ?? AudioFixtures.Text("fal-music-status-completed.json"));
            }

            return AudioFixtures.Json(AudioFixtures.Text(result), resultStatus);
        });
    }

    private static FalAiAudioIsolationClient Client(AudioStubHandler handler, TimeSpan? pollTimeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run") }, AudioFixtures.Key,
            downloadClient: new HttpClient(handler), pollInterval: TimeSpan.Zero, pollTimeout: pollTimeout,
            delay: (_, _) => Task.CompletedTask);

    private const string Echo = "https://storage.example.test/source/interview.wav?X-Amz-Signature=abc";

    private static void NoLeak(AudioIsolationException ex)
    {
        ex.Message.Should().NotContain("X-Amz").And.NotContain("storage.example.test").And.NotContain("interview.wav");
    }

    private static AudioIsolationRequest Request(string? source = Source, double seconds = 95, string model = "", bool? background = null) =>
        new(source!, seconds, model, background);

    [Fact]
    public async Task Happy_path_sends_the_recorded_body_walks_the_queue_and_downloads_without_the_key()
    {
        AudioStubHandler api = Queue();
        string? submitted = null;

        AudioIsolationResult result = await Client(api).IsolateAsync(Request(), CancellationToken.None, id =>
        {
            submitted = id;
            return Task.CompletedTask;
        });

        HttpRequestMessage submit = api.Requests[0];
        submit.RequestUri!.ToString().Should().Be("https://queue.fal.run/" + Model);
        submit.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key test");
        AudioFixtures.SameJson(api.Bodies[0], "fal-isolation-request.json").Should().BeTrue(api.Bodies[0]);

        // IN_QUEUE, IN_PROGRESS, COMPLETED
        api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)).Should().Be(3);
        api.Requests.Single(r => r.RequestUri!.ToString() == ResultUrl).Headers.Contains("Authorization")
            .Should().BeFalse("the key must never reach a CDN host");

        result.VoiceStem.Should().Equal(AudioFixtures.Silence);
        result.MimeType.Should().Be("audio/mpeg");
        result.Extension.Should().Be(".mp3");
        result.BackgroundStem.Should().BeNull();
        result.BilledSeconds.Should().Be(95);
        result.Model.Should().Be(Model);
        result.VendorRequestId.Should().Be("764cabcf-b745-4b3e-ae38-1200304cf45b");
        submitted.Should().Be(result.VendorRequestId);
        result.RawMetadata.Should().NotContain("http", "no URL is kept in the provenance metadata");
        result.ToString().Should().NotContain("storage.example.test");
    }

    [Fact]
    public async Task A_background_stem_is_refused_before_anything_is_sent_when_the_endpoint_has_none()
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Client(api).IsolateAsync(Request(background: true), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://x.test/a.wav")]
    [InlineData("http://x.test/a.wav")]
    [InlineData("https://user:pw@x.test/a.wav")]
    public async Task A_bad_source_url_is_refused_before_anything_is_sent(string source)
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Client(api).IsolateAsync(Request(source), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public async Task A_non_positive_duration_is_refused(double seconds)
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Client(api).IsolateAsync(Request(seconds: seconds), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_uncatalogued_model_is_refused_before_anything_is_sent()
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Client(api).IsolateAsync(Request(model: "fal-ai/elevenlabs/music"), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_REQUEST_INVALID");
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_402_at_submit_is_a_quota_failure()
    {
        AudioStubHandler api = Queue(submitStatus: HttpStatusCode.PaymentRequired, submitBody: "{\"detail\":\"Exhausted balance\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_VENDOR_QUOTA");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_5xx_or_429_at_submit_is_retryable(HttpStatusCode status)
    {
        AudioStubHandler api = Queue(submitStatus: status, submitBody: "{\"detail\":\"try later\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_VENDOR_RETRYABLE");
        ex.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_422_at_submit_is_a_bad_request_and_never_echoes_the_source_url()
    {
        AudioStubHandler api = Queue(submitStatus: HttpStatusCode.UnprocessableEntity, submitBody: "{\"detail\":\"cannot fetch " + Echo + " now\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_REQUEST_INVALID");
        NoLeak(ex);
    }

    [Fact]
    public async Task A_submit_refusal_echoing_a_scheme_less_signed_token_is_masked()
    {
        AudioStubHandler api = Queue(submitStatus: HttpStatusCode.BadRequest,
            submitBody: "{\"detail\":\"bad storage.example.test/source/interview.wav?X-Amz-Signature=abc\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        NoLeak((await act.Should().ThrowAsync<AudioIsolationException>()).Which);
    }

    [Fact]
    public async Task A_result_refusal_echoing_the_presigned_url_never_carries_it()
    {
        AudioStubHandler api = Queue(result: "fal-isolation-result-refusal.json", resultStatus: HttpStatusCode.UnprocessableEntity);

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_REQUEST_INVALID");
        NoLeak(ex);
    }

    [Fact]
    public async Task A_failed_status_echoing_the_presigned_url_never_carries_it()
    {
        AudioStubHandler api = Queue(completed: "{\"status\":\"COMPLETED\",\"error\":\"download of " + Echo + " failed\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_VENDOR_FAILED");
        NoLeak(ex);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_401_403_404_at_submit_is_a_provider_configuration_error(HttpStatusCode status)
    {
        AudioStubHandler api = Queue(submitStatus: status, submitBody: "{\"detail\":\"nope\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_PROVIDER_CONFIG");
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "AUDIO_VENDOR_QUOTA")]
    [InlineData(HttpStatusCode.TooManyRequests, "AUDIO_VENDOR_RETRYABLE")]
    [InlineData(HttpStatusCode.InternalServerError, "AUDIO_VENDOR_RETRYABLE")]
    [InlineData(HttpStatusCode.Forbidden, "AUDIO_VENDOR_FAILED")]
    public async Task The_result_route_classifies_402_429_5xx_and_other_statuses(HttpStatusCode status, string code)
    {
        AudioStubHandler api = Queue(resultStatus: status);

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be(code);
    }

    [Fact]
    public async Task Exhausting_the_poll_budget_is_a_vendor_failure_that_is_not_retried()
    {
        AudioStubHandler api = Queue(completed: "{\"status\":\"IN_PROGRESS\"}");

        Func<Task> act = () => Client(api, pollTimeout: TimeSpan.Zero).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_VENDOR_FAILED");
        ex.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_422_on_the_result_route_is_a_bad_request()
    {
        AudioStubHandler api = Queue(result: "fal-isolation-result.json", resultStatus: HttpStatusCode.UnprocessableEntity);

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_REQUEST_INVALID");
    }

    [Fact]
    public async Task A_completed_job_carrying_an_error_is_a_vendor_failure_and_downloads_nothing()
    {
        AudioStubHandler api = Queue(completed: """{"status":"COMPLETED","error":"worker crashed"}""");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_VENDOR_FAILED");
        api.Requests.Should().NotContain(r => r.RequestUri!.Host != "queue.fal.run");
    }

    [Fact]
    public async Task A_result_with_no_audio_url_is_a_vendor_failure_not_a_success()
    {
        AudioStubHandler api = Queue(result: "fal-isolation-result-no-audio.json");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_VENDOR_FAILED");
        ex.Message.Should().Contain("audio.url");
    }

    [Fact]
    public async Task A_result_url_on_a_private_address_is_refused_and_nothing_is_fetched()
    {
        AudioStubHandler api = Queue(result: "fal-isolation-result-private-url.json");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        AudioIsolationException ex = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        ex.Code.Should().Be("AUDIO_VENDOR_FAILED");
        api.Requests.Should().NotContain(r => r.RequestUri!.Host == "10.0.0.5", "the SSRF guard refuses before any connection");
    }

    [Fact]
    public async Task A_download_that_is_not_audio_is_a_vendor_failure()
    {
        AudioStubHandler api = Queue(cdn: _ => AudioFixtures.Json("<html>"));

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioIsolationException>()).Which.Code.Should().Be("AUDIO_VENDOR_FAILED");
    }

    [Fact]
    public async Task A_blank_key_throws_when_the_client_is_built()
    {
        Action build = () => _ = new FalAiAudioIsolationClient(new HttpClient(), " ");

        build.Should().Throw<ArgumentException>();
        await Task.CompletedTask;
    }

    [Fact]
    public void Describe_answers_the_default_for_blank_and_refuses_an_unknown_model()
    {
        FalAiAudioIsolationClient client = new(new HttpClient(), "k");

        client.Describe("").Id.Should().Be(Model);
        client.Describe(Model).UsdPerMinute.Should().Be(0.10m);
        Action act = () => client.Describe("fal-ai/demucs");
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void The_factory_serves_fal_only_and_caches_per_row()
    {
        AudioIsolationClientFactory factory = new();
        ResolvedAudioGenerationProvider fal = new(Guid.NewGuid(), "fal", ReelBolt.Shared.Data.Models.InferenceProviderKind.FalAi,
            ReelBolt.Shared.Data.Models.InferenceProviderCapability.AudioIsolation, "", Model, "k", 60);

        factory.Get(fal).Should().BeSameAs(factory.Get(fal));

        Action eleven = () => factory.Get(new ResolvedAudioGenerationProvider(Guid.NewGuid(), "eleven", ReelBolt.Shared.Data.Models.InferenceProviderKind.ElevenLabs,
            ReelBolt.Shared.Data.Models.InferenceProviderCapability.AudioIsolation, "", Model, "k", 60));
        eleven.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public async Task Metadata_with_arrays_nested_objects_urls_and_signed_tokens_is_masked_and_never_throws()
    {
        AudioStubHandler api = Queue(result: "fal-isolation-result-rich.json");

        AudioIsolationResult result = await Client(api).IsolateAsync(Request(), CancellationToken.None);

        result.VoiceStem.Should().NotBeEmpty("the job succeeded; metadata must not fail it");
        result.RawMetadata.Should().NotBeNull();
        result.RawMetadata.Should().Contain("plain").And.Contain("\"ok\"").And.Contain("\\u003Curl\\u003E")
            .And.NotContain("X-Amz").And.NotContain("storage.example.test").And.NotContain("x.test").And.NotContain("https");
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task No_exception_in_the_chain_carries_the_presigned_url_or_host(HttpStatusCode status)
    {
        AudioStubHandler api = Queue(submitStatus: status, submitBody:
            "{\"detail\":\"cannot fetch " + Echo + " and https:\\/\\/storage.example.test\\/source?X-Amz-Signature=abc\"}");

        Func<Task> act = () => Client(api).IsolateAsync(Request(), CancellationToken.None);

        Exception top = (await act.Should().ThrowAsync<AudioIsolationException>()).Which;
        for (Exception? e = top; e is not null; e = e.InnerException)
        {
            e.Message.Should().NotContain("X-Amz").And.NotContain("storage.example.test");
            e.ToString().Should().NotContain("X-Amz").And.NotContain("storage.example.test").And.NotContain("interview.wav");
        }
    }
}
