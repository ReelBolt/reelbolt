#nullable enable
using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The admin boundary for the two audio capabilities. Product decision 7 (superseding decision 2's direct route): fal
/// only. An audio capability is refused for every kind but FalAi, and the dormant ElevenLabs kind is refused
/// every capability.
/// </summary>
public class AudioGenerationCapabilityRulesTests
{
    private static readonly InferenceProviderCapability[] Audio =
        [InferenceProviderCapability.MusicGeneration, InferenceProviderCapability.SoundEffectGeneration];

    [Theory]
    [InlineData(InferenceProviderCapability.MusicGeneration)]
    [InlineData(InferenceProviderCapability.SoundEffectGeneration)]
    public void Fal_is_the_one_audio_route(InferenceProviderCapability capability)
    {
        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out string error).Should().BeFalse(error);
    }

    [Fact]
    public void Every_other_kind_is_refused_an_audio_capability()
    {
        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>().Where(k => k != InferenceProviderKind.FalAi))
        {
            foreach (InferenceProviderCapability capability in Audio)
            {
                InferenceProvidersController.IsUnsupportedCombination(kind, capability, out string error)
                    .Should().BeTrue(kind + " must not serve " + capability);
                // LocalRunner has its own Chat/Vision(/Embedding) allowlist sentence; every other kind points at fal.
                error.Should().Contain(kind == InferenceProviderKind.LocalRunner ? "LocalRunner" : "FalAi");
            }
        }
    }

    [Fact]
    public void The_dormant_ElevenLabs_kind_is_refused_every_capability_with_a_clear_message()
    {
        // Product decision 7: the enum member stays (append-only) but no capability is accepted for it.
        foreach (InferenceProviderCapability capability in Enum.GetValues<InferenceProviderCapability>())
        {
            InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.ElevenLabs, capability, out string error)
                .Should().BeTrue("ElevenLabs must not serve " + capability);
            if (capability != InferenceProviderCapability.Compute)
                error.Should().Contain("ElevenLabs").And.Contain("not offered").And.Contain("FalAi");
        }
    }

    [Fact]
    public void FalAi_keeps_every_capability_it_already_served()
    {
        foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi])
        {
            InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out string error)
                .Should().BeFalse(error);
        }

        InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, InferenceProviderCapability.Chat, out _)
            .Should().BeTrue();
    }

    [Fact]
    public void The_capability_table_lists_the_audio_routes_and_their_units()
    {
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.ElevenLabs].Should().BeEmpty("the kind is dormant (decision 7)");
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi].Should().EndWith([.. Audio, InferenceProviderCapability.AudioIsolation, InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation, InferenceProviderCapability.VideoOutpaint, InferenceProviderCapability.LipSync]);
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.MusicGeneration]
            .Should().Equal(UsageKind.MusicGenMinutes);
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.SoundEffectGeneration]
            .Should().Equal(UsageKind.SoundEffectGenSeconds);
    }

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "fal-ai/elevenlabs/sound-effects/v2")]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.SoundEffectGeneration, "fal-ai/elevenlabs/music")]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "fal-ai/stable-audio-25/text-to-audio")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration, "eleven_text_to_sound_v2")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration, "music_v1")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.SoundEffectGeneration, "music_v2_5")]
    public void A_model_the_catalogue_does_not_list_for_the_capability_is_refused(
        InferenceProviderKind kind, InferenceProviderCapability capability, string model)
    {
        InferenceProvidersController.IsUnsupportedModel(kind, capability, model, out string error).Should().BeTrue();
        error.Should().Contain(model);
    }

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.MusicGeneration, "fal-ai/elevenlabs/music")]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.SoundEffectGeneration, "fal-ai/elevenlabs/sound-effects/v2")]
    [InlineData(InferenceProviderKind.FalAi, InferenceProviderCapability.SoundEffectGeneration, "fal-ai/mmaudio-v2/text-to-audio")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration, "music_v2_5")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.MusicGeneration, "music_v2")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.SoundEffectGeneration, "eleven_text_to_sound_v2")]
    [InlineData(InferenceProviderKind.ElevenLabs, InferenceProviderCapability.SoundEffectGeneration, "")]
    public void A_catalogued_model_or_a_blank_one_is_accepted(
        InferenceProviderKind kind, InferenceProviderCapability capability, string model)
    {
        InferenceProvidersController.IsUnsupportedModel(kind, capability, model, out string error).Should().BeFalse(error);
    }
}
