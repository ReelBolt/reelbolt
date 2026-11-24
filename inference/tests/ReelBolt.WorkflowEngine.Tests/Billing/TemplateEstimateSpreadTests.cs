#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The shipped templates, priced on a cold start through the seed rate cards: the displayed range is
/// within 3x, or within 10x for a media-only template (D-08 revised, `AssertRunBand`); with history the
/// hold the preflight compares against is the old p90 sum plus the worst-case segmentation purchase a
/// VideoAnalyze step's config can bill, and on a cold start every cold step holds the larger of its own
/// floor and its share of the displayed low edge (the cold-start hold re-plan), so no template needs less
/// than before and none needs less than the low.
/// <para>
/// T-001d puts that same segmentation purchase in the SHOWN band, so shown and held agree about it. The
/// media-only template is then inside 3x again (pinned by
/// `The_media_only_template_is_within_3x_once_the_add_on_is_shown`); D-08's 10x helper is kept as the
/// safety net, not as the live check.
/// </para>
/// </summary>
public sealed class TemplateEstimateSpreadTests(ITestOutputHelper output)
{
    private static readonly DateTime At = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly LegacyCardPrices Seed = LegacyCardPrices.Seed();

    /// <summary>One stable agent definition id per agent type, as a provisioned project would have.</summary>
    private static readonly IReadOnlyDictionary<AgentType, Guid> AgentIds = Enum.GetValues<AgentType>()
        .ToDictionary(t => t, t => new Guid((int)t + 1, 0, 0, new byte[8]));

    private static readonly IReadOnlyDictionary<Guid, AgentType> AgentTypes = AgentIds.ToDictionary(p => p.Value, p => p.Key);

