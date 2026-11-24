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
/// The direct ElevenLabs route (<c>POST /v1/music</c>, <c>POST /v1/sound-generation</c>) replayed against the
/// recorded mocks of research/audio-vendors.md C4-C6. No live call, no real key.
/// </summary>
public class ElevenLabsAudioGenerationClientTests
{
    private static ElevenLabsAudioGenerationClient Client(AudioStubHandler handler, string key = AudioFixtures.Key) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io") }, key);

    private static AudioStubHandler Answers(HttpResponseMessage response) => new(_ => response);

    private static AudioGenerationRequest MusicRequest(
        string? prompt = "Warm uplifting corporate pop, light percussion, no vocals", double? duration = 30,
        bool? instrumental = true, string? plan = null, string model = "music_v2_5") =>
        new(AudioGenerationKind.Music, prompt, duration, Model: model, Instrumental: instrumental, CompositionPlanJson: plan);

    private static AudioGenerationRequest SfxRequest(double? duration = 1.5, string model = "eleven_text_to_sound_v2") =>
        new(AudioGenerationKind.SoundEffect, "A short whoosh transition, airy, fast", duration, Model: model,
            Loop: false, PromptInfluence: 0.3);

    // ================================================================ the recorded happy paths

    [Fact]
    public async Task Music_posts_the_recorded_body_with_the_format_as_a_query_parameter()
    {
        AudioStubHandler api = Answers(AudioFixtures.Audio(AudioFixtures.Silence, "audio/mpeg",
            ("song-id", "song-123"), ("current-concurrent-requests", "1"), ("maximum-concurrent-requests", "2")));

        AudioGenerationResult result = await Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        HttpRequestMessage request = api.Requests.Should().ContainSingle().Which;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://api.elevenlabs.io/v1/music?output_format=mp3_44100_128");
        request.Headers.GetValues("xi-api-key").Should().ContainSingle().Which.Should().Be("test");
        request.Headers.Contains("Authorization").Should().BeFalse();
        request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
        AudioFixtures.SameJson(api.Bodies[0], "elevenlabs-music-request.json").Should().BeTrue(api.Bodies[0]);

        result.Bytes.Should().Equal(AudioFixtures.Silence);
        result.MimeType.Should().Be("audio/mpeg");
        result.Extension.Should().Be(".mp3");
        result.DurationSec.Should().BeNull();
        result.VendorRequestId.Should().Be("song-123");
    }

    [Fact]
    public async Task Sound_generation_posts_the_recorded_body()
    {
        AudioStubHandler api = Answers(AudioFixtures.Audio(AudioFixtures.Silence, "audio/mpeg", ("character-cost", "60")));

        AudioGenerationResult result = await Client(api).GenerateAsync(SfxRequest(), CancellationToken.None);

        api.Requests[0].RequestUri!.ToString().Should().Be(
            "https://api.elevenlabs.io/v1/sound-generation?output_format=mp3_44100_128");
        AudioFixtures.SameJson(api.Bodies[0], "elevenlabs-sfx-request.json").Should().BeTrue(api.Bodies[0]);
        result.Extension.Should().Be(".mp3");
    }

    [Fact]
    public async Task The_model_id_is_always_sent_because_the_api_default_is_the_deprecated_v1()
    {
        AudioStubHandler api = Answers(AudioFixtures.Audio(AudioFixtures.Silence));

        await Client(api).GenerateAsync(MusicRequest(model: ""), CancellationToken.None);

        JsonDocument.Parse(api.Bodies[0]).RootElement.GetProperty("model_id").GetString().Should().Be("music_v2_5");
    }

