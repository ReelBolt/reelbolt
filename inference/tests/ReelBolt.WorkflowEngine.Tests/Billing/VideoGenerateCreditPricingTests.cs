#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// WP B6c: VideoGenerate on the credit path. Before this WP the step wrote
/// <see cref="UsageKind.VideoGenSeconds"/> usage rows with non-zero <c>credits_millis</c> (B3c) but
/// held <b>0</b> credits, and a zero hold short-circuits the gate into taking no reservation — so a
/// step with no reservation is never settled, and those credits were recorded and never charged.
///
/// <para>
/// These tests pin the two halves that close that leak: the hold is sized from the step's OWN
/// settings at the model's per-second rate-card price (so it matches the settlement by
/// construction), and the step falls back to a real, non-zero B0 row — never to zero — when the
/// price cannot be resolved.
/// </para>
/// </summary>
public sealed class VideoGenerateCreditPricingTests
{
    private const string Model = "MiniMax-H3";

    // -----------------------------------------------------------------
    // The shape: what the step is configured to buy
    // -----------------------------------------------------------------

    [Fact]
    public void An_inline_step_is_priced_at_its_takes_times_its_own_duration()
    {
        VideoGenerateStepConfig config = new()
        {
            PlanSource = VideoGeneratePlanSource.Inline,
            Takes = 3,
            MaxClips = 3,
            DurationSeconds = 6,
            Resolution = "768P",
            MaxSpendCredits = 5m
        };

        VideoGenerateCreditHold hold = VideoGenerateCreditPricing.TryShape(config, Capabilities())!.Value;

        hold.Clips.Should().Be(3);
        hold.SecondsPerClip.Should().Be(6);
        hold.Seconds.Should().Be(18);
    }

