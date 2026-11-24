#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The hosted video-enhance catalogue, its price rows (cited, never invented), the capability rules and the fail-closed
/// credit pricing (tiers, high-frame-rate doubling, the compute-second hold, whole billed seconds and the 120 s cap).
/// </summary>
public class VideoEnhancePricingTests
{
    private const string Topaz = "fal-ai/topaz/upscale/video";
    private const string Bytedance = "fal-ai/bytedance-upscaler/upscale/video";
    private const string Rife = "fal-ai/rife/video";
    private const string Film = "fal-ai/film/video";
    private const string WanVace = "fal-ai/wan-vace-14b/outpainting";
    private const string LumaReframe = "fal-ai/luma-dream-machine/ray-2/reframe";
    private static VideoEnhanceModelCatalog Catalog => VideoEnhanceModelCatalog.Instance;

    [Fact]
    public void Each_capability_has_exactly_one_default_and_it_is_the_decided_one()
    {
        Catalog.DefaultFor(InferenceProviderCapability.VideoUpscale).Id.Should().Be(Topaz);
        Catalog.DefaultFor(InferenceProviderCapability.FrameInterpolation).Id.Should().Be(Rife);
        Catalog.DefaultFor(InferenceProviderCapability.VideoOutpaint).Id.Should().Be(WanVace);
        Catalog.All.Count(e => e.IsDefault).Should().Be(3);
        Catalog.All.Select(e => e.Id).Should().BeEquivalentTo(Topaz, Bytedance, Rife, Film, WanVace, LumaReframe);
        Catalog.Find(InferenceProviderCapability.VideoUpscale, WanVace).Should().BeNull("an outpainter is not an upscaler");
        Catalog.Find(InferenceProviderCapability.VideoUpscale, Rife).Should().BeNull("an interpolator is not an upscaler");
        Catalog.Resolve(InferenceProviderCapability.Chat, "").Should().BeNull();
    }

    [Fact]
    public void Every_price_row_matches_the_catalogue_tier_for_tier_and_nothing_else_is_priced()
    {
        foreach (VideoEnhanceEndpoint endpoint in Catalog.All)
        {
            if (endpoint.UsdPerSecondByTier is { } tiers)
            {
                foreach ((string tier, decimal usd) in tiers)
                {
                    ModelPrice row = ModelPriceCatalog.Priced.Single(p => p.Model == endpoint.Id + "#" + tier && p.Capability == endpoint.Capability);
                    row.UsdPerUnit.Should().ContainSingle().Which.Should().Be(new KeyValuePair<UsageKind, decimal>(UsageKind.VideoEnhanceSeconds, usd));
                    row.Basis.Should().Contain("read 2026-11-02");
                }
            }
            else if (endpoint.UsdPerSecondFlat is { } flat)
            {
                ModelPrice row = ModelPriceCatalog.Priced.Single(p => p.Model == endpoint.Id && p.Capability == endpoint.Capability);
                row.UsdPerUnit[UsageKind.VideoEnhanceSeconds].Should().Be(flat);
            }
            else
            {
                ModelPrice row = ModelPriceCatalog.Priced.Single(p => p.Model == endpoint.Id && p.Capability == endpoint.Capability);
                row.UsdPerUnit[UsageKind.VideoEnhanceComputeSeconds].Should().Be(endpoint.UsdPerComputeSecond!.Value);
            }
        }

        ModelPriceCatalog.All.Count(p => p.Capability is InferenceProviderCapability.VideoUpscale or InferenceProviderCapability.FrameInterpolation
                or InferenceProviderCapability.VideoOutpaint)
            .Should().Be(Catalog.All.Sum(e => e.UsdPerSecondByTier?.Count ?? 1), "no price row may exist for an endpoint or tier the client cannot call");
    }

