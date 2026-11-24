#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Encode once after the review: credits. The "before" shape is the template as it was (compile -> review loop back
/// to the story editor, every pass encoding); the "after" shape is today's template (plan pass inside the loop, one
/// Final encode after it). Holds, the displayed estimate and what a three-pass run is charged are compared.
/// </summary>
public sealed class EncodeOnceCreditTests(ITestOutputHelper output)
{
    private const string Key = "video-derush-edit";

    /// <summary>The template's steps as workflow steps; <paramref name="encodeOnce"/> false rebuilds the pre-change shape.</summary>
    private static List<WorkflowStep> TemplateSteps(bool encodeOnce)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.BaseByKey(Key)!;
        Dictionary<AgentType, Guid> agents = [];
        List<WorkflowStep> steps = [];
        foreach ((WorkflowTemplateStepDefinition def, int i) in template.Steps.Select((d, i) => (d, i)))
        {
            if (!agents.TryGetValue(def.AgentType, out Guid agent))
                agents[def.AgentType] = agent = Guid.NewGuid();
            string? compile = def.VideoCompileConfigJson;
            if (!encodeOnce && compile is not null)
                compile = compile.Replace(",\"encodeStage\":\"Plan\"", string.Empty);
            if (!encodeOnce && i == 6)
                continue;
            steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = i + 1,
                StepType = def.StepType,
                AgentDefinitionId = agent,
                Label = def.Label,
                LoopTargetStepOrder = def.LoopTargetStepOrder,
                MaxIterations = def.MaxIterations,
                MinScore = def.MinScore,
                VideoAnalyzeConfigJson = def.VideoAnalyzeConfigJson,
                VideoCompileConfigJson = compile,
                CacheMode = def.CacheMode
            });
        }

        return steps;
    }

    [Fact]
    public void The_template_is_a_plan_inside_the_loop_and_one_final_encode_after_the_review()
    {
        List<WorkflowStep> steps = TemplateSteps(encodeOnce: true);

        steps.Select(s => s.StepType).Should().Equal(
            StepType.VideoAnalyze, StepType.Agent, StepType.Agent, StepType.Agent, StepType.VideoCompile, StepType.ReviewLoop, StepType.VideoCompile);
        CompileEncodeStages.Of(steps[4]).Should().Be(CompileEncodeStage.Plan);
        CompileEncodeStages.Of(steps[6]).Should().Be(CompileEncodeStage.Final);
        steps[5].LoopTargetStepOrder.Should().Be(2, "the review still loops back to the story editor, not past the final encode");
        foreach (WorkflowStep compile in new[] { steps[4], steps[6] })
        {
            VideoCompileStepConfig config = System.Text.Json.JsonSerializer.Deserialize<VideoCompileStepConfig>(
                compile.VideoCompileConfigJson!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                })!;
            config.Validate().Should().BeEmpty("the template's own configs are encode-once eligible");
        }

        // The final encode reads the story editor by step order (Previous is the review by then) and is otherwise the plan step's config.
        steps[6].VideoCompileConfigJson.Should().Contain("\"decision\":{\"from\":\"Step\",\"stepOrder\":2}");
        steps[4].VideoCompileConfigJson!.Replace(",\"encodeStage\":\"Plan\"", string.Empty)
            .Should().Be(steps[6].VideoCompileConfigJson!.Replace(",\"encodeStage\":\"Final\"", string.Empty));
    }

    [Fact]
    public void A_plan_pass_holds_nothing_and_the_final_encode_holds_what_the_compile_held_before()
    {
        List<WorkflowStep> before = TemplateSteps(encodeOnce: false);
        List<WorkflowStep> after = TemplateSteps(encodeOnce: true);

        RunHoldPlan planBefore = RunCreditHolds.Plan(before, new StepCreditEstimator([]), null, ColdStartStepPricing.KnownAgentTypes(before), SeedProfilePricer.Instance);
        RunHoldPlan planAfter = RunCreditHolds.Plan(after, new StepCreditEstimator([]), null, ColdStartStepPricing.KnownAgentTypes(after), SeedProfilePricer.Instance);
        output.WriteLine("holds before: " + string.Join(", ", planBefore.HoldsMillis));
        output.WriteLine("holds after : " + string.Join(", ", planAfter.HoldsMillis));

        planAfter.HoldsMillis[4].Should().Be(0, "a plan pass records no usage, so it reserves nothing");
        planAfter.HoldsMillis[6].Should().BeGreaterThanOrEqualTo(planBefore.HoldsMillis[4], "the one encode is held at least as much as one compile pass was");
        planAfter.RequiredMillis.Should().BeLessThanOrEqualTo(planBefore.RequiredMillis + planBefore.HoldsMillis[4], "the run is not asked to afford two encodes");
    }

    [Fact]
    public void The_estimate_charges_one_encode_not_one_per_pass()
    {
        WorkflowCostEstimate before = WorkflowCostEstimator.EstimateFrom([], TemplateSteps(encodeOnce: false));
        WorkflowCostEstimate after = WorkflowCostEstimator.EstimateFrom([], TemplateSteps(encodeOnce: true));
        foreach ((string name, WorkflowCostEstimate e) in new[] { ("before", before), ("after", after) })
        {
            output.WriteLine($"{name}: low={e.LowMillis} typical={e.TypicalMillis} high={e.HighMillis} required={e.RequiredMillis}");
            foreach (WorkflowStepCostEstimate s in e.Steps)
                output.WriteLine($"  step {s.StepOrder} {s.StepType} basis={s.Basis} typical={s.TypicalMillis} p25={s.LowMillis} p75={s.HighMillis} hold={s.HoldMillis} expectedRuns={s.ExpectedRuns:0.##}");
        }

        WorkflowStepCostEstimate plan = after.Steps.Single(s => s.StepOrder == 5);
        WorkflowStepCostEstimate final = after.Steps.Single(s => s.StepOrder == 7);
        plan.Basis.Should().Be(nameof(CreditEstimateBasis.NotCharged));
        plan.TypicalMillis.Should().Be(0);
        plan.HoldMillis.Should().Be(0);
        final.ExpectedRuns.Should().Be(1, "the final encode sits outside the review loop");
        final.TypicalMillis.Should().Be(before.Steps.Single(s => s.StepOrder == 5).TypicalMillis, "an encode costs what an encode cost");
        before.Steps.Single(s => s.StepOrder == 5).ExpectedRuns.Should().BeGreaterThan(1, "before, every review round paid for an encode");
        after.TypicalMillis.Should().BeLessThan(before.TypicalMillis);
        after.HighMillis.Should().BeLessThan(before.HighMillis);
    }

    [Fact]
    public void A_plan_pass_leaves_no_history_sample_and_is_never_cached()
    {
        WorkflowStep plan = TemplateSteps(encodeOnce: true)[4];
        WorkflowStep final = TemplateSteps(encodeOnce: true)[6];
        var policy = new ReelBolt.WorkflowEngine.Execution.Caching.StepCachePolicy();

        policy.IsCacheable(plan).Should().BeFalse();
        plan.CacheMode = StepCacheMode.Always;
        policy.IsCacheable(plan).Should().BeFalse("not even an explicit Always can make a plan stand in for an encoded result");
        policy.IsCacheable(final).Should().BeTrue("the final encode may hit the cache like any compile");
    }
}
