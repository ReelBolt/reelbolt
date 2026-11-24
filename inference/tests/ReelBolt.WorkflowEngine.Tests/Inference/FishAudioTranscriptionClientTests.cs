using System;
using System.IO;
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
/// Tests for <see cref="FishAudioTranscriptionClient"/> — the ASR half of the Fish Audio provider
/// kind. The wire format asserted here is taken from Fish Audio's own OpenAPI document at
/// <c>https://api.fish.audio/openapi.json</c> (OpenAPI 3.1.0, path <c>/v1/asr</c>, operation
/// <c>post</c>, summary "Speech to Text"), so these tests pin the vendor's contract rather than
/// this client's current behaviour. The HTTP layer is mocked with the same
/// <see cref="FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler"/> the TTS tests use —
/// nothing here ever reaches the live API.
/// </summary>
public class FishAudioTranscriptionClientTests
{
    /// <summary>
    /// A response in exactly the shape the OpenAPI document declares for a 200: <c>text</c>,
    /// <c>duration</c> and <c>segments</c> required, <c>language_code</c> and <c>language</c>
    /// optional, and each <c>ASRSegment</c> exactly <c>{text, start, end}</c>.
    /// </summary>
    private const string SuccessBody = """
        {"text":"Hello world.","duration":1.5,"segments":[{"text":"Hello world.","start":0.0,"end":1.5}],"language_code":"en","language":"English"}
        """;

    [Fact]
    public async Task TranscribeAsync_parses_text_segments_and_the_iso_language_code()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        TranscriptResult result = await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        result.Text.Should().Be("Hello world.");
        result.Segments.Should().HaveCount(1);
        result.Segments[0].Text.Should().Be("Hello world.");
        result.Segments[0].StartSec.Should().Be(0.0);
        result.Segments[0].EndSec.Should().Be(1.5);