    [Fact]
    public void Outpaint_is_priced_by_the_cited_wan_rows_and_the_luma_alternative_and_bills_video_seconds_at_16_fps()
    {
        VideoEnhanceEndpoint wan = Catalog.Find(InferenceProviderCapability.VideoOutpaint, WanVace)!;
        wan.UsdPerSecondByTier!.Should().Equal(new Dictionary<string, decimal> { ["720p"] = 0.08m, ["580p"] = 0.06m, ["480p"] = 0.04m });
        wan.MaxOutputFrames.Should().Be(241);
        VideoEnhanceCreditPricing.OutpaintRateFor(wan, 720)!.Value.Should().Be(("720p", 0.08m));
        VideoEnhanceCreditPricing.OutpaintRateFor(wan, 1080)!.Value.Should().Be(("720p", 0.08m), "the model never goes above 720p");
        VideoEnhanceCreditPricing.OutpaintRateFor(wan, 580)!.Value.Should().Be(("580p", 0.06m));
        VideoEnhanceCreditPricing.OutpaintRateFor(wan, 480)!.Value.Should().Be(("480p", 0.04m));

        // 24 fps x 10 s = 240 frames = 15 video seconds at 16 fps: 15 x 0.08.
        VideoEnhanceCreditPricing.OutpaintBilledSeconds(wan, 240, 10).Should().Be(15m);
        VideoEnhanceCreditPricing.EstimateOutpaintUsd(wan, 1280, 720, 240, 10).Should().Be(1.2m);
        VideoEnhanceCreditPricing.EstimateOutpaintUsd(wan, 1280, 720, 242, 10.1).Should().BeNull("over the 241-frame ceiling");
        VideoEnhanceCreditPricing.EstimateOutpaintUsd(wan, 1280, 720, 240, 121, 120).Should().BeNull("over the source cap");
        VideoEnhanceCreditPricing.WithinFrameCap(wan, 241).Should().BeTrue();
        VideoEnhanceCreditPricing.FrameCapSeconds(wan, 24)!.Value.Should().BeApproximately(241d / 24d, 1e-9);

        VideoEnhanceEndpoint luma = Catalog.Find(InferenceProviderCapability.VideoOutpaint, LumaReframe)!;
        luma.UsdPerSecondFlat.Should().Be(0.2m);
        VideoEnhanceCreditPricing.EstimateOutpaintUsd(luma, 1920, 1080, 300, 10).Should().Be(2.0m);
    }

