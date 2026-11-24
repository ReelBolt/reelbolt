#nullable enable
using System;
using System.Collections.Generic;
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
/// The connection test of a MusicGeneration / SoundEffectGeneration row goes through the audio client's no-cost
/// probe and never generates anything.
/// </summary>
public class AudioProviderTesterTests
{
    private sealed class ProbeOnlyClient(AudioGenerationProbeResult probe) : IAudioGenerationClient
    {
        public int Probes { get; private set; }
        public int Generations { get; private set; }

        public Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken ct, Func<string, Task>? onSubmitted = null)
        {
            Generations++;
            throw new InvalidOperationException("a provider test must never generate");
        }

        public Task<AudioGenerationProbeResult> ProbeAsync(CancellationToken ct)
        {
            Probes++;
            return Task.FromResult(probe);
        }

        public AudioGenerationCapabilities Describe(string model) => throw new NotSupportedException();
    }

    private sealed class Factory(Func<ResolvedAudioGenerationProvider, IAudioGenerationClient> build) : IAudioGenerationClientFactory
    {
        public List<ResolvedAudioGenerationProvider> Seen { get; } = [];

        public IAudioGenerationClient Get(ResolvedAudioGenerationProvider provider)
        {
            Seen.Add(provider);
            return build(provider);
        }
    }

    private static InferenceProviderTester Tester(IAudioGenerationClientFactory audio) =>
        new(null!, Mock.Of<ISecretProtector>(), Mock.Of<IChatClientFactory>(), Mock.Of<ITranscriptionClientFactory>(),
            Mock.Of<IEmbeddingClientFactory>(), Mock.Of<IDecisionClientFactory>(), Mock.Of<IVideoGenerationClientFactory>(),
            Mock.Of<ISpeechSynthesisClientFactory>(), NullLogger<InferenceProviderTester>.Instance,
            audioGenerationClientFactory: audio);

    private static Task<TestInferenceProviderResponse> Run(
        InferenceProviderTester tester, InferenceProviderKind kind, InferenceProviderCapability capability, string key = "test") =>
        tester.RunAsync(Guid.NewGuid(), "row", kind, capability, "", "", key, 30, false, CancellationToken.None);

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration)]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.SoundEffectGeneration)]
    public async Task An_audio_row_is_tested_through_the_probe_with_its_own_capability(
        InferenceProviderKind kind, InferenceProviderCapability capability)
    {
        ProbeOnlyClient client = new(new AudioGenerationProbeResult(true, false, "GET /v1/models answered HTTP 200."));
        Factory factory = new(_ => client);

        TestInferenceProviderResponse result = await Run(Tester(factory), kind, capability);

        result.Ok.Should().BeTrue(result.Error);
        client.Probes.Should().Be(1);
        client.Generations.Should().Be(0);
        factory.Seen.Should().ContainSingle().Which.Capability.Should().Be(capability);
    }

    [Fact]
    public async Task A_rejected_key_is_reported_as_an_authentication_failure()
    {
        Factory factory = new(_ => new ProbeOnlyClient(new AudioGenerationProbeResult(false, true, "HTTP 401")));

        TestInferenceProviderResponse result = await Run(Tester(factory), InferenceProviderKind.FalAi,
            InferenceProviderCapability.SoundEffectGeneration);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Provider rejected authentication.");
    }

    [Fact]
    public async Task A_blank_key_fails_the_test_cleanly_with_the_real_factory()
    {
        TestInferenceProviderResponse result = await Run(Tester(new AudioGenerationClientFactory()),
            InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, key: "");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("key");
    }

    [Fact]
    public async Task An_isolation_row_is_configured_only_and_never_builds_a_client()
    {
        Factory factory = new(_ => throw new InvalidOperationException("an isolation test must not build an audio client"));

        TestInferenceProviderResponse ok = await Run(Tester(factory), InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation);

        ok.Ok.Should().BeTrue(ok.Error);
        ok.ResponsePreview.Should().Contain("configured");
        factory.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task An_isolation_row_naming_an_unknown_model_fails_the_test()
    {
        TestInferenceProviderResponse result = await Tester(new Factory(_ => throw new InvalidOperationException())).RunAsync(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, "", "fal-ai/demucs",
            "test", 30, false, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("fal-ai/demucs");
    }

    [Theory]
    [InlineData(InferenceProviderCapability.VideoUpscale)]
    [InlineData(InferenceProviderCapability.FrameInterpolation)]
    [InlineData(InferenceProviderCapability.VideoOutpaint)]
    public async Task A_video_enhance_row_is_configured_only_and_never_builds_a_client(InferenceProviderCapability capability)
    {
        Factory factory = new(_ => throw new InvalidOperationException("a video-enhance test must not build an audio client"));

        TestInferenceProviderResponse ok = await Run(Tester(factory), InferenceProviderKind.FalAi, capability);

        ok.Ok.Should().BeTrue(ok.Error);
        ok.ResponsePreview.Should().Contain("configured");
        factory.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lip_sync_row_is_configured_only_and_checks_the_model_is_a_talking_head()
    {
        Factory factory = new(_ => throw new InvalidOperationException("a lip-sync test must not build a client"));
        InferenceProviderTester tester = Tester(factory);

        TestInferenceProviderResponse ok = await tester.RunAsync(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, "",
            "minimax/h3-max/lip-sync/image-to-video", "test", 30, false, CancellationToken.None);
        TestInferenceProviderResponse notTalking = await tester.RunAsync(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, "",
            "fal-ai/rife/video", "test", 30, false, CancellationToken.None);

        ok.Ok.Should().BeTrue(ok.Error);
        ok.ResponsePreview.Should().Contain("configured");
        notTalking.Ok.Should().BeFalse();
        notTalking.Error.Should().Contain("minimax/h3-max/lip-sync/image-to-video");
        factory.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task A_video_enhance_row_naming_an_unknown_or_wrong_capability_model_fails_the_test()
    {
        InferenceProviderTester tester = Tester(new Factory(_ => throw new InvalidOperationException()));

        TestInferenceProviderResponse unknown = await tester.RunAsync(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoUpscale, "", "fal-ai/seedvr/upscale/video",
            "test", 30, false, CancellationToken.None);
        TestInferenceProviderResponse wrong = await tester.RunAsync(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.FrameInterpolation, "", "fal-ai/topaz/upscale/video",
            "test", 30, false, CancellationToken.None);

        unknown.Ok.Should().BeFalse();
        unknown.Error.Should().Contain("fal-ai/seedvr/upscale/video");
        wrong.Ok.Should().BeFalse();
        wrong.Error.Should().Contain("fal-ai/rife/video");

        TestInferenceProviderResponse outpaintWrong = await tester.RunAsync(
            Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.VideoOutpaint, "", "fal-ai/rife/video",
            "test", 30, false, CancellationToken.None);
        outpaintWrong.Ok.Should().BeFalse();
        outpaintWrong.Error.Should().Contain("fal-ai/wan-vace-14b/outpainting");
    }
}
