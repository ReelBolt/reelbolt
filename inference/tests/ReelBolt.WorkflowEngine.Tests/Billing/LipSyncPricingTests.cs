#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.ChangeRequests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>A talking-head step is held and priced on the LipSync capability and the model it really runs; never VideoGeneration.</summary>
public sealed class LipSyncPricingTests
{
    private const string Hint = "minimax/h3-max/lip-sync/image-to-video";
    private const string RowModel = "fal-ai/kling-video/ai-avatar/v2/standard";

    private const string TalkingHeadJson = """
        {"planSource":"Inline","inlinePrompt":"x","model":"minimax/h3-max/lip-sync/image-to-video","takes":1,"maxClips":1,
         "durationSeconds":8,"ratio":"9:16","maxSpendCredits":50,"avatarAudio":{"from":"Step","stepOrder":1}}
        """;

    private const string PlainJson = """
        {"planSource":"Inline","inlinePrompt":"x","model":"MiniMax-H3","takes":1,"maxClips":1,"durationSeconds":8,
         "resolution":"768P","ratio":"16:9","maxSpendCredits":50}
        """;

    private static ResolvedVideoGenerationProvider Row(string model, InferenceProviderKind kind = InferenceProviderKind.FalAi) =>
        new(Guid.NewGuid(), "row", kind, "https://x", model, "k", 60, AccountId: Guid.NewGuid());

    private sealed class Rig
    {
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public List<PriceRequest> Asked { get; } = [];
        public Mock<IModelPriceService> Prices { get; } = new();
        public List<string> Described { get; } = [];
        public IVideoGenerationClientFactory Clients { get; }

        public Rig(ResolvedVideoGenerationProvider? lipSync, ResolvedVideoGenerationProvider? video)
        {
            Resolver.Setup(r => r.ResolveLipSyncAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(lipSync);
            Resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(video);
            Prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Callback<PriceRequest, CancellationToken>((r, _) => Asked.Add(r))
                .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
            var client = new Mock<IVideoGenerationClient>();
            client.Setup(c => c.Describe(It.IsAny<string>())).Returns((string m) =>
            {
                Described.Add(m);
                return new VideoGenerationCapabilities(
                    new Dictionary<string, decimal> { ["768P"] = 0.08m, [FalVideoCatalog.NativeToken] = 0.05m }, 4, 15, ["16:9", "9:16"]);
            });
            var factory = new Mock<IVideoGenerationClientFactory>();
            factory.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);
            Clients = factory.Object;
        }
    }

    [Fact]
    public async Task The_hold_of_a_talking_head_is_priced_on_LipSync_and_the_rows_own_model()
    {
        Rig rig = new(lipSync: Row(RowModel), video: Row("MiniMax-H3", InferenceProviderKind.MiniMax));

        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(TalkingHeadJson, rig.Resolver.Object, rig.Clients, rig.Prices.Object);

        hold.Should().NotBeNull();
        PriceRequest asked = rig.Asked.Should().ContainSingle().Subject;
        asked.Capability.Should().Be(InferenceProviderCapability.LipSync);
        asked.ModelCode.Should().Be(RowModel, "no providerId on the step: the template's model is only a hint");
        asked.Unit.Should().Be(UsageKind.VideoGenSeconds);
        rig.Resolver.Verify(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_hold_of_the_template_case_prices_the_models_own_resolution_not_the_default_hold()
    {
        // Default LipSync row is Kling (native only); the template asks for 768P with no provider of its own.
        Rig rig = new(lipSync: Row(RowModel), video: Row("MiniMax-H3", InferenceProviderKind.MiniMax));
        string json = TalkingHeadJson.Replace("\"durationSeconds\":8,", "\"durationSeconds\":8,\"resolution\":\"768P\",");

        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(json, rig.Resolver.Object, rig.Clients, rig.Prices.Object);

        hold.Should().NotBeNull("the normalised settings are priced, not the StepCreditDefaults fallback");
        rig.Asked.Should().ContainSingle().Which.Variant.Should().Be(FalVideoCatalog.NativeToken);
    }

    [Fact]
    public async Task A_talking_head_with_no_LipSync_row_has_no_hold_and_never_borrows_the_video_default()
    {
        Rig rig = new(lipSync: null, video: Row("MiniMax-H3", InferenceProviderKind.MiniMax));

        (await VideoGenerateCreditPricing.TryHoldMillisAsync(TalkingHeadJson, rig.Resolver.Object, rig.Clients, rig.Prices.Object)).Should().BeNull();
        rig.Asked.Should().BeEmpty();
        rig.Resolver.Verify(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_plain_video_step_is_held_exactly_as_before_on_VideoGeneration()
    {
        Rig rig = new(lipSync: Row(RowModel), video: Row("MiniMax-H3", InferenceProviderKind.MiniMax));

        (await VideoGenerateCreditPricing.TryHoldMillisAsync(PlainJson, rig.Resolver.Object, rig.Clients, rig.Prices.Object)).Should().NotBeNull();
        PriceRequest asked = rig.Asked.Should().ContainSingle().Subject;
        asked.Capability.Should().Be(InferenceProviderCapability.VideoGeneration);
        asked.ModelCode.Should().Be("MiniMax-H3");
        rig.Resolver.Verify(r => r.ResolveLipSyncAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_change_request_exact_price_of_a_talking_head_uses_the_same_capability_and_model()
    {
        Rig rig = new(lipSync: Row(RowModel), video: Row("MiniMax-H3", InferenceProviderKind.MiniMax));
        var step = new WorkflowStep { StepType = StepType.VideoGenerate, VideoGenerateConfigJson = TalkingHeadJson };
        string output = """{"clips":[{"clipId":"v1","status":"succeeded","durationSeconds":8}]}""";

        StepChangeExactPrice? exact = await StepChangeExactPriceCalculator.TryComputeAsync(
            step, new ChangeRequestPayload(null, ["v1"], null), output, rig.Resolver.Object, rig.Prices.Object, CancellationToken.None);

        exact.Should().NotBeNull();
        PriceRequest asked = rig.Asked.Should().ContainSingle().Subject;
        asked.Capability.Should().Be(InferenceProviderCapability.LipSync);
        asked.ModelCode.Should().Be(RowModel);
    }
}
