#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>The admin boundary and the capability/usage table for the talking-head LipSync capability.</summary>
public class LipSyncCapabilityRulesTests
{
    private const string TalkingHead = "minimax/h3-max/lip-sync/image-to-video";
    private const string PlainVideo = "fal-ai/kling-video/v3/turbo/pro/image-to-video";

    [Fact]
    public void LipSync_meters_VideoGenSeconds_and_VideoGeneration_stays_the_default_for_that_unit()
    {
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.LipSync].Should().Equal(UsageKind.VideoGenSeconds);
        ProviderCapabilityUsage.DefaultCapabilityFor(UsageKind.VideoGenSeconds).Should().Be(InferenceProviderCapability.VideoGeneration);
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi].Should().Contain(InferenceProviderCapability.LipSync);
    }

    [Fact]
    public void Every_kind_but_FalAi_is_refused_for_LipSync()
    {
        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>())
        {
            bool refused = InferenceProvidersController.IsUnsupportedCombination(kind, InferenceProviderCapability.LipSync, out string error);
            refused.Should().Be(kind != InferenceProviderKind.FalAi, kind + ": " + error);
        }
    }

    [Fact]
    public void A_LipSync_row_must_name_a_talking_head_model()
    {
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, TalkingHead, out _)
            .Should().BeFalse();
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, "fal-ai/kling-video/ai-avatar/v2/standard", out _)
            .Should().BeFalse();
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, PlainVideo, out string notTalking)
            .Should().BeTrue();
        notTalking.Should().Contain(TalkingHead);
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, "", out _)
            .Should().BeTrue("blank would mean the default image-to-video endpoint");
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, "nope/unknown", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void A_VideoGeneration_row_may_no_longer_name_a_talking_head_model_and_says_where_to_add_it()
    {
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, TalkingHead, out string error)
            .Should().BeTrue();
        error.Should().Contain("Lip-sync");
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoGeneration, PlainVideo, out _)
            .Should().BeFalse();
    }
}
