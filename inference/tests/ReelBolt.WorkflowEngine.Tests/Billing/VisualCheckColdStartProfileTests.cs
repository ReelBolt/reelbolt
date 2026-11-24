using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// T-102 item 4: a ReviewLoop whose <c>visualCheck</c> is enabled adds one vision call per iteration to its
/// cold-start profile (frames + 1 images, the checklist prompt, a short answer). Both the API's displayed
/// range and the engine's hold price a step through <see cref="ColdStartStepPricing.PriceProfile"/>, so this is
/// the one place the call is counted; anything else (off, absent, malformed, another step type) is unchanged.
/// </summary>
public class VisualCheckColdStartProfileTests
{
    private static readonly Guid ReviewerId = Guid.NewGuid();
    private static readonly IReadOnlyDictionary<Guid, AgentType> Types = new Dictionary<Guid, AgentType> { [ReviewerId] = AgentType.VideoReviewAgent };

    [Theory]
    [InlineData(null)]
    [InlineData("""{"enabled":true}""")]
    [InlineData("""{"visualCheck":{"enabled":false}}""")]
    [InlineData("""{"visualCheck":""")]
    public void Without_an_enabled_visual_check_the_profile_is_the_reviewers_own(string? config)
    {
        StepUsageProfile profile = StepUsageProfiles.For(ReviewLoop(config), AgentType.VideoReviewAgent);

        profile.Should().BeEquivalentTo(StepUsageProfiles.For(StepType.ReviewLoop, AgentType.VideoReviewAgent));
    }

    [Theory]
    [InlineData("""{"visualCheck":{"enabled":true}}""", 9)]
    [InlineData("""{"visualCheck":{"enabled":true,"frames":40}}""", 17)]
    [InlineData("""{"visualCheck":{"enabled":true,"frames":2}}""", 3)]
    public void An_enabled_visual_check_holds_its_worst_case_on_the_vision_model(string config, int images)
    {
        // S-77: up to two calls (one retry on a malformed answer), each with every frame at the largest per-image
        // charge and the reviewer's full output ceiling, priced on the vision target rather than the chat model.
        StepUsageProfile basis = StepUsageProfiles.For(StepType.ReviewLoop, AgentType.VideoReviewAgent);
        StepUsageProfile profile = StepUsageProfiles.For(ReviewLoop(config), AgentType.VideoReviewAgent);

        decimal expectedInput = StepUsageProfiles.VisualCheckCalls
            * (StepUsageProfiles.VisualCheckPromptTokens + images * StepUsageProfiles.VisualCheckTokensPerFrame);
        Vision(profile, UsageKind.LlmInputTokens).Should().Be(expectedInput);
        Vision(profile, UsageKind.LlmOutputTokens).Should().Be(StepUsageProfiles.VisualCheckCalls * StepUsageProfiles.VisualCheckOutputTokens);
        Quantity(profile, UsageKind.LlmInputTokens).Should().Be(Quantity(basis, UsageKind.LlmInputTokens), "the reviewer's chat is unchanged");
        Quantity(profile, UsageKind.LlmOutputTokens).Should().Be(Quantity(basis, UsageKind.LlmOutputTokens));
        profile.Quantities.Should().HaveCount(basis.Quantities.Count + 2);
        profile.Source.Should().Contain("visual check");
    }

    [Fact]
    public void The_visual_check_worst_case_constants_match_the_reviewer()
    {
        StepUsageProfiles.VisualCheckCalls.Should().Be(2, "one retry after a malformed answer (VisualFrameReviewer)");
        StepUsageProfiles.VisualCheckOutputTokens.Should().Be(3000, "VisualFrameReviewer's MaxOutputTokens");
        StepUsageProfiles.VisualCheckTokensPerFrame.Should().BeGreaterThanOrEqualTo(4_000,
            "a frame is held at the largest per-image charge of the priced vision vendors (AssistantTurnGuard.MinTokensPerImage)");
    }

    [Fact]
    public async Task The_vision_target_is_priced_on_the_default_vision_model()
    {
        Guid visionId = Guid.NewGuid();
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(visionId, "vision", InferenceProviderKind.Gemini, "https://v", "gemini-vision", "k", 60));
        Mock<IModelPriceService> prices = new();
        List<PriceRequest> asked = [];
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .Returns(new ValueTask<PriceQuote>(new PriceQuote(7_000, 1m, null)));
        prices.Setup(p => p.PriceQuotedUsdAsync(It.IsAny<decimal>(), It.IsAny<UsageKind>(), It.IsAny<UsageBillingMode>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<PriceQuote>(new PriceQuote(0, 0m, null)));
        ProfilePriceKey key = new(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, 6000m, AgentType.Custom, null);

        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync([key], resolver.Object, prices.Object, NullLogger.Instance, CancellationToken.None);

        pricer.IsSeedFallback(key).Should().BeFalse();
        asked.Should().ContainSingle().Which.ModelCode.Should().Be("gemini-vision");
    }