    [Fact]
    public async Task A_chunks_composition_plan_is_passed_through_alone()
    {
        AudioStubHandler api = Answers(AudioFixtures.Audio(AudioFixtures.Silence));

        await Client(api).GenerateAsync(
            MusicRequest(prompt: null, duration: null, instrumental: null, plan: AudioFixtures.Text("elevenlabs-plan-v2.json")),
            CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(api.Bodies[0]);
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("composition_plan", "model_id");
        System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(body.RootElement.GetProperty("composition_plan").GetRawText()),
                AudioFixtures.Node("elevenlabs-plan-v2.json"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_thirty_second_effect_is_allowed_directly_although_fal_caps_at_twenty_two()
    {
        AudioStubHandler api = Answers(AudioFixtures.Audio(AudioFixtures.Silence));

        await Client(api).GenerateAsync(SfxRequest(duration: 30), CancellationToken.None);

        api.Requests.Should().ContainSingle();
    }

    // ================================================================ refusals before anything is sent

    public static IEnumerable<object[]> Refused()
    {
        yield return [MusicRequest(duration: 301), "300"];
        yield return [MusicRequest(model: "music_v1"), "music_v1"];
        yield return [MusicRequest(model: "eleven_text_to_sound_v2"), "eleven_text_to_sound_v2"];
        yield return [MusicRequest(plan: AudioFixtures.Text("elevenlabs-plan-v2.json")), "composition plan"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: AudioFixtures.Text("fal-plan-sections.json")), "sections"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: AudioFixtures.Text("elevenlabs-plan-v2.json").Replace("\"high\"", "\"extreme\"")), "context_adherence"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: AudioFixtures.Text("elevenlabs-plan-v2.json").Replace("10000", "130000")), "duration_ms"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null,
            plan: "{\"chunks\":[" + string.Join(",", Enumerable.Repeat(
                "{\"text\":\"[Verse]\",\"duration_ms\":3000,\"positive_styles\":[],\"negative_styles\":[]}", 31)) + "]}"), "30"];
        yield return [MusicRequest(prompt: null, duration: null, instrumental: null, plan: "not json"), "composition plan"];
        yield return [SfxRequest(duration: 31), "30"];
        yield return [SfxRequest(duration: 0.4), "0.5"];
        yield return [SfxRequest() with { OutputFormat = "pcm_48000" }, "pcm_48000"];
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task An_invalid_request_is_refused_before_any_request_is_sent(AudioGenerationRequest request, string named)
    {
        AudioStubHandler api = Answers(AudioFixtures.Audio(AudioFixtures.Silence));

        Func<Task> act = () => Client(api).GenerateAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().ContainEquivalentOf(named);
        api.Requests.Should().BeEmpty();
    }

    // ================================================================ vendor failures, classified

