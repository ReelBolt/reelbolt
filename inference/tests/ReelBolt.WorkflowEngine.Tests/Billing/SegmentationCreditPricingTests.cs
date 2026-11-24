#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Text-prompted segmentation's credit hold (initiative "prompted region effects", plan §6): a
/// <see cref="StepType.VideoAnalyze"/> step with <c>Method = Segmentation</c> targets buys a hosted
/// vendor call billed per 16 frames of video input, which the token-shaped row cannot see. The hold
/// must grow by the worst case the step is allowed to buy, must never shrink anything, and must be
/// unchanged for every step that predates the feature.
/// </summary>
public sealed class SegmentationCreditPricingTests
{
    /// <summary>
    /// The figure the ticket documents: 60 s at the worst-case 30 fps is 1,800 frames, which is 113
    /// sixteen-frame units, at $0.01 each = $1.13 of vendor cost = 188.371 credits = 188,371
    /// millicredits.
    /// </summary>
    private const long OneTargetOnTheDefaultCapMillis = 188_371;

    // -----------------------------------------------------------------
    // The arithmetic (pure)
    // -----------------------------------------------------------------

    [Fact]
    public void The_unit_price_is_the_vendors_figure_converted_to_credits_and_rounded_up()
    {
        SegmentationCreditPricing.UsdPerFrameUnit.Should().Be(0.01m);
        SegmentationCreditPricing.VendorFrameUnit.Should().Be(16);

        // $0.01 / $0.006 per credit = 1.6666... credits = 1,666.67 millicredits, rounded UP so a
        // hold can never be undercut by a billing event that does not round.
        SegmentationCreditPricing.FrameUnitMillis.Should().Be(1_667);
        CreditCurrency.CreditsFromUsd(0.01m).Should().Be(1.6666666666666666666666666667m);
    }

    [Fact]
    public void The_price_has_one_home_so_the_hold_and_the_rate_card_cannot_drift()
    {
        // The house rule: a vendor price lives in ModelPriceCatalog, keyed by usage kind. This asserts the
        // helper READS it rather than restating it, so editing the number moves the hold and the invoice
        // together or not at all.
        List<ModelPrice> rows = [.. ModelPriceCatalog.All.Where(p =>
            p.ProviderKind == InferenceProviderKind.FalAi
            && p.Capability == InferenceProviderCapability.VideoSegmentation
            && p.Model == SegmentationCreditPricing.DefaultModel)];

        rows.Should().ContainSingle("one endpoint is priced, and it is the one the helper prices");
        ModelPrice row = rows[0];
        row.IsPriced.Should().BeTrue();
        row.Basis.Should().Contain("$0.01 per 16 frames").And.Contain("2026-10-18");

        decimal usdPerFrame = row.UsdPerUnit[UsageKind.VideoSegmentationFrames];
        usdPerFrame.Should().Be(ModelPriceCatalog.FalVideoSegmentationUsdPerFrame);
        (usdPerFrame * ModelPriceCatalog.FalVideoSegmentationFramesPerUnit).Should()
            .Be(ModelPriceCatalog.FalVideoSegmentationUsdPerUnit,
                "the card is the vendor's block sentence divided by its own block size");

        SegmentationCreditPricing.UsdPerFrameUnit.Should().Be(ModelPriceCatalog.FalVideoSegmentationUsdPerUnit);
        SegmentationCreditPricing.VendorFrameUnit.Should().Be(ModelPriceCatalog.FalVideoSegmentationFramesPerUnit);
        SegmentationCreditPricing.FrameUnitMillis.Should()
            .Be((long)decimal.Ceiling(
                CreditCurrency.CreditsFromUsd(usdPerFrame * ModelPriceCatalog.FalVideoSegmentationFramesPerUnit) * 1000m),
                "one billed 16-frame block costs what the catalog's row says it costs");

        // The hold and the meter round the SAME frames into the SAME blocks: two implementations of the
        // vendor's 16-frame rule that a change to one would otherwise silently unbalance.
        VideoSegmentationBilling.FramesPerUnit.Should().Be(SegmentationCreditPricing.VendorFrameUnit);
        SegmentationCreditPricing.FrameUnitsFor(60)
            .Should().Be(VideoSegmentationBilling.UnitsFor(60 * SegmentationCreditPricing.AssumedMaxFps));
    }

    [Fact]
    public void Frame_units_round_up_over_the_vendors_sixteen_frame_blocks()
    {
        SegmentationCreditPricing.FrameUnitsFor(0).Should().Be(0);
        SegmentationCreditPricing.FrameUnitsFor(1).Should().Be(2, "1 s at 30 fps is 30 frames = 2 units");
        SegmentationCreditPricing.FrameUnitsFor(60).Should().Be(113, "1,800 frames / 16 = 112.5, rounded up");
        SegmentationCreditPricing.FrameUnitsFor(600).Should().Be(1_125, "the 600 s ceiling: 18,000 frames / 16");
    }

    [Fact]
    public void One_target_on_the_default_cap_adds_the_documented_amount()
    {
        SegmentationCreditPricing.AddOnMillis(1, SegmentationCreditPricing.DefaultMaxSegmentationSeconds)
            .Should().Be(OneTargetOnTheDefaultCapMillis);

        SegmentationCreditPricing.AddOnMillis(1, 60).Should().Be(113 * 1_667);
    }