    [Fact]
    public void An_explicit_vision_provider_is_carried_on_the_vision_quantities_and_their_price_keys()
    {
        Guid explicitId = Guid.NewGuid();
        WorkflowStep step = ReviewLoop($$$"""{"visualCheck":{"enabled":true,"visionProviderId":"{{{explicitId}}}"}}""");
        var recorder = new RecordingProfilePricer();

        ColdStartStepPricing.PriceProfile(step, AgentType.VideoReviewAgent, Types, recorder);

        recorder.Requests.Where(k => k.Target == ProfilePriceTarget.Vision).Should().NotBeEmpty()
            .And.OnlyContain(k => k.ProviderId == explicitId);
        recorder.Requests.Where(k => k.Target == ProfilePriceTarget.Chat).Should().OnlyContain(k => k.ProviderId == null);
    }

    [Fact]
    public async Task An_explicit_vision_provider_is_priced_on_its_own_model_not_the_default_vision_model()
    {
        Guid explicitId = Guid.NewGuid();
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "vision", InferenceProviderKind.Gemini, "https://v", "default-vision", "k", 60));
        resolver.Setup(r => r.ResolveVisionAsync(explicitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(explicitId, "vision", InferenceProviderKind.Gemini, "https://v", "explicit-vision", "k", 60));
        Mock<IModelPriceService> prices = PriceService(out List<PriceRequest> asked);
        ProfilePriceKey explicitKey = new(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, 6000m, AgentType.Custom, null, explicitId);
        ProfilePriceKey defaultKey = explicitKey with { ProviderId = null };

        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
            [explicitKey, defaultKey], resolver.Object, prices.Object, NullLogger.Instance, CancellationToken.None);

        pricer.IsSeedFallback(explicitKey).Should().BeFalse();
        asked.Select(r => r.ModelCode).Should().BeEquivalentTo(["explicit-vision", "default-vision"]);
    }

    [Fact]
    public async Task An_explicit_vision_provider_with_no_price_falls_to_the_seed_figure_never_the_default_models_price()
    {
        Guid explicitId = Guid.NewGuid();
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(explicitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(explicitId, "vision", InferenceProviderKind.Gemini, "https://v", "unpriced-vision", "k", 60));
        Mock<IModelPriceService> prices = PriceService(out _);
        prices.Setup(p => p.PriceAsync(It.Is<PriceRequest>(r => r.ModelCode == "unpriced-vision"), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<PriceQuote>(Task.FromException<PriceQuote>(new InvalidOperationException("MODEL_UNPRICED"))));
        ProfilePriceKey key = new(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, 6000m, AgentType.Custom, null, explicitId);

        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync([key], resolver.Object, prices.Object, NullLogger.Instance, CancellationToken.None);

        pricer.IsSeedFallback(key).Should().BeTrue("an unpriced model is a guess the estimate must flag, not the default model's price");
        resolver.Verify(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IModelPriceService> PriceService(out List<PriceRequest> asked)
    {
        List<PriceRequest> requests = [];
        asked = requests;
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => requests.Add(r))
            .Returns(new ValueTask<PriceQuote>(new PriceQuote(7_000, 1m, null)));
        prices.Setup(p => p.PriceQuotedUsdAsync(It.IsAny<decimal>(), It.IsAny<UsageKind>(), It.IsAny<UsageBillingMode>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<PriceQuote>(new PriceQuote(0, 0m, null)));
        return prices;
    }

    [Fact]
    public void Only_a_review_loop_counts_the_call()
    {
        WorkflowStep agent = ReviewLoop("""{"visualCheck":{"enabled":true}}""");
        agent.StepType = StepType.Agent;

        StepUsageProfiles.For(agent, AgentType.VideoReviewAgent)
            .Should().BeEquivalentTo(StepUsageProfiles.For(StepType.Agent, AgentType.VideoReviewAgent));
    }

    [Fact]
    public void The_priced_cold_start_cost_rises_with_the_check()
    {
        StepCostDistribution off = ColdStartStepPricing.PriceProfile(ReviewLoop(null), AgentType.VideoReviewAgent, Types, SeedProfilePricer.Instance).Cost;
        StepCostDistribution on = ColdStartStepPricing.PriceProfile(
            ReviewLoop("""{"visualCheck":{"enabled":true}}"""), AgentType.VideoReviewAgent, Types, SeedProfilePricer.Instance).Cost;

        on.TypicalMillis.Should().BeGreaterThan(off.TypicalMillis);
        on.P75Millis.Should().BeGreaterThan(off.P75Millis);
    }

    private static decimal Vision(StepUsageProfile profile, UsageKind kind) =>
        profile.Quantities.Where(q => q.Kind == kind && q.Target == ProfilePriceTarget.Vision).Sum(q => q.Quantity);

    private static decimal Quantity(StepUsageProfile profile, UsageKind kind) =>
        profile.Quantities.Where(q => q.Kind == kind && q.Target == ProfilePriceTarget.Chat).Sum(q => q.Quantity);

    private static WorkflowStep ReviewLoop(string? config) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = 3,
        StepType = StepType.ReviewLoop,
        AgentDefinitionId = ReviewerId,
        LoopTargetStepOrder = 2,
        MaxIterations = 3,
        ReviewLoopConfigJson = config,
    };
}
