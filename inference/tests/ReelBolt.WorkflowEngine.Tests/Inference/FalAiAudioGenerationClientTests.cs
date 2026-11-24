#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// fal.ai's audio arm (ElevenLabs Music, ElevenLabs SFX v2, MMAudio) replayed against the recorded mocks of
/// research/audio-vendors.md C1-C3. Each test pins an exact body, the queue lifecycle, a refusal that must
/// happen BEFORE anything is sent, or the classification of a vendor failure.
/// </summary>
public class FalAiAudioGenerationClientTests
{
    private const string Music = "fal-ai/elevenlabs/music";
    private const string SfxV2 = "fal-ai/elevenlabs/sound-effects/v2";
    private const string MMAudio = "fal-ai/mmaudio-v2/text-to-audio";

    private const string MusicResultUrl = "https://v3.fal.media/files/lion/abc123_music_generated.mp3";

    /// <summary>
    /// The queue as the fixtures record it: the submit answer, the status walk IN_QUEUE, IN_PROGRESS,
    /// COMPLETED, the result document, and the CDN answering the silent MP3.
    /// </summary>
    private static AudioStubHandler Queue(
        string result = "fal-music-result.json", HttpStatusCode resultStatus = HttpStatusCode.OK,
        string? completed = null, Func<HttpRequestMessage, HttpResponseMessage?>? cdn = null)
    {
        Queue<string> statuses = new(["fal-music-status-in-queue.json", "fal-music-status-in-progress.json"]);
        return new AudioStubHandler(request =>
        {
            string host = request.RequestUri!.Host;
            if (host != "queue.fal.run")
            {
                return cdn?.Invoke(request) ?? AudioFixtures.Audio(AudioFixtures.Silence);
            }

            if (request.Method == HttpMethod.Post) return AudioFixtures.Json(AudioFixtures.Text("fal-music-submit.json"));
            if (request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
            {
                return AudioFixtures.Json(statuses.Count > 0
                    ? AudioFixtures.Text(statuses.Dequeue())
                    : completed ?? AudioFixtures.Text("fal-music-status-completed.json"));
            }

            return AudioFixtures.Json(AudioFixtures.Text(result), resultStatus);
        });
    }

    private static FalAiAudioGenerationClient Client(AudioStubHandler handler, string? probeModel = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run") }, AudioFixtures.Key,
            downloadClient: new HttpClient(handler), pollInterval: TimeSpan.Zero,
            delay: (_, _) => Task.CompletedTask, probeModel: probeModel);

    private static AudioGenerationRequest MusicRequest(
        string? prompt = "Warm uplifting corporate pop, light percussion, no vocals", double? duration = 30,
        bool? instrumental = true, string? plan = null, string model = Music, string? format = "mp3_44100_128") =>
        new(AudioGenerationKind.Music, prompt, duration, Model: model, Instrumental: instrumental,
            CompositionPlanJson: plan, OutputFormat: format);

    private static AudioGenerationRequest SfxRequest(
        string prompt = "A short whoosh transition, airy, fast", double? duration = 1.5, bool? loop = false,
        double? influence = 0.3, string model = SfxV2, string? format = "mp3_44100_128") =>
        new(AudioGenerationKind.SoundEffect, prompt, duration, Model: model, Loop: loop, PromptInfluence: influence,
            OutputFormat: format);

    // ================================================================ the recorded happy paths

    [Fact]
    public async Task Music_sends_the_recorded_body_walks_the_queue_and_downloads_without_the_key()
    {
        AudioStubHandler api = Queue();

        AudioGenerationResult result = await Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        HttpRequestMessage submit = api.Requests[0];
        submit.Method.Should().Be(HttpMethod.Post);
        submit.RequestUri!.ToString().Should().Be("https://queue.fal.run/fal-ai/elevenlabs/music");
        submit.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key test");
        AudioFixtures.SameJson(api.Bodies[0], "fal-music-request.json").Should().BeTrue(api.Bodies[0]);

        api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)).Should().Be(3);
        api.Requests.Should().Contain(r => r.RequestUri!.ToString() ==
            "https://queue.fal.run/fal-ai/elevenlabs/requests/764cabcf-b745-4b3e-ae38-1200304cf45b");

