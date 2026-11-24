#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The SpeechSynthesis connectivity test measures the duration of the synthesized WAV. Fish Audio's
/// <c>POST /v1/tts</c> answer is a STREAMED WAV, so its RIFF header carries a placeholder data size
/// (0 or 0xFFFFFFFF) rather than the real byte count: the recorded fixtures below are that shape.
/// The shape is an ASSUMED fixture (no live vendor call is ever made from tests) and is listed as
/// "must measure" in docs/voiceover.md.
/// </summary>
public class SpeechSynthesisTesterTests
{
    private const int SampleRate = 44100;
    private const int Seconds = 1;

    private static MemoryStream StreamedWav(uint declaredDataSize, int payloadBytes = SampleRate * 2 * Seconds)
    {
        byte[] bytes = new byte[44 + payloadBytes];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), declaredDataSize == 0 ? 0 : uint.MaxValue);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), declaredDataSize);
        return new MemoryStream(bytes);
    }

    private static Task<TestInferenceProviderResponse> Run(MemoryStream wav)
    {
        Mock<ISpeechSynthesisClient> client = new();
        client.Setup(c => c.SynthesizeAsync(It.IsAny<SpeechRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SynthesizedSpeech(wav, "audio/wav"));
        Mock<ISpeechSynthesisClientFactory> factory = new();
        factory.Setup(f => f.Get(It.IsAny<ResolvedSpeechSynthesisProvider>())).Returns(client.Object);

        InferenceProviderTester tester = new(null!, Mock.Of<ISecretProtector>(), Mock.Of<IChatClientFactory>(),
            Mock.Of<ITranscriptionClientFactory>(), Mock.Of<IEmbeddingClientFactory>(), Mock.Of<IDecisionClientFactory>(),
            Mock.Of<IVideoGenerationClientFactory>(), factory.Object, NullLogger<InferenceProviderTester>.Instance);
        return tester.RunAsync(Guid.NewGuid(), "fish", InferenceProviderKind.FishAudio,
            InferenceProviderCapability.SpeechSynthesis, "https://api.fish.audio", "s2.1-pro", "test", 30, false, CancellationToken.None);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public async Task A_streamed_wav_with_a_placeholder_data_size_is_a_working_synthesis(uint declared)
    {
        TestInferenceProviderResponse result = await Run(StreamedWav(declared));

        result.Ok.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task A_wav_with_no_audio_after_the_header_still_reports_zero_duration()
    {
        TestInferenceProviderResponse result = await Run(StreamedWav(0, payloadBytes: 0));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("zero duration");
    }
}