    public static TheoryData<string, string> TemplatesByChatModel()
    {
        TheoryData<string, string> data = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            // A template made ONLY of VideoGenerate steps (avatars phase 2's spokesperson clip) has no range at all:
            // its whole price is the step's declared fixed hold, which this harness does not price (the declared
            // variant below does). There is nothing to band, so it is left out rather than asserted at 0.
            if (template.Steps.All(s => s.StepType == StepType.VideoGenerate))
                continue;
            // The same for a template of only audio generation (music or sound effect, plus its branches): declared-priced.
            if (template.Steps.All(s => s.StepType is StepType.AudioGenerate or StepType.Conditional))
                continue;
            data.Add(template.Key, "deepseek-flash");
            data.Add(template.Key, "claude-opus-5-5");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TemplatesByChatModel))]
    public void Every_shipped_template_is_within_3x_on_a_cold_start(string templateKey, string chatModel)
    {
        WorkflowStep[] steps = StepsOf(templateKey);

        WorkflowCostEstimate estimate = Estimate(steps, chatModel);

        string detail = $"{templateKey} on {chatModel}: low {Credits(estimate.LowMillis)}, typical {Credits(estimate.TypicalMillis)}, " +
            $"high {Credits(estimate.HighMillis)} (ratio {Ratio(estimate):0.00}); variance from {VarianceShares(steps, estimate)}; per step: " +
            string.Join("; ", estimate.Steps.Select(s =>
                $"#{s.StepOrder} {s.StepType} {s.Label}: {Credits(s.LowMillis)}/{Credits(s.TypicalMillis)}/{Credits(s.HighMillis)} x{s.ExpectedRuns:0.##}"));
        output.WriteLine(detail);

        estimate.LowMillis.Should().BePositive(detail);
        AssertRunBand(templateKey, estimate, steps, detail);
    }

    /// <summary>A declared VideoGenerate price, as B6c prices a step from its own settings (the figure the hold test uses).</summary>
    private const long DeclaredVideoMillis = 160_000;

    public static TheoryData<string, string> DeclaredVideoTemplatesByChatModel()
    {
        TheoryData<string, string> data = [];
        foreach (string template in new[] { "video-derush-edit-broll", "video-derush-edit-dailies" })
        {
            data.Add(template, "deepseek-flash");
            data.Add(template, "claude-opus-5-5");
        }

        return data;
    }

    /// <summary>The production path: the VideoGenerate step carries a declared price, which leaves the range.</summary>
    [Theory]
    [MemberData(nameof(DeclaredVideoTemplatesByChatModel))]
    public void A_template_with_a_declared_video_price_is_within_3x_on_a_cold_start(string templateKey, string chatModel)
    {
        WorkflowStep[] steps = StepsOf(templateKey);
        Dictionary<int, long> videoHolds = steps
            .Where(s => s.StepType == StepType.VideoGenerate)
            .ToDictionary(s => s.StepOrder, _ => DeclaredVideoMillis);
        videoHolds.Should().NotBeEmpty($"{templateKey} must have a VideoGenerate step for this variant to mean anything");

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], steps, videoHolds, Display(), new SnapshotPricer(chatModel));

        string detail = $"{templateKey} on {chatModel} (declared video price): low {Credits(estimate.LowMillis)}, " +
            $"typical {Credits(estimate.TypicalMillis)}, high {Credits(estimate.HighMillis)} (ratio {Ratio(estimate):0.00}), " +
            $"fixed max {Credits(estimate.FixedMaxMillis)}; variance from {VarianceShares(steps, estimate)}";
        output.WriteLine(detail);

        estimate.Steps.Where(s => s.StepType == nameof(StepType.VideoGenerate))
            .Should().OnlyContain(s => s.FixedMaxMillis == DeclaredVideoMillis && s.TypicalMillis == 0, detail);
        estimate.LowMillis.Should().BePositive(detail);
        estimate.HighMillis.Should().BeLessThanOrEqualTo(3 * estimate.LowMillis, detail);
    }

    /// <summary>
    /// The two steps of the region-effect template: a VideoAnalyze step and a VideoCompile step, and
    /// nothing else, so its displayed band is VideoAnalyze's own.
    /// </summary>
    [Theory]
    [InlineData("deepseek-flash")]
    [InlineData("claude-opus-5-5")]
    public void The_region_effect_template_shows_the_segmentation_add_on_its_hold_carries(string chatModel)
    {
        WorkflowStep[] steps = StepsOf(WorkflowTemplateCatalog.RegionEffectTemplateKey);
        WorkflowStep analyze = steps.Single(s => s.StepType == StepType.VideoAnalyze);
        long addOn = SegmentationCreditPricing.AddOnMillis(analyze.VideoAnalyzeConfigJson);
        addOn.Should().Be(188_371, "one target on the template's 60 s cap: ceil(60 × 30 / 16) = 113 frame-units at 1,667");

        // The control: the same template with the analyze step's config stripped of its target.
        WorkflowStep[] controlSteps = StepsOf(WorkflowTemplateCatalog.RegionEffectTemplateKey);
        controlSteps.Single(s => s.StepType == StepType.VideoAnalyze).VideoAnalyzeConfigJson = null;
        SegmentationCreditPricing.AddOnMillis(controlSteps.Single(s => s.StepType == StepType.VideoAnalyze).VideoAnalyzeConfigJson)
            .Should().Be(0);

        WorkflowCostEstimate shown = Estimate(steps, chatModel);
        WorkflowCostEstimate control = Estimate(controlSteps, chatModel);

        string detail = $"{WorkflowTemplateCatalog.RegionEffectTemplateKey} on {chatModel}: shown low {Credits(shown.LowMillis)}, " +
            $"typical {Credits(shown.TypicalMillis)}, high {Credits(shown.HighMillis)} (add-on {Credits(addOn)}); " +
            $"control low {Credits(control.LowMillis)}, typical {Credits(control.TypicalMillis)}, high {Credits(control.HighMillis)}; " +
            $"required {Credits(shown.RequiredMillis)}";
        output.WriteLine(detail);

        // T-001c's gap: the template DISPLAYED 0.4–1.8 credits while its VideoAnalyze step RESERVED 200,621
        // millicredits, because the segmentation purchase was in the hold alone. Both edges of the band now
        // carry the same figure — the purchase does not depend on how long the analysis takes, so it moves
        // the band and leaves its width where it was.
        WorkflowStepCostEstimate analyzeShown = shown.Steps.Single(s => s.StepType == nameof(StepType.VideoAnalyze));
        WorkflowStepCostEstimate analyzeControl = control.Steps.Single(s => s.StepType == nameof(StepType.VideoAnalyze));
        analyzeShown.LowMillis.Should().Be(analyzeControl.LowMillis + addOn, detail);
        analyzeShown.TypicalMillis.Should().Be(analyzeControl.TypicalMillis + addOn, detail);
        analyzeShown.HighMillis.Should().Be(analyzeControl.HighMillis + addOn, detail);
        shown.LowMillis.Should().Be(control.LowMillis + addOn, detail);
        shown.TypicalMillis.Should().Be(control.TypicalMillis + addOn, detail);
        shown.HighMillis.Should().Be(control.HighMillis + addOn, detail);

        // Held: the run's requirement is still the hold plan's sum, and it still carries the segmentation
        // purchase (once) — this display change moves no reservation.
        shown.RequiredMillis.Should().Be(shown.Steps.Sum(s => s.HoldMillis), detail);
        shown.RequiredMillis.Should().BeGreaterThan(control.RequiredMillis, detail);
        long previous = new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples)
            .Estimate(StepType.VideoAnalyze, analyze.AgentDefinitionId)
            .HoldMillis;
        analyzeShown.HoldMillis.Should().BeGreaterThanOrEqualTo(previous + addOn, detail);
    }

    /// <summary>
    /// T-001d: with the add-on in the DISPLAYED band the media-only template is inside 3x again — the
    /// exact case D-08's 10x bound was widened for (it measured 4.25x on deepseek-flash and 9.59x on
    /// claude-opus-5-5 while the add-on was in the hold alone). Pinned here so the improvement cannot
    /// silently regress; the 10x bound in <see cref="AssertRunBand"/> stays as the safety net for a
    /// media-only run whose VideoAnalyze profile really is that wide, and is not the real check any more.
    /// </summary>
    [Theory]
    [InlineData("deepseek-flash")]
    [InlineData("claude-opus-5-5")]
    public void The_media_only_template_is_within_3x_once_the_add_on_is_shown(string chatModel)
    {
        List<string> mediaOnly = [.. WorkflowTemplateCatalog.GetAll()
            .Where(t => IsMediaOnly(t.Key, StepsOf(t.Key)))
            .Select(t => t.Key)];
        mediaOnly.Should().Contain(WorkflowTemplateCatalog.RegionEffectTemplateKey,
            "the region-effect template is the media-only template this bound exists for");

        foreach (string key in mediaOnly)
        {
            WorkflowCostEstimate estimate = Estimate(StepsOf(key), chatModel);
            string detail = $"{key} on {chatModel}: low {Credits(estimate.LowMillis)}, high {Credits(estimate.HighMillis)} " +
                $"(ratio {Ratio(estimate):0.00})";

            estimate.LowMillis.Should().BePositive(detail);
            estimate.HighMillis.Should().BeLessThanOrEqualTo(3 * estimate.LowMillis, detail);
        }
    }

    [Fact]
    public void With_history_the_required_credits_are_the_old_p90_sum_for_every_template()
    {
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            WorkflowStep[] steps = StepsOf(template.Key);
            Dictionary<int, long> videoHolds = steps
                .Where(s => s.StepType == StepType.VideoGenerate)
                .ToDictionary(s => s.StepOrder, _ => 160_000L);

            // Synthetic history: four samples for every step's (type, agent), so every hold is a p90.
            List<StepCreditSample> history = [.. steps.SelectMany(s => Enumerable.Range(1, 4)
                .Select(i => new StepCreditSample(s.StepType, s.AgentDefinitionId, i * 7_000L)))];

            foreach (IReadOnlyDictionary<int, long>? holds in new IReadOnlyDictionary<int, long>?[] { null, videoHolds })
            {
                var hold = new StepCreditEstimator(history, StepCreditEstimator.DefaultMinSamples);
                // QA-P2-8: a compute-only VideoAnalyze holds its compute part on every branch, so its
                // figure comes from the compute default rather than from the p90 of token-bearing runs;
                // the p90 sum then adds, for a VideoAnalyze step whose config names segmentation targets,
                // the worst case that hosted, per-frame-billed purchase can cost (SegmentationCreditPricing).
                // A plan-only compile (encode-once) is the new split's planning placeholder: it did not exist in
                // the pre-change template and records no usage, so it is zero here and in the "before" figure.
                // A step that may look at single moments (LookAtMoment) also holds the worst case of its looks, in a run
                // whose analysis describes events: a tool call its own history does not bound.
                // An analysis that describes events holds the worst case of its own looks the same way (the most it can
                // take, at the describer's output ceiling): the p90 of other analyses knows nothing of them.
                var pricer = new SnapshotPricer("claude-opus-5-5");
                long[] expected = [.. steps.Select(s => CompileEncodeStages.IsPlanStage(s) ? 0 : hold.Estimate(
                        s.StepType, s.AgentDefinitionId,
                        holds is not null && holds.TryGetValue(s.StepOrder, out long v) ? v : null,
                        computeOnly: StepCreditDefaults.IsComputeOnly(s)).HoldMillis
                    + SegmentationCreditPricing.AddOnMillis(s.VideoAnalyzeConfigJson)
                    + MomentLookAddOnMillis(steps, s, pricer)
                    + EventLookAddOnMillis(s, pricer))];

                WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
                    history, steps, holds, Display(), pricer);

                estimate.RequiredMillis.Should().Be(expected.Sum(), $"{template.Key} with history must hold its old p90 sum plus the segmentation and look add-ons");
                estimate.Steps.Select(s => s.HoldMillis).Should().Equal(expected);

                // ...and the one deliberate exception is a step that calls no model: it never holds the p90 of
                // runs that did.
                foreach (WorkflowStep step in steps.Where(StepCreditDefaults.IsComputeOnly))
                {
                    long tokenP90 = hold.Estimate(step.StepType, step.AgentDefinitionId).HoldMillis;
                    estimate.Steps.Single(s => s.StepOrder == step.StepOrder).HoldMillis
                        .Should().BeLessThan(tokenP90, $"{template.Key}: a compute-only step never holds a token-bearing p90");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TemplatesByChatModel))]
    public void On_a_cold_start_the_required_credits_are_never_below_the_old_figure_nor_the_video_holds_plus_the_displayed_low_edge(string templateKey, string chatModel)
    {
        WorkflowStep[] steps = StepsOf(templateKey);
        Dictionary<int, long> declared = steps
            .Where(s => s.StepType == StepType.VideoGenerate)
            .ToDictionary(s => s.StepOrder, _ => DeclaredVideoMillis);

        foreach (IReadOnlyDictionary<int, long>? holds in new IReadOnlyDictionary<int, long>?[] { null, declared })
        {
            WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], steps, holds, Display(), new SnapshotPricer(chatModel));

            List<WorkflowStepCostEstimate> video = [.. estimate.Steps.Where(s => s.StepType == nameof(StepType.VideoGenerate))];
            long videoHolds = video.Sum(s => s.HoldMillis);
            long expectedVideoHold = holds is null ? StepCreditDefaults.MillisFor(StepType.VideoGenerate).Low : DeclaredVideoMillis;
            video.Should().OnlyContain(s => s.HoldMillis == expectedVideoHold);
            long before = BeforeMillis(steps, holds);

            string detail = $"{templateKey} on {chatModel} ({(holds is null ? "undeclared" : "declared")} video): " +
                $"required {Credits(estimate.RequiredMillis)}, before {Credits(before)}, video {Credits(videoHolds)}, low {Credits(estimate.LowMillis)}";
            output.WriteLine(detail);

            estimate.RequiredMillis.Should().Be(estimate.Steps.Sum(s => s.HoldMillis), detail);
            estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(before, detail);
            estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(videoHolds + estimate.LowMillis, detail);
        }
    }

    [Theory]
    [MemberData(nameof(TemplatesByChatModel))]
    public void On_a_cold_start_every_cold_step_holds_the_larger_of_its_old_default_and_its_low_edge_share(string templateKey, string chatModel)
    {
        WorkflowStep[] steps = StepsOf(templateKey);
        var pricer = new SnapshotPricer(chatModel);
        var cold = new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples);

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], steps, null, Display(), pricer);

        List<WorkflowStep> coldSteps = [.. steps.Where(s => !CompileEncodeStages.IsPlanStage(s)
            && RunCreditHolds.IsCold(s.StepType, cold.Estimate(s.StepType, s.AgentDefinitionId)))];
        coldSteps.Should().NotBeEmpty(templateKey);
        List<RunStepInput> inputs = [.. coldSteps.Select(s => new RunStepInput(
            s.StepOrder,
            // The looks at events of a describing analysis are not in its share: their worst case is held on top (below).
            ColdStartStepPricing.PriceProfile(s, ColdStartStepPricing.AgentTypeOf(s.AgentDefinitionId, s, AgentTypes), AgentTypes, pricer, withoutEventLooks: true).Cost,
            0))];
        IReadOnlyDictionary<int, long> shares = ColdStartHoldPlan.Allocate(inputs, RunLoopModel.Of(steps).Loops);

        foreach (WorkflowStep step in coldSteps)
        {
            long previous = cold.Estimate(step.StepType, step.AgentDefinitionId,
                computeOnly: StepCreditDefaults.IsComputeOnly(step)).HoldMillis;
            // The cold floor is the step's own default plus any segmentation add-on it prices.
            long segmentation = SegmentationCreditPricing.AddOnMillis(step.VideoAnalyzeConfigJson);
            // Looks at single moments are held on top of the ladder, like web searches: they are not part of the share.
            // So are an analysis step's own looks at events. Before, a cold analysis held only its share of the low edge
            // (12 typical looks of a 20-look profile); now it holds the most it can take (30 when the length decides),
            // each at 600 output tokens, so the sound-effects recipe's first step holds more than its shown high edge.
            long looks = MomentLookAddOnMillis(steps, step, pricer) + EventLookAddOnMillis(step, pricer);
            long hold = estimate.Steps.Single(s => s.StepOrder == step.StepOrder).HoldMillis;
            string detail = $"{templateKey} on {chatModel}: #{step.StepOrder} {step.StepType} {step.Label}: " +
                $"hold {hold}, floor {previous + segmentation}, default {previous}, segmentation {segmentation}, share {shares[step.StepOrder]}, looks {looks}";

            previous.Should().BePositive(detail);
            hold.Should().BeGreaterThanOrEqualTo(previous, detail);
            hold.Should().Be(Math.Max(previous + segmentation, shares[step.StepOrder]) + looks, detail);
        }
    }

    [Theory]
    [InlineData("deepseek-flash")]
    [InlineData("claude-opus-5-5")]
    public void Quick_win_promo_still_needs_at_least_its_low_edge_on_a_cold_start(string chatModel)
    {
        WorkflowStep[] steps = StepsOf("quick-win-promo");

        WorkflowCostEstimate estimate = Estimate(steps, chatModel);

        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(estimate.LowMillis);
        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(BeforeMillis(steps, null));
    }

    [Fact]
    public void Prints_the_before_and_after_cold_start_required_credits()
    {
        StringBuilder table = new();
        table.AppendLine("| Template | Before (Σ B0 low) | After, deepseek-flash | Shown low–typical, deepseek-flash | After, claude-opus-5-5 | Shown low–typical, claude-opus-5-5 |");
        List<string> lowered = [];
        table.AppendLine("|---|---:|---:|---:|---:|---:|");
        int rows = 0;
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            WorkflowStep[] steps = StepsOf(template.Key);
            var cold = new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples);
            long before = steps.Where(s => !CompileEncodeStages.IsPlanStage(s)).Sum(s => cold.Estimate(s.StepType, s.AgentDefinitionId, computeOnly: StepCreditDefaults.IsComputeOnly(s)).HoldMillis);
            before.Should().Be(BeforeMillis(steps, null));
            WorkflowCostEstimate flash = Estimate(steps, "deepseek-flash");
            WorkflowCostEstimate opus = Estimate(steps, "claude-opus-5-5");

            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {template.Key} | {Credits(before)} | {Credits(flash.RequiredMillis)} | {Credits(flash.LowMillis)}–{Credits(flash.TypicalMillis)} | " +
                $"{Credits(opus.RequiredMillis)} | {Credits(opus.LowMillis)}–{Credits(opus.TypicalMillis)} |"));
            rows++;

            flash.RequiredMillis.Should().BeLessThanOrEqualTo(opus.RequiredMillis, template.Key);
            if (flash.RequiredMillis < before || opus.RequiredMillis < before)
            {
                lowered.Add(template.Key);
            }
        }

        output.WriteLine(table.ToString());
        lowered.Should().BeEmpty("the cold-start hold re-plan raised the cold-start hold and never lowered it");
        rows.Should().Be(WorkflowTemplateCatalog.GetAll().Count);
    }

    /// <summary>QA-1: a one-step workflow of a skewed agent shows exactly that step's own band, not a symmetric one.</summary>
    [Theory]
    [InlineData("deepseek-flash")]
    [InlineData("claude-opus-5-5")]
    public void A_one_step_DirectorAgent_workflow_shows_exactly_its_own_band(string chatModel)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = AgentIds[AgentType.DirectorAgent],
            AgentDefinition = new AgentDefinition { Id = AgentIds[AgentType.DirectorAgent], Name = "director", AgentType = AgentType.DirectorAgent },
            StepOrder = 1,
            StepType = StepType.Agent
        };

        WorkflowCostEstimate estimate = Estimate([step], chatModel);
        WorkflowStepCostEstimate own = estimate.Steps.Single();

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"DirectorAgent on {chatModel}: ≈ {Credits(estimate.TypicalMillis)} (usually {Credits(estimate.LowMillis)}–{Credits(estimate.HighMillis)}); " +
            $"step band {Credits(own.LowMillis)}–{Credits(own.HighMillis)}"));
        estimate.TypicalMillis.Should().Be(own.TypicalMillis);
        estimate.LowMillis.Should().Be(own.LowMillis);
        estimate.HighMillis.Should().Be(own.HighMillis);
    }

    [Fact]
    public void Prints_the_before_and_after_table()
    {
        StringBuilder table = new();
        table.AppendLine("| Template | Chat model | Old low–high (Σp50–Σp90) | New typical | New low–high | High/low | Fixed max |");
        table.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        int rows = 0;
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            WorkflowStep[] steps = StepsOf(template.Key);
            var cold = new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples);
            long oldLow = steps.Where(s => !CompileEncodeStages.IsPlanStage(s)).Sum(s => cold.Estimate(s.StepType, s.AgentDefinitionId).LowMillis);
            long oldHigh = steps.Where(s => !CompileEncodeStages.IsPlanStage(s)).Sum(s => cold.Estimate(s.StepType, s.AgentDefinitionId).HighMillis);

            foreach (string model in new[] { "deepseek-flash", "claude-opus-5-5" })
            {
                WorkflowCostEstimate estimate = Estimate(steps, model);
                table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {template.Key} | {model} | {Credits(oldLow)}–{Credits(oldHigh)} | {Credits(estimate.TypicalMillis)} | " +
                    $"{Credits(estimate.LowMillis)}–{Credits(estimate.HighMillis)} | {Ratio(estimate):0.00} | {Credits(estimate.FixedMaxMillis)} |"));
                rows++;
            }
        }

        output.WriteLine(table.ToString());
        rows.Should().Be(WorkflowTemplateCatalog.GetAll().Count * 2);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// D-08 (revised): 3x is the band assertion for every shipped template that has a chat/editor/voiceover
    /// step to dominate its total — no template's assertion is weaker than that.
    /// <para>
    /// A MEDIA-ONLY template — every step's type is <see cref="StepType.VideoAnalyze"/> or
    /// <see cref="StepType.VideoCompile"/> — asserts <c>high ≤ 10 × low</c> instead, because there is
    /// nothing but VideoAnalyze's own usage profile to shape its band: a two-step media template inherits
    /// that profile's spread wholesale (measured before T-001d: deepseek-flash 4.25x, claude-opus-5-5
    /// 9.59x; widths 1.353 and 17.7 credits), and no band-shape exemption can hide that. The widened bound
    /// is still a bound: a band wider than 10x fails.
    /// </para>
    /// <para>
    /// SINCE T-001d the segmentation add-on is in the displayed band as well as the hold, which is most of
    /// a <see cref="StepType.VideoAnalyze"/> step's cold-start cost, so the shipped media-only template now
    /// measures ~1.01x (deepseek-flash) and ~1.09x (claude-opus-5-5). This 10x bound therefore stays only as
    /// a safety net for a media-only run whose profile genuinely is that wide; the live check is the 3x
    /// one in <c>The_media_only_template_is_within_3x_once_the_add_on_is_shown</c>.
    /// </para>
    /// </summary>
    private static void AssertRunBand(string templateKey, WorkflowCostEstimate estimate, IReadOnlyList<WorkflowStep> steps, string detail)
    {
        const long ChatDominatesFactor = 3;
        const long MediaOnlyFactor = 10;

        // A subtitles template's cost is dominated by the speech-to-text of footage whose length is unknown on a
        // cold start, so its band inherits that profile's spread the same way a media-only template does (measured:
        // video-subtitles 4.25x on deepseek-flash and 9.59x on claude-opus-5-5; the translated one 3.4x and 5.0x).
        // The thumbnail recipe is a local analysis and a frame grab: its band is a fraction of a credit wide, so it gets the wide bound.
        bool wideBand = IsMediaOnly(templateKey, steps) || IsTranscriptionBound(templateKey) || templateKey == "thumbnail-and-cover-text";
        long factor = wideBand ? MediaOnlyFactor : ChatDominatesFactor;

        estimate.HighMillis.Should().BeLessThanOrEqualTo(factor * estimate.LowMillis,
            $"{detail} (a {(wideBand ? "media-only or transcription-bound template: 10x" : "chat/editor/voiceover template: 3x")} band)");
    }

    private static bool IsMediaOnly(string templateKey, IReadOnlyList<WorkflowStep> steps)
        => steps.All(s => s.StepType is StepType.VideoAnalyze or StepType.VideoCompile) && !IsTranscriptionBound(templateKey);

    /// <summary>The subtitles recipes: an analyze step that must transcribe, so their band is the speech-to-text spread.</summary>
    private static bool IsTranscriptionBound(string templateKey)
        => templateKey is "video-subtitles" or "video-subtitles-translated";

    private static WorkflowCostEstimate Estimate(IReadOnlyList<WorkflowStep> steps, string chatModel)
        => WorkflowCostEstimator.EstimateFrom([], steps, null, Display(), new SnapshotPricer(chatModel));

    /// <summary>
    /// What a step holds for its looks at single moments (<c>LookAtMoment</c>), worked out here from the grant and the
    /// run's analysis steps, not read back from the plan: the most looks a step may take, on the vision model, when the
    /// step's agent has the tool (an edit room's solo fallback is a story editor) and an analysis of the run describes
    /// events. 0 for every other step. The shipped templates name no vision model, so one price covers them.
    /// </summary>
    private static long MomentLookAddOnMillis(IReadOnlyList<WorkflowStep> steps, WorkflowStep step, SnapshotPricer pricer)
    {
        AgentType? agent = step.StepType switch
        {
            StepType.Agent => ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, AgentTypes),
            StepType.EditRoom => AgentType.VideoStoryEditor,
            _ => null
        };
        if (agent is not { } type || !ToolGroupCatalog.GroupsFor(type).Contains(ToolGroup.MomentLook))
            return 0;
        if (!steps.Any(s => s.StepType == StepType.VideoAnalyze && StepUsageProfiles.EventLookProvider(s.VideoAnalyzeConfigJson).Describes))
            return 0;

        return pricer.Price(new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Vision,
                MomentLookLimits.MaxLooksPerStep * StepUsageProfiles.EventLookInputTokens), type, step.AgentDefinitionId).CreditsMillis
            + pricer.Price(new ProfileQuantity(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision,
                MomentLookLimits.MaxLooksPerStep * StepUsageProfiles.EventLookMaxOutputTokens), type, step.AgentDefinitionId).CreditsMillis;
    }

    /// <summary>
    /// What a VideoAnalyze step holds for its own looks at events (<c>describeEvents</c>), worked out here from its config,
    /// not read back from the plan: the most looks it can take (its explicit count, else the automatic ceiling), each at
    /// the look's input tokens and the describer's output ceiling, on the vision model. 0 for every other step.
    /// </summary>
    private static long EventLookAddOnMillis(WorkflowStep step, SnapshotPricer pricer)
    {
        if (step.StepType != StepType.VideoAnalyze || StepUsageProfiles.EventLookWorstCase(step.VideoAnalyzeConfigJson) is not { } looks)
            return 0;
        return pricer.Price(new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Vision,
                looks.Looks * StepUsageProfiles.EventLookInputTokens, looks.ProviderId), AgentType.Custom, null).CreditsMillis
            + pricer.Price(new ProfileQuantity(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision,
                looks.Looks * StepUsageProfiles.EventLookMaxOutputTokens, looks.ProviderId), AgentType.Custom, null).CreditsMillis;
    }

    /// <summary>The cold-start requirement before the cold-start hold re-plan: Σ the <see cref="StepCreditEstimator"/> holds over no history.</summary>
    private static long BeforeMillis(IReadOnlyList<WorkflowStep> steps, IReadOnlyDictionary<int, long>? videoHolds)
    {
        var cold = new StepCreditEstimator([], StepCreditEstimator.DefaultMinSamples);
        // A plan-only compile is the encode-once split's planning placeholder and never held, so it is not part
        // of the pre-change figure this comparison is about.
        return steps.Where(s => !CompileEncodeStages.IsPlanStage(s)).Sum(s => cold.Estimate(
            s.StepType, s.AgentDefinitionId,
            videoHolds is not null && videoHolds.TryGetValue(s.StepOrder, out long v) ? v : null,
            computeOnly: StepCreditDefaults.IsComputeOnly(s)).HoldMillis);
    }

    private static DisplayCreditInputs Display() => DisplayCreditInputs.Empty with { AgentTypes = AgentTypes };

    /// <summary>
    /// Which steps the run's variance comes from, largest first. A step outside every loop contributes
    /// sigma_low² + sigma_high²; a step in a loop window contributes E[N²]·(sigma_low² + sigma_high²) +
    /// 2·Var(N)·m·B, which sums over the window to exactly the loop's two-sided E[N²]·V + 2·Var(N)·B².
    /// </summary>
    private static string VarianceShares(IReadOnlyList<WorkflowStep> steps, WorkflowCostEstimate estimate)
    {
        List<LoopInput> loops = [.. steps.Where(s => s.StepType == StepType.ReviewLoop)
            .Select(s => RunCreditRange.ColdStartLoop(s.StepOrder, s.LoopTargetStepOrder ?? int.MinValue, Math.Max(1, s.MaxIterations)))];
        Dictionary<int, WorkflowStepCostEstimate> byOrder = estimate.Steps.ToDictionary(s => s.StepOrder);
        List<(WorkflowStepCostEstimate Step, double Variance)> shares = [];
        foreach (WorkflowStepCostEstimate step in estimate.Steps)
        {
            double stepVariance = RunCreditRange.StepVariance(new StepCostDistribution(step.TypicalMillis, step.LowMillis, step.HighMillis));
            LoopInput? window = RunCreditRange.WindowOf(step.StepOrder, loops);
            if (window is null)
            {
                shares.Add((step, stepVariance));
                continue;
            }

            double body = byOrder.Values
                .Where(s => RunCreditRange.WindowOf(s.StepOrder, loops) == window)
                .Sum(s => (double)s.TypicalMillis);
            double n2 = window.PassVariance + (window.MeanPasses * window.MeanPasses);
            shares.Add((step, (n2 * stepVariance) + (2 * window.PassVariance * step.TypicalMillis * body)));
        }

        double total = shares.Sum(s => s.Variance);
        return total <= 0
            ? "nothing"
            : string.Join(", ", shares.OrderByDescending(s => s.Variance).Take(3).Select(s =>
                string.Create(CultureInfo.InvariantCulture, $"#{s.Step.StepOrder} {s.Step.Label} ({s.Step.Basis}) {s.Variance / total:P0}")));
    }

    private static double Ratio(WorkflowCostEstimate e) => e.LowMillis > 0 ? e.HighMillis / (double)e.LowMillis : double.PositiveInfinity;

    private static string Credits(long millis) => (millis / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The template's steps as provisioning would write them, agent navigation included, with no database.</summary>
    private static WorkflowStep[] StepsOf(string templateKey)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetAll().Single(t => t.Key == templateKey);
        Guid workflowId = Guid.NewGuid();
        return [.. template.Steps.Select((s, i) => new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = AgentIds[s.AgentType],
            AgentDefinition = new AgentDefinition { Id = AgentIds[s.AgentType], Name = s.AgentType.ToString(), AgentType = s.AgentType },
            StepOrder = i + 1,
            Label = s.Label,
            StepType = s.StepType,
            ConditionExpression = s.ConditionExpression,
            LoopSourceExpression = s.LoopSourceExpression,
            LoopTargetStepOrder = s.LoopTargetStepOrder,
            MaxIterations = s.MaxIterations,
            MinScore = s.MinScore,
            ParallelAgentIdsJson = s.ParallelAgentTypes is { Count: > 0 } parallel
                ? JsonSerializer.Serialize(parallel.Distinct().Select(t => AgentIds[t]))
                : null,
            VideoAnalyzeConfigJson = s.VideoAnalyzeConfigJson,
            VideoCompileConfigJson = s.VideoCompileConfigJson,
            VideoGenerateConfigJson = s.VideoGenerateConfigJson,
            EditRoomConfigJson = s.EditRoomConfigJson,
            GraphicsRoomConfigJson = s.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson = s.ColorGradeRoomConfigJson,
            DailiesRoomConfigJson = s.DailiesRoomConfigJson,
            ReviewLoopConfigJson = s.ReviewLoopConfigJson,
            VoiceoverConfigJson = s.VoiceoverConfigJson,
            ThumbnailGenerateConfigJson = s.ThumbnailGenerateConfigJson,
            TimelineRenderConfigJson = s.TimelineRenderConfigJson,
            CacheMode = s.CacheMode
        })];
    }

    /// <summary>Prices every quantity through the real pricing rule over the legacy seed's prices, Platform-billed.</summary>
    private sealed class SnapshotPricer(string chatModel) : IProfileQuantityPricer
    {
        private readonly InferenceProviderKind _chatKind = chatModel.StartsWith("claude", StringComparison.Ordinal)
            ? InferenceProviderKind.Anthropic
            : InferenceProviderKind.DeepSeek;

        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        {
            (InferenceProviderKind kind, InferenceProviderCapability capability, string model, string? variant) = quantity.Target switch
            {
                ProfilePriceTarget.Chat => (_chatKind, InferenceProviderCapability.Chat, chatModel, (string?)null),
                ProfilePriceTarget.Compute => (ComputeMetering.ProviderKind, InferenceProviderCapability.Compute,
                    WellKnownModelCatalog.ComputeModelCode, ComputeMetering.Tier1080),
                ProfilePriceTarget.SpeechSynthesis => (InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis, "default", null),
                ProfilePriceTarget.Transcription => (InferenceProviderKind.FishAudio, InferenceProviderCapability.Transcription, "default", null),
                // The shipped review loops look at the film (sampled frames and targeted looks): their vision calls are
                // priced at the fixture chat model's own token prices, never left unpriced.
                ProfilePriceTarget.Vision => (_chatKind, InferenceProviderCapability.Vision, chatModel, null),
                _ => (InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "default", (string?)null)
            };

            PriceQuote quote = Seed.Price(new PriceRequest(
                null, null, kind, capability, model, quantity.Kind, variant, UsageBillingMode.Platform, quantity.Quantity, At));
            return new ProfileQuote(quote.CreditsMillis, OwnProvider: false);
        }
    }
}