    [Fact]
    public async Task A_source_under_the_wan_frame_floor_is_billed_as_the_floor_and_the_estimate_equals_the_hold()
    {
        VideoEnhanceEndpoint wan = Catalog.Find(InferenceProviderCapability.VideoOutpaint, WanVace)!;
        wan.MinOutputFrames.Should().Be(81);
        VideoEnhanceCreditPricing.AtLeastMinFrames(wan, 80).Should().BeFalse();
        VideoEnhanceCreditPricing.AtLeastMinFrames(wan, 81).Should().BeTrue();
        VideoEnhanceCreditPricing.MinFrameSeconds(wan, 30).Should().Be(3, "81 frames at 30 fps is 2.7 s, rounded up");

        // 30 frames bill as 81: ceil(81 / 16) = 6 video seconds, never ceil(30 / 16) = 2.
        VideoEnhanceCreditPricing.OutpaintBilledSeconds(wan, 30, 1).Should().Be(6m);
        VideoEnhanceCreditPricing.OutpaintBilledSeconds(wan, 81, 2.7).Should().Be(6m);
        VideoEnhanceCreditPricing.OutpaintBilledSeconds(wan, 240, 8).Should().Be(15m, "a long source is unchanged");
        decimal usd = VideoEnhanceCreditPricing.EstimateOutpaintUsd(wan, 1280, 720, 30, 1)!.Value;
        usd.Should().Be(6m * 0.08m);

        Mock<IInferenceProviderResolver> providers = new();
        providers.Setup(p => p.ResolveVideoOutpaintAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi,
                InferenceProviderCapability.VideoOutpaint, "", WanVace, "k", 60, AccountId: Guid.NewGuid()));
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));

        long? hold = await VideoEnhanceCreditPricing.TryHoldOutpaintMillisAsync(1280, 720, 30, 1, null, providers.Object, prices.Object);
        hold.Should().Be(6000, "the hold prices the floor, the same seconds the estimate bills");
    }

    [Fact]
    public async Task An_extend_step_hold_without_hints_is_sized_at_the_model_ceiling_not_unpriced()
    {
        Mock<IInferenceProviderResolver> providers = new();
        providers.Setup(p => p.ResolveVideoOutpaintAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAudioGenerationProvider(Guid.NewGuid(), "row", InferenceProviderKind.FalAi,
                InferenceProviderCapability.VideoOutpaint, "", WanVace, "k", 60, AccountId: Guid.NewGuid()));
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        const string json = """{"op":"Extend","engine":"Hosted","source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"},"allowSourceMediaEgress":true,"targetAspect":"1:1"}""";

        long? hold = await VideoEnhanceStepPricing.TryStepHoldMillisAsync(json, providers.Object, prices.Object);

        hold.Should().Be(16_000, "120 s at the reference rate is clamped to 241 frames: ceil(241 / 16) = 16 video seconds");
    }

    [Fact]
    public void The_cited_figures_are_the_vendors_and_high_frame_rate_is_exactly_double()
    {
        VideoEnhanceEndpoint topaz = Catalog.Find(InferenceProviderCapability.VideoUpscale, Topaz)!;
        topaz.UsdPerSecondByTier!.Should().Equal(new Dictionary<string, decimal>
        {
            ["720p"] = 0.01m,
            ["1080p"] = 0.02m,
            ["uhd"] = 0.08m,
            ["720p-hfr"] = 0.02m,
            ["1080p-hfr"] = 0.04m,
            ["uhd-hfr"] = 0.16m,
        });
        VideoEnhanceEndpoint bytedance = Catalog.Find(InferenceProviderCapability.VideoUpscale, Bytedance)!;
        bytedance.UsdPerSecondByTier!.Should().Equal(new Dictionary<string, decimal>
        {
            ["1080p"] = 0.0072m,
            ["uhd"] = 0.0288m,
            ["1080p-hfr"] = 0.0144m,
            ["uhd-hfr"] = 0.0576m,
        });
        foreach (VideoEnhanceEndpoint e in new[] { topaz, bytedance })
        {
            foreach (string tier in e.UsdPerSecondByTier!.Keys.Where(k => !k.EndsWith("-hfr", StringComparison.Ordinal)))
            {
                e.UsdPerSecondByTier[tier + "-hfr"].Should().Be(e.UsdPerSecondByTier[tier] * 2);
            }
        }

        Catalog.Find(InferenceProviderCapability.FrameInterpolation, Rife)!.UsdPerComputeSecond.Should().Be(0.0013m);
        Catalog.Find(InferenceProviderCapability.FrameInterpolation, Film)!.UsdPerComputeSecond.Should().Be(0.0013m);
    }

    [Fact]
    public void The_price_book_prefills_each_tier_and_an_unlisted_model_stays_unpriced()
    {
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoUpscale, Topaz)
            .Should().HaveCount(6)
            .And.Contain(s => s.Variant == "uhd-hfr" && s.CostUsdPerUnit == 0.16m && s.Unit == UsageKind.VideoEnhanceSeconds);
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.FrameInterpolation, Rife)
            .Should().ContainSingle(s => s.Unit == UsageKind.VideoEnhanceComputeSeconds && s.CostUsdPerUnit == 0.0013m && s.Variant == null);
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.VideoUpscale, "fal-ai/seedvr/upscale/video")
            .Should().BeEmpty("no cited price, so no row: unofferable, never a default");
    }

    [Fact]
    public void The_capability_table_and_admin_boundary_serve_video_enhance_through_fal_only()
    {
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.FalAi]
            .Should().Contain([InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation]);
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.VideoUpscale].Should().Equal(UsageKind.VideoEnhanceSeconds);
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.FrameInterpolation].Should().Equal(UsageKind.VideoEnhanceComputeSeconds);
        ProviderCapabilityUsage.RequiredCards().Should().Contain((InferenceProviderKind.FalAi, UsageKind.VideoEnhanceSeconds))
            .And.Contain((InferenceProviderKind.FalAi, UsageKind.VideoEnhanceComputeSeconds));

        foreach (InferenceProviderCapability capability in new[] { InferenceProviderCapability.VideoUpscale, InferenceProviderCapability.FrameInterpolation })
        {
            InferenceProvidersController.IsUnsupportedCombination(InferenceProviderKind.FalAi, capability, out string e).Should().BeFalse(e);
            foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>().Where(k => k != InferenceProviderKind.FalAi))
            {
                InferenceProvidersController.IsUnsupportedCombination(kind, capability, out _).Should().BeTrue(kind + " must not serve " + capability);
            }
        }
    }

    [Theory]
    [InlineData(InferenceProviderCapability.VideoUpscale, "", false)]
    [InlineData(InferenceProviderCapability.VideoUpscale, Topaz, false)]
    [InlineData(InferenceProviderCapability.VideoUpscale, Bytedance, false)]
    [InlineData(InferenceProviderCapability.VideoUpscale, Rife, true)]
    [InlineData(InferenceProviderCapability.VideoUpscale, "fal-ai/seedvr/upscale/video", true)]
    [InlineData(InferenceProviderCapability.FrameInterpolation, "", false)]
    [InlineData(InferenceProviderCapability.FrameInterpolation, Film, false)]
    [InlineData(InferenceProviderCapability.FrameInterpolation, Topaz, true)]
    public void The_model_boundary_refuses_an_uncatalogued_or_wrong_capability_model(InferenceProviderCapability capability, string model, bool refused)
    {
        InferenceProvidersController.IsUnsupportedModel(InferenceProviderKind.FalAi, capability, model, out _).Should().Be(refused);
    }

    [Theory]
    [InlineData(480, 30, "720p")]
    [InlineData(720, 30, "720p")]
    [InlineData(721, 30, "1080p")]
    [InlineData(1080, 30, "1080p")]
    [InlineData(1081, 30, "uhd")]
    [InlineData(2160, 30, "uhd")]
    [InlineData(720, 31, "720p-hfr")]
    [InlineData(1080, 60, "1080p-hfr")]
    [InlineData(2160, 50, "uhd-hfr")]
    [InlineData(1080, 30.0, "1080p")]
    public void The_tier_follows_the_output_short_side_and_doubles_above_30_fps(int shortSide, double fps, string tier)
    {
        VideoEnhanceCreditPricing.TierFor(shortSide, fps).Should().Be(tier);
    }

    [Fact]
    public void The_output_short_side_and_the_bytedance_frame_rate_are_computed_by_the_helpers()
    {
        VideoEnhanceCreditPricing.OutputShortSide(1920, 1080, 2).Should().Be(2160);
        VideoEnhanceCreditPricing.OutputShortSide(1080, 1920, 1.5).Should().Be(1620, "portrait uses the short side");
        VideoEnhanceCreditPricing.BytedanceTargetFps(29.97).Should().Be(30);
        VideoEnhanceCreditPricing.BytedanceTargetFps(12).Should().Be(24);
        VideoEnhanceCreditPricing.BytedanceTargetFps(240).Should().Be(120);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(5, 5)]
    [InlineData(5.001, 6)]
    [InlineData(120, 120)]
    public void Billed_seconds_round_up_to_a_whole_second(double seconds, int billed)
    {
        VideoEnhanceCreditPricing.BilledSeconds(seconds).Should().Be(billed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void A_non_positive_duration_cannot_be_billed(double seconds)
    {
        Action act = () => VideoEnhanceCreditPricing.BilledSeconds(seconds);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_upscale_estimate_is_price_times_billed_seconds_at_the_output_tier_and_the_cap_is_120_seconds_inclusive()
    {
        VideoEnhanceEndpoint topaz = Catalog.Find(InferenceProviderCapability.VideoUpscale, Topaz)!;

        // 960x540 x2 -> 1080p, 30 fps: 10 s x $0.02
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(topaz, 960, 540, 30, 10, 2).Should().Be(0.20m);
        // 1920x1080 x2 -> 2160 uhd, 60 fps: doubled to $0.16/s; 4.2 s bills 5 s
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(topaz, 1920, 1080, 60, 4.2, 2).Should().Be(0.80m);
        // 640x360 x2 -> 720p, 24 fps
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(topaz, 640, 360, 24, 120, 2).Should().Be(1.20m, "exactly 120 s is allowed");
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(topaz, 640, 360, 24, 120.5, 2).Should().BeNull("over the cap");
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(topaz, 640, 360, 24, 90, 2, maxSourceSeconds: 60).Should().BeNull("the cap is configurable");
        VideoEnhanceCreditPricing.DefaultMaxSourceSeconds.Should().Be(120);
        VideoEnhanceCreditPricing.MaxSourceSecondsKey.Should().Be("VideoEnhance:Hosted:MaxSourceSeconds");
    }

    [Fact]
    public void Bytedance_prices_the_forced_target_rate_and_has_no_720p_tier()
    {
        VideoEnhanceEndpoint bytedance = Catalog.Find(InferenceProviderCapability.VideoUpscale, Bytedance)!;

        // 1080p source x2 -> uhd; a 60 fps source is asked for 60 fps -> doubled $0.0576/s
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(bytedance, 1920, 1080, 60, 10, 2).Should().Be(0.576m);
        // a 25 fps source stays at 25 -> standard uhd $0.0288/s
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(bytedance, 1920, 1080, 25, 10, 2).Should().Be(0.288m);
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(bytedance, 640, 360, 25, 10, 2).Should().BeNull("720p output has no bytedance price: fail closed");
    }

    [Fact]
    public void Bytedance_fails_closed_above_the_cited_resolution_and_frame_rate()
    {
        VideoEnhanceEndpoint bytedance = Catalog.Find(InferenceProviderCapability.VideoUpscale, Bytedance)!;

        VideoEnhanceCreditPricing.UpscaleTierFor(bytedance, 2160, 30).Should().Be("uhd", "4K is the top cited tier");
        VideoEnhanceCreditPricing.UpscaleTierFor(bytedance, 2161, 30).Should().BeNull("there is no 8K card");
        VideoEnhanceCreditPricing.UpscaleTierFor(bytedance, 2160, 60).Should().Be("uhd-hfr");
        VideoEnhanceCreditPricing.UpscaleTierFor(bytedance, 1080, 61).Should().BeNull("only 60 fps doubling is cited");
        VideoEnhanceCreditPricing.UpscaleTierFor(bytedance, 1080, 120).Should().BeNull();
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(bytedance, 3840, 2160, 30, 10, 2).Should().BeNull("4320 short side");
        VideoEnhanceCreditPricing.EstimateUpscaleUsd(bytedance, 1920, 1080, 90, 10, 1.5).Should().BeNull();
        // Topaz's top tier is open-ended ("above 1080p"), so it is unaffected.
        VideoEnhanceCreditPricing.UpscaleTierFor(Catalog.Find(InferenceProviderCapability.VideoUpscale, Topaz)!, 4320, 30).Should().Be("uhd");
    }

    [Fact]
    public void The_factory_binds_the_poll_timeout_and_the_source_cap_from_configuration()
    {
        new VideoEnhanceClientFactory().PollTimeout.Should().Be(TimeSpan.FromMinutes(30));
        Microsoft.Extensions.Configuration.IConfiguration config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [VideoEnhanceCreditPricing.PollTimeoutMinutesKey] = "7" }).Build();
        new VideoEnhanceClientFactory(config).PollTimeout.Should().Be(TimeSpan.FromMinutes(7));
        Microsoft.Extensions.Configuration.IConfiguration bad = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [VideoEnhanceCreditPricing.PollTimeoutMinutesKey] = "0" }).Build();
        new VideoEnhanceClientFactory(bad).PollTimeout.Should().Be(TimeSpan.FromMinutes(30), "a non-positive value keeps the default");
    }

    [Fact]
    public void The_compute_second_hold_comes_from_output_frames_scaled_by_pixels_and_rounds_up()
    {
        // 10 s at 30 fps = 300 source frames, x2 = 600 output frames; 1080p; 0.05 s per frame = 30 s
        VideoEnhanceCreditPricing.ComputeSecondsHold(VideoEnhanceCreditPricing.OutputFrames(300, 2), 1920, 1080).Should().Be(30m);
        // 4x doubles it
        VideoEnhanceCreditPricing.ComputeSecondsHold(VideoEnhanceCreditPricing.OutputFrames(300, 4), 1920, 1080).Should().Be(60m);
        // 4K is 4x the pixels of 1080p
        VideoEnhanceCreditPricing.ComputeSecondsHold(600, 3840, 2160).Should().Be(120m);
        // 720p is 4/9 of 1080p: 600 x 0.05 x 4/9 = 13.33 -> 14
        VideoEnhanceCreditPricing.ComputeSecondsHold(600, 1280, 720).Should().Be(14m, "an estimate is rounded up, never down");
        // the per-frame rate is the setting
        VideoEnhanceCreditPricing.ComputeSecondsHold(600, 1920, 1080, 0.1m).Should().Be(60m);
        VideoEnhanceCreditPricing.DefaultComputeSecondsPerOutputFrame.Should().Be(0.05m);
        VideoEnhanceCreditPricing.ComputeSecondsPerOutputFrameKey.Should().Be("VideoEnhance:Hosted:ComputeSecondsPerOutputFrame");
        VideoEnhanceCreditPricing.PollTimeoutMinutesKey.Should().Be("VideoEnhance:Hosted:PollTimeoutMinutes");
        VideoEnhanceCreditPricing.DefaultPollTimeoutMinutes.Should().Be(30);

        Action zero = () => VideoEnhanceCreditPricing.ComputeSecondsHold(0, 1920, 1080);
        zero.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_interpolation_estimate_is_the_held_compute_seconds_at_the_compute_second_price()
    {
        VideoEnhanceEndpoint rife = Catalog.Find(InferenceProviderCapability.FrameInterpolation, Rife)!;

        VideoEnhanceCreditPricing.EstimateInterpolationUsd(rife, 1920, 1080, 300, 2, 10).Should().Be(30m * 0.0013m);
        VideoEnhanceCreditPricing.EstimateInterpolationUsd(rife, 1920, 1080, 300, 2, 121).Should().BeNull("over the 120 s cap");
        VideoEnhanceCreditPricing.EstimateInterpolationUsd(Catalog.Find(InferenceProviderCapability.VideoUpscale, Topaz)!, 1920, 1080, 300, 2, 10)
            .Should().BeNull("an upscaler has no compute-second price");
    }

    private static ResolvedAudioGenerationProvider Row(InferenceProviderCapability capability, string model) =>
        new(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, capability, "", model, "test", 60, AccountId: Guid.NewGuid());

    private static (Mock<IInferenceProviderResolver>, Mock<IModelPriceService>, List<PriceRequest>) Wire(
        ResolvedAudioGenerationProvider? upscale, ResolvedAudioGenerationProvider? interpolation = null)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(upscale);
        resolver.Setup(r => r.ResolveFrameInterpolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(interpolation);
        List<PriceRequest> asked = [];
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        return (resolver, prices, asked);
    }

    [Fact]
    public async Task The_upscale_hold_prices_whole_output_seconds_under_the_tier_variant()
    {
        ResolvedAudioGenerationProvider row = Row(InferenceProviderCapability.VideoUpscale, Topaz);
        var (resolver, prices, asked) = Wire(row);

        long? hold = await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(1920, 1080, 60, 4.2, 2, null, resolver.Object, prices.Object);

        PriceRequest request = asked.Should().ContainSingle().Which;
        request.Unit.Should().Be(UsageKind.VideoEnhanceSeconds);
        request.Capability.Should().Be(InferenceProviderCapability.VideoUpscale);
        request.ModelCode.Should().Be(Topaz);
        request.Variant.Should().Be("uhd-hfr");
        request.Quantity.Should().Be(5m);
        request.ModelCapabilityId.Should().Be(row.ProviderId);
        hold.Should().Be(5_000);
    }

    [Fact]
    public async Task The_interpolation_hold_prices_the_compute_seconds_under_the_interpolation_capability()
    {
        ResolvedAudioGenerationProvider row = Row(InferenceProviderCapability.FrameInterpolation, Rife);
        var (resolver, prices, asked) = Wire(null, row);

        long? hold = await VideoEnhanceCreditPricing.TryHoldInterpolationMillisAsync(1920, 1080, 300, 2, 10, null, resolver.Object, prices.Object);

        PriceRequest request = asked.Should().ContainSingle().Which;
        request.Unit.Should().Be(UsageKind.VideoEnhanceComputeSeconds);
        request.Capability.Should().Be(InferenceProviderCapability.FrameInterpolation);
        request.ModelCode.Should().Be(Rife);
        request.Variant.Should().BeNull();
        request.Quantity.Should().Be(30m);
        hold.Should().Be(30_000);
    }

    [Fact]
    public async Task Over_the_cap_no_provider_an_unknown_model_an_unpriced_tier_or_an_unpriced_row_all_answer_null()
    {
        var (resolver, prices, asked) = Wire(Row(InferenceProviderCapability.VideoUpscale, Topaz));
        (await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(960, 540, 30, 121, 2, null, resolver.Object, prices.Object)).Should().BeNull();
        asked.Should().BeEmpty("the cap is checked before any price lookup");

        var (none, p2, _) = Wire(null);
        (await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(960, 540, 30, 10, 2, null, none.Object, p2.Object)).Should().BeNull();
        (await VideoEnhanceCreditPricing.TryHoldInterpolationMillisAsync(960, 540, 300, 2, 10, null, none.Object, p2.Object)).Should().BeNull();

        var (unknown, p3, _) = Wire(Row(InferenceProviderCapability.VideoUpscale, "fal-ai/seedvr/upscale/video"));
        (await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(960, 540, 30, 10, 2, null, unknown.Object, p3.Object)).Should().BeNull();

        var (bytedance, p4, _) = Wire(Row(InferenceProviderCapability.VideoUpscale, Bytedance));
        (await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(640, 360, 30, 10, 2, null, bytedance.Object, p4.Object))
            .Should().BeNull("a 720p bytedance output has no price");

        var (badMultiplier, p5, _) = Wire(null, Row(InferenceProviderCapability.FrameInterpolation, Rife));
        (await VideoEnhanceCreditPricing.TryHoldInterpolationMillisAsync(960, 540, 300, 3, 10, null, badMultiplier.Object, p5.Object)).Should().BeNull();

        var (priced, _, _) = Wire(Row(InferenceProviderCapability.VideoUpscale, Topaz));
        Mock<IModelPriceService> unpriced = new();
        unpriced.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnpricedUsageException(UsageKind.VideoEnhanceSeconds, InferenceProviderKind.FalAi, Topaz));
        (await VideoEnhanceCreditPricing.TryHoldUpscaleMillisAsync(960, 540, 30, 10, 2, null, priced.Object, unpriced.Object))
            .Should().BeNull("unpriced is unofferable, never free");
    }
}