        // `language_code` is the ISO 639-1 field the vendor tells callers to use in code, and
        // `language` is "for display only" — the code wins when both are present.
        result.Language.Should().Be("en");
    }

    [Fact]
    public async Task TranscribeAsync_falls_back_to_the_display_language_name_when_no_code_is_returned()
    {
        const string body = """
            {"text":"Ciao.","duration":1.0,"segments":[],"language":"Italian"}
            """;
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(body, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        TranscriptResult result = await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        result.Language.Should().Be("Italian");
        result.Segments.Should().BeEmpty();
    }

    [Fact]
    public async Task TranscribeAsync_reports_no_words_because_v1_asr_has_no_word_field()
    {
        // ASRSegment is exactly {text, start, end} and the 200 schema lists no words array, so a
        // caller asking for word timestamps still gets none — and must NOT get word timings
        // interpolated from segment bounds, which would be fabricated data.
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        TranscriptResult result = await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", "en", wordTimestamps: true, CancellationToken.None);

        result.Words.Should().BeEmpty();
    }

    [Fact]
    public async Task TranscribeAsync_leaves_whisper_only_confidence_fields_null()
    {
        // no_speech_prob / avg_logprob / compression_ratio are Whisper-family fields that
        // VideoAnalyze uses to discard music-and-silence segments. Fish does not report them, so
        // they must stay null ("not reported") rather than defaulting to 0, which would read as a
        // confident transcription of speech.
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        TranscriptResult result = await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        result.Segments[0].NoSpeechProb.Should().BeNull();
        result.Segments[0].AvgLogprob.Should().BeNull();
        result.Segments[0].CompressionRatio.Should().BeNull();
    }

    [Fact]
    public async Task TranscribeAsync_posts_multipart_to_v1_asr_with_the_documented_fields()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "chunk-0.wav", null, false, CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://api.fish.audio/v1/asr");
        handler.LastRequest!.Content.Should().BeOfType<MultipartFormDataContent>();

        string body = handler.LastRequestBody ?? string.Empty;
        body.Should().Contain("audio");
        body.Should().Contain("chunk-0.wav");
        body.Should().Contain("audio/wav");

        // `ignore_timestamps` defaults to TRUE in the vendor schema, which would return a response
        // without the precise timings this platform needs — so it must be sent explicitly as false.
        body.Should().Contain("ignore_timestamps");
        body.Should().Contain("false");
    }

    [Fact]
    public async Task TranscribeAsync_omits_the_language_field_entirely_when_no_hint_is_given()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        (handler.LastRequestBody ?? string.Empty).Should().NotContain("name=language");
    }

    [Fact]
    public async Task TranscribeAsync_sends_the_language_hint_when_present()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", "it", false, CancellationToken.None);

        string body = handler.LastRequestBody ?? string.Empty;
        body.Should().Contain("language");
        body.Should().Contain("it");
    }

    [Theory]
    [InlineData(null, "transcribe-1")]
    [InlineData("", "transcribe-1")]
    [InlineData("   ", "transcribe-1")]
    [InlineData("transcribe-1-pro", "transcribe-1-pro")]
    public async Task TranscribeAsync_sends_the_model_header(string modelOverride, string expected)
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(SuccessBody, null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), modelOverride);

        await client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        // `model` is a request HEADER, not a body field — the same convention the TTS client uses.
        handler.LastRequest!.Headers.GetValues("model").Single().Should().Be(expected);
    }

    [Fact]
    public void ResolveEffectiveModel_defaults_to_the_documented_transcribe_1()
    {
        FishAudioTranscriptionClient.ResolveEffectiveModel(null).Should().Be("transcribe-1");
        FishAudioTranscriptionClient.ResolveEffectiveModel("transcribe-1").Should().Be("transcribe-1");
        FishAudioTranscriptionClient.ResolveEffectiveModel("transcribe-1-pro").Should().Be("transcribe-1-pro");
    }

    [Fact]
    public async Task TranscribeAsync_surfaces_the_vendor_error_message_and_status_on_non_success()
    {
        // Fish's OpenAPI document declares the error shape for 401/402/503 as
        // {status, message, reason?}.
        const string errorBody = """
            {"status":402,"message":"Insufficient balance","reason":"insufficient_balance"}
            """;
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(errorBody, null, HttpStatusCode.PaymentRequired);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        Func<Task> act = () => client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        HttpRequestException ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.Message.Should().Contain("Insufficient balance");
        ex.Message.Should().Contain("402");
    }

    [Fact]
    public async Task TranscribeAsync_falls_back_to_the_raw_body_when_a_non_success_body_is_not_json()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler("<html>502 Bad Gateway</html>", null, HttpStatusCode.BadGateway);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        Func<Task> act = () => client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        HttpRequestException ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.Message.Should().Contain("502 Bad Gateway");
        ex.Message.Should().Contain("502");
    }

    [Fact]
    public async Task TranscribeAsync_bounds_a_vendor_error_message()
    {
        // The message is logged by callers, so a hostile or broken endpoint must not be able to
        // push an unbounded string into it.
        string errorBody = "{\"status\":500,\"message\":\"" + new string('x', 5000) + "\"}";
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(errorBody, null, HttpStatusCode.InternalServerError);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        Func<Task> act = () => client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        HttpRequestException ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.Message.Length.Should().BeLessThan(400);
    }

    [Fact]
    public async Task TranscribeAsync_propagates_a_transport_error()
    {
        var client = new FishAudioTranscriptionClient(NewHttpClient(new ThrowingHandler()), null);

        Func<Task> act = () => client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*connection refused*");
    }

    [Fact]
    public async Task TranscribeAsync_rejects_a_success_body_that_is_not_json()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler("not json at all", null, HttpStatusCode.OK);
        var client = new FishAudioTranscriptionClient(NewHttpClient(handler), null);

        Func<Task> act = () => client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*was not JSON*");
    }

    [Fact]
    public async Task TranscribeAsync_propagates_cancellation_rather_than_reporting_a_provider_failure()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        var client = new FishAudioTranscriptionClient(NewHttpClient(new ThrowingHandler()), null);

        Func<Task> act = () => client.TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()),
            "audio.wav", null, false, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpClient NewHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.fish.audio"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>A transport-level failure, the shape HttpClient raises when it cannot connect.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("connection refused");
        }
    }
}