    [Fact]
    public void A_plan_sourced_step_is_priced_at_its_clip_ceiling_and_the_models_midpoint_duration()
    {
        VideoGenerateStepConfig config = new()
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Previous, null),
            Takes = 1,
            MaxClips = 4,
            DurationSeconds = 6,
            Resolution = "768P",
            MaxSpendCredits = 5m
        };

        VideoGenerateCreditHold hold = VideoGenerateCreditPricing.TryShape(config, Capabilities())!.Value;

        // The plan's own shots do not exist before the planner step runs, so the ceiling is MaxClips
        // (the executor's own hard cap across the whole step) and the per-clip duration is the pinned
        // midpoint of the model's range -- SnapDurationSeconds("Medium") over MiniMax-H3's 4..15s.
        hold.Clips.Should().Be(4);
        hold.SecondsPerClip.Should().Be(ShotPlanParameterMapper.SnapDurationSeconds(Capabilities(), "Medium"));
        hold.SecondsPerClip.Should().Be(9);
    }

    [Fact]
    public void The_hold_scales_linearly_with_clip_count_beyond_four_for_plan_and_inline_steps()
    {
        VideoGenerateCreditHold Shape(VideoGeneratePlanSource source, int clips) =>
            VideoGenerateCreditPricing.TryShape(new VideoGenerateStepConfig
            {
                PlanSource = source,
                Plan = source == VideoGeneratePlanSource.StepRef ? new ExtractInputRef(ExtractInputSource.Previous, null) : null,
                Takes = source == VideoGeneratePlanSource.Inline ? clips : 1,
                MaxClips = clips,
                DurationSeconds = 6,
                Resolution = "768P",
                MaxSpendCredits = 5m
            }, Capabilities())!.Value;

        foreach (VideoGeneratePlanSource source in new[] { VideoGeneratePlanSource.StepRef, VideoGeneratePlanSource.Inline })
        {
            VideoGenerateCreditHold four = Shape(source, 4);
            VideoGenerateCreditHold eight = Shape(source, 8);

            eight.Clips.Should().Be(8);
            eight.Seconds.Should().Be(four.Seconds * 2);
            eight.HoldMillis.Should().Be(four.HoldMillis * 2);
        }
    }

    [Fact]
    public void A_plan_sourced_step_without_a_model_description_has_no_shape()
    {
        VideoGenerateStepConfig config = new() { PlanSource = VideoGeneratePlanSource.StepRef, MaxClips = 2 };

        VideoGenerateCreditPricing.TryShape(config, capabilities: null).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    public void An_unreadable_config_has_no_shape(string? json)
    {
        VideoGenerateCreditPricing.TryShape(json, Capabilities()).Should().BeNull();
    }

    [Fact]
    public void A_config_with_no_clips_has_no_shape()
    {
        VideoGenerateStepConfig config = new() { Takes = 0, MaxClips = 0, DurationSeconds = 6 };

        VideoGenerateCreditPricing.TryShape(config, Capabilities()).Should().BeNull();
    }

    // -----------------------------------------------------------------
    // The hold: the same per-second price the settlement uses
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_hold_is_the_steps_own_seconds_at_the_rate_cards_per_second_price()
    {
        const string json = """
            {"planSource":"Inline","inlinePrompt":"a shot","takes":3,"maxClips":3,"durationSeconds":6,
             "resolution":"768P","ratio":"16:9","maxSpendCredits":5}
            """;

        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(
            json, Resolver(), Clients(), Rates(7_000), CancellationToken.None);

        // 3 takes x 6s x 7,000 millicredits/s. This is the SAME lookup (same unit, same provider,
        // same model) the step's own VideoGenSeconds settlement performs, so the hold matches the
        // charge rather than approximating it.
        hold.Should().Be(126_000);
    }

    [Fact]
    public async Task The_run_briefs_avatar_adds_the_input_material_charge_to_a_step_that_names_none()
    {
        const string json = """
            {"planSource":"Inline","inlinePrompt":"a shot","takes":3,"maxClips":3,"durationSeconds":6,
             "resolution":"768P","ratio":"16:9","maxSpendCredits":5}
            """;
        const string named = """
            {"planSource":"Inline","inlinePrompt":"a shot","takes":3,"maxClips":3,"durationSeconds":6,
             "resolution":"768P","ratio":"16:9","maxSpendCredits":5,"avatarId":"00000000-0000-4000-8000-000000000001"}
            """;
        const string frames = """
            {"planSource":"Inline","inlinePrompt":"a shot","takes":3,"maxClips":3,"durationSeconds":6,
             "resolution":"768P","ratio":"16:9","maxSpendCredits":5,"firstFrameProjectFileId":"00000000-0000-4000-8000-000000000002"}
            """;

        async Task<long?> Hold(string config) => await VideoGenerateCreditPricing.TryHoldMillisAsync(
            config, Resolver(), AvatarClients(), new InputMaterialPriceService(7_000, 9_000), CancellationToken.None);

        long? plain = await Hold(json);
        plain.Should().Be(126_000, "a run with no brief avatar is priced exactly as before");

        using (BriefAvatarPricing.Enter(Guid.Parse("00000000-0000-4000-8000-000000000009")))
        {
            (await Hold(json)).Should().Be(126_000 + 9_000, "the brief's avatar fills the step, so its input material is held");
            (await Hold(named)).Should().Be(126_000 + 9_000, "a step that names its own avatar is priced the same either way");
            (await Hold(frames)).Should().Be(126_000, "a frame-mode step never takes the brief's avatar");
        }

        (await Hold(json)).Should().Be(126_000, "the ambient ends with its scope");
        using (BriefAvatarPricing.Enter(Guid.Empty))
            (await Hold(json)).Should().Be(126_000, "an empty id is no avatar");
    }

    [Fact]
    public async Task A_byo_provider_prices_to_zero_and_therefore_holds_nothing()
    {
        const string json = """
            {"planSource":"Inline","inlinePrompt":"a shot","takes":3,"maxClips":3,"durationSeconds":6,
             "resolution":"768P","ratio":"16:9","maxSpendCredits":5}
            """;

        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(
            json, Resolver(organizationId: Guid.NewGuid()), Clients(), Rates(7_000), CancellationToken.None);

        // D8: BYO inference burns no platform credits. The rate card's Byo multiplier is 0, which is
        // also what the settlement records for this take -- and a zero hold takes no reservation.
        hold.Should().Be(0);
    }

    [Fact]
    public async Task An_unresolvable_provider_has_no_hold()
    {
        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(
            InlineJson(), Resolver(resolved: false), Clients(), Rates(7_000), CancellationToken.None);

        hold.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_model_has_no_hold()
    {
        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(
            InlineJson(), Resolver(), Clients(modelKnown: false), Rates(7_000), CancellationToken.None);

        hold.Should().BeNull();
    }

    [Fact]
    public async Task An_unpriced_unit_has_no_hold_rather_than_a_free_one()
    {
        long? hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(
            InlineJson(), Resolver(), Clients(), new ThrowingPriceService(), CancellationToken.None);

        // A missing price must not read as "this step is free" at the reservation: the caller
        // falls back to B0's non-zero default row, which still takes a reservation.
        hold.Should().BeNull();
    }

    [Fact]
    public void The_cold_start_default_row_is_a_real_video_sized_figure()
    {
        (long low, long high) = StepCreditDefaults.MillisFor(StepType.VideoGenerate);

        // B6c made this row non-zero on purpose: it is the fallback for a step whose own settings
        // cannot be priced, and a zero here is the leak. It is derived from the heuristic per-second
        // price in PricingDefaults, so re-pricing that moves this row with it; at the default markup, like
        // every seed fallback (T-030).
        low.Should().BePositive();
        high.Should().BeGreaterThanOrEqualTo(low);
        low.Should().Be((long)Math.Round(
            Math.Round(12m * PricingDefaults.VideoGenUsdPerUnit * PricingDefaults.CreditsMillisPerUsd) * PricingDefaults.DefaultMarkup));
        StepCreditDefaults.IsNotCharged(StepType.VideoGenerate).Should().BeFalse();
    }

    [Fact]
    public void A_declared_video_hold_wins_over_history()
    {
        var estimator = new StepCreditEstimator(
            Enumerable.Range(1, 10).Select(i => new StepCreditSample(StepType.VideoGenerate, null, i * 1_000)));

        StepCreditEstimate estimate = estimator.Estimate(StepType.VideoGenerate, null, videoGenerateHoldMillis: 126_000);

        estimate.Basis.Should().Be(CreditEstimateBasis.VideoGeneration);
        estimate.HoldMillis.Should().Be(126_000);
        estimate.LowMillis.Should().Be(126_000);
        estimate.FromHistory.Should().BeFalse();
    }

    [Fact]
    public void Without_a_declared_hold_a_video_step_still_estimates_from_history()
    {
        var estimator = new StepCreditEstimator(
            Enumerable.Range(1, 10).Select(i => new StepCreditSample(StepType.VideoGenerate, null, i * 10_000)));

        StepCreditEstimate estimate = estimator.Estimate(StepType.VideoGenerate, null);

        estimate.HoldMillis.Should().Be(91_000);
        estimate.Basis.Should().Be(CreditEstimateBasis.StepTypeHistory);
    }

    // -----------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------

    private static string InlineJson() => """
        {"planSource":"Inline","inlinePrompt":"a shot","takes":3,"maxClips":3,"durationSeconds":6,
         "resolution":"768P","ratio":"16:9","maxSpendCredits":5}
        """;

    private static VideoGenerationCapabilities Capabilities() => new(
        PricePerSecondByResolution: new Dictionary<string, decimal> { { "768P", 0.08m } },
        MinDurationSeconds: 4,
        MaxDurationSeconds: 15,
        AllowedRatios: ["16:9"]);

    private static IInferenceProviderResolver Resolver(Guid? organizationId = null, bool resolved = true)
    {
        var mock = new Mock<IInferenceProviderResolver>();
        mock.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved
                ? new ResolvedVideoGenerationProvider(
                    Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax, "https://api.minimax.io",
                    Model, "key", 60, organizationId)
                : null);
        return mock.Object;
    }

    private static IVideoGenerationClientFactory Clients(bool modelKnown = true)
    {
        var client = new Mock<IVideoGenerationClient>();
        if (modelKnown)
        {
            client.Setup(c => c.Describe(It.IsAny<string>())).Returns(Capabilities());
        }
        else
        {
            client.Setup(c => c.Describe(It.IsAny<string>()))
                .Throws(new NotSupportedException("Model nope is not supported"));
        }

        var factory = new Mock<IVideoGenerationClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);
        return factory.Object;
    }

    private static IVideoGenerationClientFactory AvatarClients()
    {
        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.Describe(It.IsAny<string>())).Returns(Capabilities());
        var support = client.As<IVideoReferenceInputSupport>();
        support.Setup(c => c.ReferenceInputSupport(It.IsAny<string>()))
            .Returns(new VideoReferenceInputSupport(1, 0, 0, 1, 0, 0, false, false));
        support.Setup(c => c.InputMaterialCostUsd(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<VideoReferenceMaterial>>()))
            .Returns(1m);
        var factory = new Mock<IVideoGenerationClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);
        return factory.Object;
    }

    private sealed class InputMaterialPriceService(long millisPerSecond, long inputMillis) : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new PriceQuote(request.Quantity > 0 ? (long)(request.Quantity * millisPerSecond) : 0, 0m, Guid.NewGuid()));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) => ValueTask.FromResult(new PriceQuote(inputMillis, 0m, null));

        public void Invalidate()
        {
        }
    }

    private static IModelPriceService Rates(long millisPerSecond) => new PerSecondPriceService(millisPerSecond);

    private sealed class PerSecondPriceService(long millisPerSecond) : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            request.Unit.Should().Be(UsageKind.VideoGenSeconds);
            request.ModelCode.Should().Be(Model);
            request.Capability.Should().Be(InferenceProviderCapability.VideoGeneration);
            request.Variant.Should().Be("768P", "the configured resolution is the price variant the settle will use");

            decimal millis = request.BillingMode == UsageBillingMode.Platform ? request.Quantity * millisPerSecond : 0m;
            return ValueTask.FromResult(new PriceQuote((long)millis, 0m, Guid.NewGuid()));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) => throw new InvalidOperationException("not an oracle path");

        public void Invalidate()
        {
        }
    }

    private sealed class ThrowingPriceService : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
            => throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) => throw new InvalidOperationException("not an oracle path");

        public void Invalidate()
        {
        }
    }
}