    [Fact]
    public async Task A_bad_prompt_is_user_input_carrying_the_vendors_suggestion()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json(AudioFixtures.Text("elevenlabs-bad-prompt.json"), HttpStatusCode.BadRequest));

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Kind.Should().Be(AudioGenerationErrorKind.UserInput);
        ex.Suggestion.Should().Be("An epic rock ballad with dramatic tempo changes ...");
        ex.Message.Should().Contain("bad_prompt").And.Contain("An epic rock ballad");
    }

    [Fact]
    public async Task A_bad_composition_plan_is_user_input_carrying_the_suggested_plan()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json(
            "{\"detail\":{\"status\":\"bad_composition_plan\",\"message\":\"illustrative\",\"data\":{\"composition_plan_suggestion\":{\"chunks\":[]}}}}",
            HttpStatusCode.UnprocessableEntity));

        Func<Task> act = () => Client(api).GenerateAsync(
            MusicRequest(prompt: null, duration: null, instrumental: null, plan: AudioFixtures.Text("elevenlabs-plan-v2.json")),
            CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Kind.Should().Be(AudioGenerationErrorKind.UserInput);
        ex.Suggestion.Should().Be("{\"chunks\":[]}");
    }

    /// <summary>QA-P1-18: each status replays a body shaped for that status. The 402/500/503 rows used to
    /// replay the 401 body, so the classifier was never exercised against a payment or a server error.</summary>
    [Theory]
    [InlineData("elevenlabs-payment-required-402.json", "payment_required")]
    [InlineData("elevenlabs-server-error-500.json", "internal_server_error")]
    [InlineData("elevenlabs-service-unavailable-503.json", "service_unavailable")]
    public void A_refusal_fixture_carries_its_own_status_error_shape(string file, string marker)
    {
        string body = AudioFixtures.Text(file);

        body.Should().Contain(marker);
        body.Should().NotContain("authentication_error", "the 401 body is not a payment or server error");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "elevenlabs-auth-401.json", AudioGenerationErrorKind.Configuration)]
    [InlineData(HttpStatusCode.Forbidden, "elevenlabs-auth-401.json", AudioGenerationErrorKind.Configuration)]
    [InlineData(HttpStatusCode.PaymentRequired, "elevenlabs-payment-required-402.json", AudioGenerationErrorKind.VendorQuota)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "elevenlabs-validation-422.json", AudioGenerationErrorKind.UserInput)]
    [InlineData(HttpStatusCode.TooManyRequests, "elevenlabs-concurrent-limit-429.json", AudioGenerationErrorKind.Retryable)]
    [InlineData(HttpStatusCode.InternalServerError, "elevenlabs-server-error-500.json", AudioGenerationErrorKind.Retryable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "elevenlabs-service-unavailable-503.json", AudioGenerationErrorKind.Retryable)]
    public async Task A_refusal_is_classified(HttpStatusCode status, string body, AudioGenerationErrorKind kind)
    {
        AudioStubHandler api = Answers(AudioFixtures.Json(AudioFixtures.Text(body), status));

        Func<Task> act = () => Client(api).GenerateAsync(SfxRequest(), CancellationToken.None);

        AudioGenerationException ex = (await act.Should().ThrowAsync<AudioGenerationException>()).Which;
        ex.Kind.Should().Be(kind);
        ex.VendorStatus.Should().Be((int)status);
        ex.IsRetryable.Should().Be(kind == AudioGenerationErrorKind.Retryable);
    }

    [Fact]
    public async Task The_validation_message_names_the_field()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json(AudioFixtures.Text("elevenlabs-validation-422.json"), HttpStatusCode.UnprocessableEntity));

        Func<Task> act = () => Client(api).GenerateAsync(MusicRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Message.Should().Contain("music_length_ms");
    }

    [Fact]
    public async Task A_success_that_is_not_audio_is_refused()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json("{\"ok\":true}"));

        Func<Task> act = () => Client(api).GenerateAsync(SfxRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Kind.Should().Be(AudioGenerationErrorKind.VendorFailed);
    }

    // ================================================================ the key

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_key_throws(string key)
    {
        FluentActions.Invoking(() => new ElevenLabsAudioGenerationClient(new HttpClient(), key))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_env_sentinel_reads_ELEVENLABS_API_KEY_only()
    {
        Dictionary<string, string?> env = new() { ["ELEVENLABS_API_KEY"] = "from-env", ["XI_API_KEY"] = "other" };
        ElevenLabsAudioGenerationClient.ResolveApiKey("env:", name => env.GetValueOrDefault(name)).Should().Be("from-env");
        ElevenLabsAudioGenerationClient.ResolveApiKey("sk_live_literal", _ => "from-env").Should().Be("sk_live_literal");
    }

    [Fact]
    public void The_env_sentinel_with_no_variable_throws_without_falling_back()
    {
        FluentActions.Invoking(() => ElevenLabsAudioGenerationClient.ResolveApiKey("env:", _ => null))
            .Should().Throw<ArgumentException>().WithMessage("*ELEVENLABS_API_KEY*");
    }

    [Fact]
    public async Task The_key_never_appears_in_an_error()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json("{\"detail\":{\"message\":\"bad key sk_secret_value\"}}", HttpStatusCode.Unauthorized));

        Func<Task> act = () => Client(api, key: "sk_secret_value").GenerateAsync(SfxRequest(), CancellationToken.None);

        (await act.Should().ThrowAsync<AudioGenerationException>()).Which.Message.Should().NotContain("sk_secret_value");
    }

    // ================================================================ the probe

    [Fact]
    public async Task The_probe_is_one_free_GET_of_the_models_list()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json("[{\"model_id\":\"eleven_multilingual_v2\"}]"));

        AudioGenerationProbeResult probe = await Client(api).ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeTrue(probe.Reason);
        HttpRequestMessage only = api.Requests.Should().ContainSingle().Which;
        only.Method.Should().Be(HttpMethod.Get);
        only.RequestUri!.ToString().Should().Be("https://api.elevenlabs.io/v1/models");
        only.Headers.GetValues("xi-api-key").Should().ContainSingle().Which.Should().Be("test");
    }

    [Fact]
    public async Task The_probe_reports_a_rejected_key()
    {
        AudioStubHandler api = Answers(AudioFixtures.Json(AudioFixtures.Text("elevenlabs-auth-401.json"), HttpStatusCode.Unauthorized));

        AudioGenerationProbeResult probe = await Client(api).ProbeAsync(CancellationToken.None);

        probe.Ok.Should().BeFalse();
        probe.AuthRejected.Should().BeTrue();
    }
}