    [Fact]
    public void Zero_targets_add_nothing_whatever_the_cap_is()
    {
        SegmentationCreditPricing.AddOnMillis(0, 60).Should().Be(0);
        SegmentationCreditPricing.AddOnMillis(0, 600).Should().Be(0);
        SegmentationCreditPricing.AddOnMillis(null).Should().Be(0);
        SegmentationCreditPricing.AddOnMillis("").Should().Be(0);
        SegmentationCreditPricing.AddOnMillis("{}").Should().Be(0);
    }

    [Fact]
    public void The_hold_grows_with_targets_and_with_the_cap_and_never_shrinks()
    {
        long one = SegmentationCreditPricing.AddOnMillis(1, 60);
        long two = SegmentationCreditPricing.AddOnMillis(2, 60);
        long longer = SegmentationCreditPricing.AddOnMillis(1, 600);

        two.Should().Be(2 * one);
        longer.Should().BeGreaterThan(one);
    }

    // -----------------------------------------------------------------
    // Reading the persisted step config
    // -----------------------------------------------------------------

    [Fact]
    public void Only_labels_whose_method_is_segmentation_are_priced()
    {
        const string config = """
            {
              "trackObjects": [
                { "label": "speedometer", "method": "Segmentation" },
                { "label": "licence plate" },
                { "label": "the red car" },
                { "label": "  ", "method": "Segmentation" },
                { "label": "face", "method": "segmentation" }
              ],
              "maxSegmentationSeconds": 30
            }
            """;

        (int targets, int seconds) = SegmentationCreditPricing.Shape(config);

        targets.Should().Be(2, "two of the five targets ask for segmentation and carry a label; the default method is Feature");
        seconds.Should().Be(30);
        SegmentationCreditPricing.AddOnMillis(config).Should().Be(2 * SegmentationCreditPricing.FrameUnitsFor(30) * 1_667);
    }

    [Fact]
    public void A_config_with_no_cap_reads_the_steps_own_default()
    {
        const string config = """{ "trackObjects": [ { "label": "speedometer", "method": "Segmentation" } ] }""";

        SegmentationCreditPricing.Shape(config).MaxSeconds.Should().Be(SegmentationCreditPricing.DefaultMaxSegmentationSeconds);
        SegmentationCreditPricing.AddOnMillis(config).Should().Be(OneTargetOnTheDefaultCapMillis);
    }

    [Fact]
    public void An_unreadable_or_odd_config_never_throws_and_never_prices()
    {
        SegmentationCreditPricing.AddOnMillis("not json at all").Should().Be(0);
        SegmentationCreditPricing.AddOnMillis("[1,2,3]").Should().Be(0);
        SegmentationCreditPricing.AddOnMillis("""{ "trackObjects": "speedometer" }""").Should().Be(0);
        SegmentationCreditPricing.AddOnMillis("""{ "trackObjects": [ { "method": "Segmentation" } ] }""").Should().Be(0);
    }

    // -----------------------------------------------------------------
    // The run plan the API's preflight and the engine's reservation share
    // -----------------------------------------------------------------

    [Fact]
    public void The_run_plans_analyze_hold_grows_by_exactly_the_add_on()
    {
        Guid agent = Guid.NewGuid();
        WorkflowStep[] withTarget = [Analyze(1, agent, SegmentationConfig(1, 60))];
        WorkflowStep[] withoutTarget = [Analyze(1, agent, SegmentationConfig(0, 60))];

        RunHoldPlan grown = Plan(withTarget);
        RunHoldPlan plain = Plan(withoutTarget);

        grown.HoldsMillis[0].Should().Be(plain.HoldsMillis[0] + OneTargetOnTheDefaultCapMillis);
    }

    [Fact]
    public void A_step_that_prices_no_segmentation_keeps_its_hold_byte_for_byte()
    {
        Guid agent = Guid.NewGuid();
        WorkflowStep[] legacy = [Analyze(1, agent, null)];
        WorkflowStep[] featureOnly = [Analyze(1, agent, SegmentationConfig(0, 60))];

        Plan(legacy).HoldsMillis[0].Should().Be(Plan(featureOnly).HoldsMillis[0]);
        Plan(legacy).HoldsMillis[0].Should().Be(StepCreditDefaults.MillisFor(StepType.VideoAnalyze).Low);
    }

    [Fact]
    public void Other_step_types_are_never_priced_for_segmentation()
    {
        // The config field belongs to the analyze step; a compile step carrying the same JSON (which it
        // never should) must not move its hold.
        WorkflowStep[] steps =
        [
            new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoCompile,
                AgentDefinitionId = Guid.NewGuid(),
                VideoAnalyzeConfigJson = SegmentationConfig(2, 60)
            }
        ];

        Plan(steps).HoldsMillis[0].Should().Be(StepCreditDefaults.MillisFor(StepType.VideoCompile).Low);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static RunHoldPlan Plan(IReadOnlyList<WorkflowStep> steps)
    {
        Dictionary<Guid, AgentType> types = [];
        foreach (WorkflowStep step in steps)
        {
            if (step.AgentDefinitionId is { } id) types[id] = AgentType.Custom;
        }

        return RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, types, SeedProfilePricer.Instance);
    }

    private static WorkflowStep Analyze(int order, Guid agent, string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.VideoAnalyze,
        AgentDefinitionId = agent,
        VideoAnalyzeConfigJson = configJson
    };

    private static string SegmentationConfig(int targets, int maxSeconds)
    {
        List<string> entries = [.. Enumerable.Range(0, targets)
            .Select(i => $$"""{ "label": "object {{i}}", "method": "Segmentation" }""")];
        return $$"""{ "trackObjects": [{{string.Join(", ", entries)}}], "maxSegmentationSeconds": {{maxSeconds}} }""";
    }
}
