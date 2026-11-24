using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The local OpenAI-compatible TTS path (docs/voiceover.md "A local OpenAI-compatible TTS server"):
/// the verified <c>POST {base}/audio/speech</c> JSON shape, the no-ambient-credential rule, and the
/// factory arm that builds it.
/// </summary>
public class OpenAICompatibleSpeechSynthesisClientTests
{
    [Fact]
    public async Task Sends_the_documented_create_speech_body_with_wav_and_the_default_voice()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(
            null, FishAudioSpeechSynthesisClientTests.GetSampleWavBytes(), HttpStatusCode.OK);
        var client = new OpenAICompatibleSpeechSynthesisClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://whisper:8000/v1/") }, "speaches-ai/Kokoro-82M-v1.0-ONNX");

        SynthesizedSpeech result = await client.SynthesizeAsync(new SpeechRequest("Hello there", null, null), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://whisper:8000/v1/audio/speech");
        handler.LastRequest.Headers.Authorization.Should().BeNull("a blank key sends no Authorization header and reads no environment variable");
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("speaches-ai/Kokoro-82M-v1.0-ONNX");
        body.RootElement.GetProperty("input").GetString().Should().Be("Hello there");
        body.RootElement.GetProperty("voice").GetString().Should().Be(OpenAICompatibleSpeechSynthesisClient.DefaultVoice);
        body.RootElement.GetProperty("response_format").GetString().Should().Be("wav");
        body.RootElement.TryGetProperty("pronunciation_dictionary", out _).Should().BeFalse("not on this contract");
        result.ContentType.Should().Be("audio/wav");
        result.Wav.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Uses_the_requested_voice_and_model_override_and_refuses_over_long_input()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(
            null, FishAudioSpeechSynthesisClientTests.GetSampleWavBytes(), HttpStatusCode.OK);
        var client = new OpenAICompatibleSpeechSynthesisClient(new HttpClient(handler) { BaseAddress = new Uri("http://tts:9000/v1") }, "tts-1");

        await client.SynthesizeAsync(new SpeechRequest("Hi", "af_heart", "gpt-4o-mini-tts"), CancellationToken.None);
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("voice").GetString().Should().Be("af_heart");
        body.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o-mini-tts");

        Func<Task> tooLong = () => client.SynthesizeAsync(
            new SpeechRequest(new string('a', OpenAICompatibleSpeechSynthesisClient.MaxInputChars + 1), null, null), CancellationToken.None);
        await tooLong.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_vendor_error_surfaces_the_message_and_status_without_a_500()
    {
        var handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(
            "{\"error\":{\"message\":\"Incorrect API key provided\",\"type\":\"invalid_request_error\"}}", null, HttpStatusCode.Unauthorized);
        var client = new OpenAICompatibleSpeechSynthesisClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1") }, null);

        Func<Task> act = () => client.SynthesizeAsync(new SpeechRequest("Hi", null, null), CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("Incorrect API key provided").And.Contain("401");
    }

    [Fact]
    public void Factory_builds_the_OpenAI_compatible_client_for_that_kind_and_still_refuses_cloud_chat_kinds()
    {
        var factory = new SpeechSynthesisClientFactory();

        ISpeechSynthesisClient client = factory.Get(new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "local-tts", InferenceProviderKind.OpenAICompatible, "http://whisper:8000/v1", "tts-1", "", 30));
        client.Should().BeOfType<OpenAICompatibleSpeechSynthesisClient>();
        client.Should().NotBeAssignableTo<IVoiceCloningClient>("only Fish exposes a cloning API");

        factory.Get(new ResolvedSpeechSynthesisProvider(
                Guid.NewGuid(), "fish", InferenceProviderKind.FishAudio, "https://api.fish.audio", "s2.1-pro", "k", 30))
            .Should().BeAssignableTo<IVoiceCloningClient>();

        Action azure = () => factory.Get(new ResolvedSpeechSynthesisProvider(
            Guid.NewGuid(), "azure", InferenceProviderKind.AzureOpenAI, "https://x.openai.azure.com", "tts", "k", 30));
        azure.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Effective_model_precedence_is_request_then_row_then_tts_1()
    {
        OpenAICompatibleSpeechSynthesisClient.ResolveEffectiveModel("req", "row").Should().Be("req");
        OpenAICompatibleSpeechSynthesisClient.ResolveEffectiveModel(null, "row").Should().Be("row");
        OpenAICompatibleSpeechSynthesisClient.ResolveEffectiveModel("", "").Should().Be("tts-1");
    }
}