        HttpRequestMessage download = api.Requests.Single(r => r.RequestUri!.ToString() == MusicResultUrl);
        download.Headers.Contains("Authorization").Should().BeFalse("the key must never reach a CDN host");

        result.Bytes.Should().Equal(AudioFixtures.Silence);
        result.MimeType.Should().Be("audio/mpeg");
        result.Extension.Should().Be(".mp3");
        result.DurationSec.Should().BeNull("fal returns no duration; the engine probes the file");
        result.VendorRequestId.Should().Be("764cabcf-b745-4b3e-ae38-1200304cf45b");
    }

    [Fact]
    public async Task Sound_effect_v2_sends_the_recorded_body_and_reads_a_url_only_result()
    {
        AudioStubHandler api = Queue(result: "fal-sfx-result.json");

        AudioGenerationResult result = await Client(api).GenerateAsync(SfxRequest(), CancellationToken.None);

        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/fal-ai/elevenlabs/sound-effects/v2");
        AudioFixtures.SameJson(api.PostBodies.Single(), "fal-sfx-request.json").Should().BeTrue(api.PostBodies.Single());
        result.MimeType.Should().Be("audio/mpeg", "a url-only output takes its type from the output_format sent");
        result.Extension.Should().Be(".mp3");
        result.Bytes.Length.Should().Be(AudioFixtures.Silence.Length);
    }

    [Fact]
    public async Task MMAudio_sends_only_prompt_and_duration_and_reads_its_flac_output()
    {
        AudioStubHandler api = Queue(result: "fal-mmaudio-result.json",
            cdn: _ => AudioFixtures.Audio(AudioFixtures.Silence, "application/octet-stream"));

        AudioGenerationResult result = await Client(api).GenerateAsync(
            new AudioGenerationRequest(AudioGenerationKind.SoundEffect, "rain on a tin roof", 8, Model: MMAudio),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(api.PostBodies.Single());
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("prompt", "duration");
        JsonElement fixture = AudioFixtures.Node("fal-mmaudio-request.json").Deserialize<JsonElement>();
        body.RootElement.GetProperty("prompt").GetString().Should().Be(fixture.GetProperty("prompt").GetString());
        body.RootElement.GetProperty("duration").GetDouble().Should().Be(fixture.GetProperty("duration").GetDouble());
        result.Extension.Should().Be(".flac");
        result.MimeType.Should().Be("audio/flac");
    }

    [Fact]
    public async Task A_blank_model_uses_the_default_endpoint_of_the_requested_job()
    {
        AudioStubHandler api = Queue(result: "fal-sfx-result.json");

        await Client(api).GenerateAsync(SfxRequest(model: ""), CancellationToken.None);

        api.Requests[0].RequestUri!.AbsolutePath.Should().Be("/" + SfxV2);
    }

    [Fact]
    public async Task A_sections_composition_plan_is_passed_through_without_the_prompt_only_fields()
    {
        AudioStubHandler api = Queue();
        string plan = AudioFixtures.Text("fal-plan-sections.json");

        await Client(api).GenerateAsync(MusicRequest(prompt: null, duration: null, instrumental: null, plan: plan),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(api.PostBodies.Single());
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("composition_plan", "output_format");
        System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(body.RootElement.GetProperty("composition_plan").GetRawText()),
                AudioFixtures.Node("fal-plan-sections.json"))
            .Should().BeTrue();
    }

    // ================================================================ refusals before anything is sent

    public static IEnumerable<object[]> Refused()
    {
        yield return [MusicRequest(duration: 301), "300"];
        yield return [MusicRequest(duration: 2.9), "3"];
        yield return [MusicRequest(duration: null), "duration"];
        yield return [MusicRequest(prompt: new string('a', 4101)), "4100"];
        yield return [MusicRequest(prompt: null), "prompt"];
        yield return [MusicRequest(plan: AudioFixtures.Text("fal-plan-sections.json")), "composition plan"];
        yield return [MusicRequest(format: "wav"), "wav"];
        yield return [MusicRequest(format: "pcm_44100"), "pcm_44100"];
        yield return [MusicRequest(model: SfxV2), SfxV2];
        yield return [MusicRequest(model: "fal-ai/stable-audio-25/text-to-audio"), "fal-ai/stable-audio-25/text-to-audio"];
        yield return [MusicRequest() with { Loop = true }, "loop"];
        yield return [MusicRequest() with { PromptInfluence = 0.5 }, "prompt influence"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: "{\"positive_global_styles\":[],\"negative_global_styles\":[],\"sections\":[],\"seed\":1}"), "seed"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: AudioFixtures.Text("fal-plan-sections.json").Replace("10000", "2000")), "duration_ms"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: AudioFixtures.Text("elevenlabs-plan-v2.json")), "chunks"];
        yield return [SfxRequest(duration: 23), "22"];
        yield return [SfxRequest(duration: 0.4), "0.5"];
        yield return [SfxRequest(prompt: new string('a', 451)), "450"];
        yield return [SfxRequest(influence: 1.5), "prompt influence"];
        yield return [SfxRequest() with { Instrumental = true }, "instrumental"];
        yield return [SfxRequest() with { CompositionPlanJson = "{}" }, "composition plan"];
        yield return [SfxRequest(prompt: "  "), "prompt"];
        yield return [SfxRequest(model: MMAudio, loop: null, influence: null), "output format"];
        yield return [SfxRequest(model: MMAudio, format: null, influence: null), "loop"];
        yield return [SfxRequest(model: MMAudio, format: null, loop: null, duration: 31), "30"];
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task An_invalid_request_is_refused_before_any_request_is_sent(AudioGenerationRequest request, string named)
    {
        AudioStubHandler api = Queue();

        Func<Task> act = () => Client(api).GenerateAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().ContainEquivalentOf(named);
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ vendor failures, classified

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "fal-auth-401.json", AudioGenerationErrorKind.Configuration)]
    [InlineData(HttpStatusCode.Forbidden, "fal-auth-401.json", AudioGenerationErrorKind.Configuration)]
    [InlineData(HttpStatusCode.PaymentRequired, "fal-payment-required-402.json", AudioGenerationErrorKind.VendorQuota)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "fal-validation-422.json", AudioGenerationErrorKind.UserInput)]
    [InlineData(HttpStatusCode.TooManyRequests, "fal-rate-limit-429.json", AudioGenerationErrorKind.Retryable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "fal-service-unavailable-503.json", AudioGenerationErrorKind.Retryable)]
    public async Task A_refused_submit_is_classified(HttpStatusCode status, string body, AudioGenerationErrorKind kind)
    {
        AudioStubHandler api = new(_ => AudioFixtures.Json(AudioFixtures.Text(body), status));

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Kind.Should().Be(kind);
        ex.VendorStatus.Should().Be((int)status);
        ex.Message.Should().NotContain("Key test");
    }

    /// <summary>QA-P1-18: every fal refusal fixture is fal's OWN error shape — a flat string <c>detail</c> (as the
    /// recorded 401 shows), which is what <see cref="FalQueueClient.VendorMessage"/> can read. Replaying ElevenLabs'
    /// nested <c>detail</c> object in a fal test was the unrealistic fixture: fal's own reader gets no message from it.</summary>
    [Theory]
    [InlineData("fal-rate-limit-429.json")]
    [InlineData("fal-validation-422.json")]
    [InlineData("fal-payment-required-402.json")]
    [InlineData("fal-service-unavailable-503.json")]
    public void The_fal_refusal_fixtures_are_fal_shaped(string fixture)
    {
        JsonElement body = JsonDocument.Parse(AudioFixtures.Text(fixture)).RootElement;

        body.GetProperty("detail").ValueKind.Should().Be(JsonValueKind.String,
            "fal answers a flat string detail; a nested object is the ElevenLabs shape");
        FalQueueClient.VendorMessage(AudioFixtures.Text(fixture)).Should().NotBeEmpty(
            "fal's own reader must be able to read the body the fal test replays");
    }

    [Fact]
    public async Task A_completed_job_carrying_an_error_is_a_vendor_failure_and_nothing_is_downloaded()
    {
        AudioStubHandler api = Queue(completed:
            "{\"status\":\"COMPLETED\",\"request_id\":\"764cabcf-b745-4b3e-ae38-1200304cf45b\",\"error\":\"generation failed\",\"error_type\":\"runner_error\"}");

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Kind.Should().Be(AudioGenerationErrorKind.VendorFailed);
        ex.Message.Should().Contain("generation failed");
        api.Requests.Should().NotContain(r => r.RequestUri!.Host != "queue.fal.run");
    }

    [Fact]
    public async Task A_result_refused_as_invalid_is_user_input()
    {
        AudioStubHandler api = Queue(result: "elevenlabs-validation-422.json", resultStatus: HttpStatusCode.UnprocessableEntity);

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Kind.Should().Be(AudioGenerationErrorKind.UserInput);
    }

    [Theory]
    [InlineData("{\"audio\":{\"url\":\"http://v3.fal.media/x.mp3\"}}")]
    [InlineData("{\"audio\":{\"url\":\"https://127.0.0.1/x.mp3\"}}")]
    [InlineData("{\"audio\":{\"url\":\"https://169.254.169.254/latest\"}}")]
    [InlineData("{\"audio\":{}}")]
    [InlineData("{\"video\":{\"url\":\"https://v3.fal.media/x.mp4\"}}")]
    public async Task An_unsafe_or_missing_audio_url_is_never_fetched(string resultBody)
    {
        AudioStubHandler api = new(request =>
        {
            if (request.Method == HttpMethod.Post) return AudioFixtures.Json(AudioFixtures.Text("fal-music-submit.json"));
            if (request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal))
                return AudioFixtures.Json(AudioFixtures.Text("fal-music-status-completed.json"));
            if (request.RequestUri.Host == "queue.fal.run") return AudioFixtures.Json(resultBody);
            return AudioFixtures.Audio(AudioFixtures.Silence);
        });

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
        api.Requests.Should().OnlyContain(r => r.RequestUri!.Host == "queue.fal.run");
    }

    [Fact]
    public async Task A_download_that_is_not_audio_is_refused()
    {
        AudioStubHandler api = Queue(cdn: _ => AudioFixtures.Json("<html/>"));

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Message.Should().Contain("application/json");
    }

    // ================================================================ construction and the probe

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_key_throws(string key)
    {
        FluentActions.Invoking(() => new FalAiAudioGenerationClient(new HttpClient(), key))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task The_probe_reads_one_random_request_status_on_the_app_route_and_creates_nothing()
    {
        AudioStubHandler api = new(_ => AudioFixtures.Json("{\"detail\":\"Request not found\"}", HttpStatusCode.NotFound));

        AudioGenerationProbeResult probe = await Client(api, probeModel: Music).ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeTrue(probe.Reason);
        HttpRequestMessage only = api.Requests.Should().ContainSingle().Which;
        only.Method.Should().Be(HttpMethod.Get);
        only.RequestUri!.ToString().Should().MatchRegex(
            "^https://queue\\.fal\\.run/fal-ai/elevenlabs/requests/[0-9a-f-]{36}/status$");
        only.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("Key test");
    }

    [Fact]
    public async Task The_probe_reports_a_rejected_key()
    {
        AudioStubHandler api = new(_ => AudioFixtures.Json(AudioFixtures.Text("fal-auth-401.json"), HttpStatusCode.Unauthorized));

        AudioGenerationProbeResult probe = await Client(api, probeModel: SfxV2).ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.AuthRejected.Should().BeTrue();
    }

    [Fact]
    public async Task The_probe_refuses_an_uncatalogued_model_without_a_request()
    {
        AudioStubHandler api = new(_ => AudioFixtures.Json("{}"));

        AudioGenerationProbeResult probe = await Client(api, probeModel: "fal-ai/stable-audio").ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        api.Requests.Should().BeEmpty();
    }
}
