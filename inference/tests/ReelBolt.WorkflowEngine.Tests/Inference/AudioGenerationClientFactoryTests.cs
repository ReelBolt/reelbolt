#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

public class AudioGenerationClientFactoryTests
{
    private static ResolvedAudioGenerationProvider Row(
        InferenceProviderKind kind, string key = "test", string endpoint = "",
        InferenceProviderCapability capability = InferenceProviderCapability.MusicGeneration) =>
        new(Guid.NewGuid(), "row", kind, capability, endpoint, "", key, 60);

    [Fact]
    public void A_FalAi_row_builds_its_client_and_is_cached()
    {
        AudioGenerationClientFactory factory = new();

        ResolvedAudioGenerationProvider fal = Row(InferenceProviderKind.FalAi);
        factory.Get(fal).Should().BeOfType<FalAiAudioGenerationClient>();
        factory.Get(fal).Should().BeSameAs(factory.Get(fal));
    }

    [Fact]
    public void A_dormant_ElevenLabs_row_never_gets_a_client()
    {
        // Product decision 7: fal only. The direct client stays in the tree, dormant; the factory never builds it.
        FluentActions.Invoking(() => new AudioGenerationClientFactory().Get(Row(InferenceProviderKind.ElevenLabs)))
            .Should().Throw<NotSupportedException>().WithMessage("*not offered*fal*");
    }

    [Theory]
    [InlineData(InferenceProviderKind.MiniMax)]
    [InlineData(InferenceProviderKind.FishAudio)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    public void Any_other_kind_is_refused(InferenceProviderKind kind)
    {
        FluentActions.Invoking(() => new AudioGenerationClientFactory().Get(Row(kind)))
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void A_blank_key_throws_rather_than_reading_the_environment()
    {
        FluentActions.Invoking(() => new AudioGenerationClientFactory().Get(Row(InferenceProviderKind.FalAi, key: "")))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_resolved_row_never_prints_its_key()
    {
        Row(InferenceProviderKind.ElevenLabs, key: "sk_secret").ToString().Should().NotContain("sk_secret");
        Row(InferenceProviderKind.ElevenLabs, key: "sk_secret").CacheKey.Should().NotContain("sk_secret");
    }

    [Fact]
    public void The_default_hosts_are_the_vendors()
    {
        ElevenLabsAudioGenerationClient.DefaultEndpoint.Should().Be("https://api.elevenlabs.io");
        FalAiAudioGenerationClient.DefaultEndpoint.Should().Be("https://queue.fal.run");
    }
}
