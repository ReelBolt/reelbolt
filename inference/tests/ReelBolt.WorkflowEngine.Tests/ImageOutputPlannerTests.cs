#nullable enable
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

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The output-size planner, the config rules around it, and the tier-priced hold (offline).</summary>
public sealed class ImageOutputPlannerTests
{
    private const string Nano = "google/nano-banana-2.1";
    private const string NanoEdit = "google/nano-banana-2.1/edit";
    private const string Ideogram = "ideogram/v4.5";

    private static ImageGenerateStepConfig Gen(int? w = null, int? h = null, string? resolution = null) =>
        new(ImageGenerateMode.Generate, "x", OutputWidth: w, OutputHeight: h, Resolution: resolution);

    private static ImageGenerateStepConfig Edit(int? w = null, int? h = null, string? resolution = null) =>
        new(ImageGenerateMode.Edit, Instruction: "x", Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput),
            AllowSourceMediaEgress: true, OutputWidth: w, OutputHeight: h, Resolution: resolution);

    private static ImageOutputPlan Plan(ImageGenerateStepConfig c, string model) =>
        ImageOutputPlanner.PlanFor(c, InferenceProviderKind.FalAi, model);

    [Theory]
    [InlineData(2000, 2000, "2K", "1:1")]
    [InlineData(1024, 1024, "1K", "1:1")]
    [InlineData(1025, 1025, "2K", "1:1")]
    [InlineData(1920, 1080, "2K", "16:9")]
    [InlineData(1080, 1350, "2K", "4:5")]
    [InlineData(4096, 4096, "4K", "1:1")]
    [InlineData(3000, 2000, "4K", "3:2")]
    [InlineData(64, 64, "1K", "1:1")]
    [InlineData(100, 64, "1K", "3:2")]
    [InlineData(8192, 8192, "4K", "1:1")]
    [InlineData(1000, 1010, "1K", "1:1")]
    [InlineData(2400, 1000, "4K", "21:9")]
    public void Nano_picks_the_smallest_covering_tier_and_the_nearest_aspect(int w, int h, string tier, string aspect)
    {
        foreach (string model in new[] { Nano, NanoEdit })
        {
            ImageOutputPlan plan = Plan(model == Nano ? Gen(w, h) : Edit(w, h), model);
            plan.Tier.Should().Be(tier);
            plan.AspectRatio.Should().Be(aspect);
            (plan.RequestWidth, plan.RequestHeight).Should().Be((null, null));
            (plan.TargetWidth, plan.TargetHeight).Should().Be((w, h));
            plan.HasTarget.Should().BeTrue();
        }
    }

    [Fact]
    public void An_explicit_resolution_wins_over_the_smallest_covering_tier()
    {
        Plan(Gen(1000, 1000, "4k"), Nano).Tier.Should().Be("4K");
    }

    [Fact]
    public void Resolution_alone_sets_the_tier_for_generate_and_edit_without_a_target()
    {
        foreach (ImageOutputPlan plan in new[] { Plan(Gen(resolution: "2k"), Nano), Plan(Edit(resolution: "2K"), NanoEdit) })
        {
            plan.Tier.Should().Be("2K");
            plan.HasTarget.Should().BeFalse();
            plan.AspectRatio.Should().BeNull();
        }
    }

    [Fact]
    public void No_output_fields_plans_nothing_so_behaviour_is_unchanged()
    {
        ImageOutputPlan plan = Plan(new ImageGenerateStepConfig(ImageGenerateMode.Generate, "x", AspectRatio: "16:9"), Nano);
        plan.Should().Be(new ImageOutputPlan(null, "16:9", null, null, null, null));
    }

    [Theory]
    [InlineData(2000, 2000, 2048, 2048)]
    [InlineData(1920, 1080, 2560, 1440)]
    [InlineData(1024, 1024, 1024, 1024)]
    [InlineData(1000, 1000, 1024, 1024)]
    [InlineData(4096, 4096, 2048, 2048)]
    [InlineData(1080, 1350, 1792, 2240)]
    [InlineData(5000, 5000, 2048, 2048)]
    [InlineData(64, 64, 1024, 1024)]
    public void Ideogram_sends_the_smallest_listed_size_covering_the_target_at_the_nearest_aspect(int w, int h, int sw, int sh)
    {
        ImageOutputPlan plan = Plan(Gen(w, h), Ideogram);
        (plan.RequestWidth, plan.RequestHeight).Should().Be((sw, sh));
        plan.AspectRatio.Should().BeNull();
        plan.Tier.Should().BeNull("ideogram is priced by quality, not size");
        (plan.TargetWidth, plan.TargetHeight).Should().Be((w, h));
    }

    [Fact]
    public void Ideogram_edit_plans_no_explicit_size_and_a_resolution_word_is_ignored()
    {
        ImageOutputPlan plan = Plan(Gen(1000, 1000, "2K"), Ideogram);
        plan.Tier.Should().BeNull();
    }

    [Fact]
    public void An_unknown_model_plans_a_pass_through_with_the_target()
    {
        ImageOutputPlan plan = ImageOutputPlanner.PlanFor(Gen(2000, 2000), InferenceProviderKind.FalAi, "nope/unknown");
        plan.Should().Be(new ImageOutputPlan(null, null, null, null, 2000, 2000));
    }

    [Fact]
    public void NearestSize_ties_pick_the_smaller_area_and_the_largest_when_none_covers()
    {
        ImageOutputPlanner.NearestSize(["100x100", "200x200"], 150, 150).Should().Be((200, 200));
        ImageOutputPlanner.NearestSize(["100x100", "200x200"], 500, 500).Should().Be((200, 200));
        ImageOutputPlanner.NearestSize(["100x100", "200x200"], 50, 50).Should().Be((100, 100));
        ImageOutputPlanner.NearestSize([], 50, 50).Should().BeNull();
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Valid_output_configs_pass()
    {
        Gen(2000, 2000).Validate().Should().BeEmpty();
        Gen(2000, 2000, "2k").Validate().Should().BeEmpty();
        Gen(resolution: "4K").Validate().Should().BeEmpty();
        Edit(resolution: "1K").Validate().Should().BeEmpty();
        Edit(64, 8192).Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData(2000, null)]
    [InlineData(null, 2000)]
    [InlineData(63, 100)]
    [InlineData(100, 8193)]
    public void Bad_output_sizes_are_refused(int? w, int? h)
    {
        Gen(w, h).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Output_size_does_not_combine_with_the_other_size_fields()
    {
        (Gen(1000, 1000) with { AspectRatio = "1:1" }).Validate().Should().Contain(e => e.Contains("one way"));
        (Gen(1000, 1000) with { Width = 1024, Height = 1024 }).Validate().Should().Contain(e => e.Contains("one way"));
    }

    [Fact]
    public void Resolution_must_be_a_tier_word_and_cover_the_output_size()
    {
        Gen(resolution: "8K").Validate().Should().Contain("Resolution must be one of 1K, 2K or 4K.");
        Gen(resolution: "").Validate().Should().NotBeEmpty();
        Gen(3000, 2000, "2K").Validate().Should().ContainSingle().Which.Should().Contain("use 4K");
        Gen(1500, 1000, "1K").Validate().Should().ContainSingle().Which.Should().Contain("use 2K");
    }

    // ---------------------------------------------------------------- pricing

    [Fact]
    public async Task The_hold_and_the_unit_price_follow_the_planned_tier_times_the_picture_count()
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        var row = new ResolvedImageProvider(System.Guid.NewGuid(), "row", InferenceProviderKind.FalAi,
            InferenceProviderCapability.ImageGeneration, "", Nano, "test", 60, IsPlatformManaged: true, AccountId: System.Guid.NewGuid());
        resolver.Setup(r => r.ResolveImageGenerationAsync(It.IsAny<System.Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
        List<PriceRequest> asked = [];
        var prices = new Mock<IModelPriceService>();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Returns((PriceRequest r, CancellationToken _) =>
            {
                asked.Add(r);
                decimal usd = r.Variant switch { "1K" => 0.040m, "2K" => 0.059m, "4K" => 0.134m, _ => 9m };
                return new System.Threading.Tasks.ValueTask<PriceQuote>(new PriceQuote((long)(usd * 200_000m), usd, System.Guid.NewGuid()));
            });

        // 2000x2000 -> 2K; 3 items x 2 variations = 6 pictures.
        long? hold = await ImageGenerateCreditPricing.TryHoldMillisAsync(
            """{"mode":"Generate","prompt":"a {item}","itemList":"red, blue, green","variations":2,"outputWidth":2000,"outputHeight":2000}""",
            resolver.Object, prices.Object);

        asked.Should().ContainSingle().Which.Variant.Should().Be("2K");
        hold.Should().Be(11_800 * 6);

        asked.Clear();
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(
            """{"mode":"Generate","prompt":"x","resolution":"4k","variations":2}""", resolver.Object, prices.Object)).Should().Be(26_800 * 2);
        asked.Single().Variant.Should().Be("4K");

        asked.Clear();
        (await ImageGenerateCreditPricing.TryHoldMillisAsync(
            """{"mode":"Generate","prompt":"x"}""", resolver.Object, prices.Object)).Should().Be(8_000, "no output fields keeps the vendor's 1K default");
        asked.Single().Variant.Should().Be("1K");
    }

    [Theory]
    [InlineData("1K", 0.040, 0.044)]
    [InlineData("2K", 0.059, 0.063)]
    [InlineData("4K", 0.134, 0.138)]
    public void Billing_key_carries_the_planned_tier(string tier, double generate, double edit)
    {
        ImageGenerateCreditPricing.BillingKey(InferenceProviderKind.FalAi, Nano, tier).Should().Be(Nano + "#" + tier);
        ImageGenerateCreditPricing.BillingKey(InferenceProviderKind.FalAi, NanoEdit, null).Should().Be(NanoEdit + "#1K");
        generate.Should().BeLessThan(edit);
    }
}
