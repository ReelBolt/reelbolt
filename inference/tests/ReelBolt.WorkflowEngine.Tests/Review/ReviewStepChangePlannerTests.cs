using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// Change requests a review asks for inside its own run (docs/design/review-loop-adjustments.md "Change requests inside
/// the run"): the same catalogue and validator a customer request goes through, and never a prompt, consent, provider,
/// model or spend cap.
/// </summary>
public class ReviewStepChangePlannerTests
{
    private const string VoiceoverOutput = """
        {"view":{"lines":[{"id":"vo0","status":"ok","text":"Benvenuti."},{"id":"vo1","status":"ok","text":"Il caffe."},{"id":"vo2","status":"ok","text":"Novara ti aspetta."}]},"meta":{}}
        """;

    private const string VideoGenerateOutput = """{"clips":[{"clipId":"v1","status":"succeeded","prompt":"a cup"},{"clipId":"v2","status":"succeeded","prompt":"a street"}]}""";

    /// <summary>Voiceover(1) -> VideoGenerate(2) -> VideoAnalyze(3) -> EditRoom(4, loop target) -> Compile(5) -> Review(6).</summary>
    private static List<WorkflowStep> Steps() =>
    [
        ReviewAdjustmentPlannerTests.Step(1, StepType.Voiceover),
        ReviewAdjustmentPlannerTests.Step(2, StepType.VideoGenerate),
        ReviewAdjustmentPlannerTests.Step(3, StepType.VideoAnalyze, analyze: """{"transcription":"Off","vision":"Off"}"""),
        ReviewAdjustmentPlannerTests.Step(4, StepType.EditRoom),
        ReviewAdjustmentPlannerTests.Step(5, StepType.VideoCompile, compile: """{"version":1}"""),
        ReviewAdjustmentPlannerTests.Step(6, StepType.ReviewLoop, loopTarget: 4),
    ];

    private static ReviewAdjustmentRequest Request(List<WorkflowStep> steps) =>
        new(steps, steps.Single(s => s.StepType == StepType.ReviewLoop), RunHasOwnSnapshot: true, LoopsBack: true,
            LatestOutputJson: order => order switch { 1 => VoiceoverOutput, 2 => VideoGenerateOutput, _ => null });

    private static ReviewAdjustmentProposal C(int step, string item, string setting, string value, string reason = "because") =>
        new(step, setting, JsonValue.Create(value), reason, item);

    [Fact]
    public void A_voiceover_line_change_becomes_one_validated_customer_request_and_widens_the_rerun()
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Steps()),
        [
            C(1, "vo2", "text", "Novara, ti aspettiamo.", "the line was cut mid-word"),
            C(1, "vo2", "pronunciation", "Novara=Novàra"),
            C(1, "vo1", "speed", "1.2"),
        ]);

        plan.Decisions.Should().OnlyContain(d => d.IsApplied && d.Kind == ReviewAdjustmentDecision.ChangeKind);
        plan.Decisions[0].Item.Should().Be("vo2");
        plan.Decisions[0].To.Should().Be("Novara, ti aspettiamo.");
        ReviewStepChange change = plan.StepChanges.Should().ContainSingle().Subject;
        change.StepOrder.Should().Be(1);
        change.Payload.Overrides!["vo2"]["text"].GetString().Should().Be("Novara, ti aspettiamo.");
        change.Payload.Overrides["vo2"]["pronunciation"][0].GetProperty("spelling").GetString().Should().Be("Novàra");
        change.Payload.Overrides["vo1"]["speed"].GetDouble().Should().Be(1.2);
        ChangeRequestValidator.Validate(StepType.Voiceover, change.Payload, VoiceoverOutput).Should().BeEmpty(
            "the payload is exactly what a customer request would carry");
        plan.RerunFromStepOrder.Should().Be(1);
        plan.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void A_change_outside_the_customer_limits_is_refused_with_the_validators_words()
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Steps()),
            [C(1, "vo2", "speed", "3"), C(1, "vo9", "text", "hello")]);

        plan.HasChanges.Should().BeFalse();
        plan.Decisions.Should().OnlyContain(d => d.Status == ReviewAdjustmentDecision.Refused);
        plan.Decisions[0].Why.Should().Contain("must be between 0.5 and 2");
        plan.Decisions[0].Why.Should().Contain("unknown item");
    }

    [Theory]
    [InlineData(2, "v1", "prompt", "a red cup", "prompts are written by the user")]
    [InlineData(2, "v1", "allowSourceMediaEgress", "true", "never adjustable")]
    [InlineData(2, "v1", "providerId", "x", "never adjustable")]
    [InlineData(2, "v1", "model", "kling", "never adjustable")]
    [InlineData(2, "v1", "maxSpendCredits", "999", "never adjustable")]
    [InlineData(2, "v1", "takes", "4", "never adjustable")]
    [InlineData(1, "vo0", "systemPrompt", "be nice", "never adjustable")]
    [InlineData(1, "vo0", "color", "red", "can set only text, speed, pronunciation")]
    public void A_review_never_writes_a_prompt_consent_a_provider_a_model_or_a_spend_cap(int step, string item, string setting, string value, string why)
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Steps()), [C(step, item, setting, value)]);

        plan.HasChanges.Should().BeFalse();
        plan.Decisions.Should().ContainSingle().Which.Why.Should().Contain(why);
    }

    [Fact]
    public void A_shot_retake_is_accepted_and_widens_the_rerun_to_the_generate_step()
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Steps()), [C(2, "v1", "retake", "true", "the cup is blurred")]);

        plan.Decisions.Should().ContainSingle().Which.IsApplied.Should().BeTrue();
        JsonElement retake = plan.StepChanges.Single().Payload.Overrides!["v1"]["retake"];
        retake.ValueKind.Should().Be(JsonValueKind.True);
        plan.RerunFromStepOrder.Should().Be(2);
    }

    [Fact]
    public void A_reviewer_may_not_change_the_voice()
    {
        // A voice needs the user's consent; a customer may ask for one, a review never does.
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Steps()), [C(1, "*", "voiceId", Guid.NewGuid().ToString())]);

        plan.Decisions.Should().ContainSingle().Which.IsApplied.Should().BeFalse();
        plan.Decisions.Single().Why.Should().Contain("never adjustable by a review");
        plan.StepChanges.Should().BeEmpty();
    }

    [Fact]
    public void Change_entries_and_plain_settings_share_the_six_per_round_cap_and_a_repeat_is_refused()
    {
        List<ReviewAdjustmentProposal> proposals =
        [
            C(1, "vo0", "text", "One."),
            C(1, "vo0", "text", "Two."),
            .. Enumerable.Range(0, 6).Select(i => C(1, "vo1", "speed", "1." + i)),
        ];

        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Steps()), proposals);

        plan.Decisions.Should().HaveCount(8);
        plan.Decisions[1].Why.Should().Contain("asked twice");
        plan.Decisions.Skip(6).Should().OnlyContain(d => d.Why!.Contains("more than 6"));
    }

    [Fact]
    public void A_script_instruction_needs_the_scene_it_is_about()
    {
        WorkflowStep script = ReviewAdjustmentPlannerTests.Step(1, StepType.Agent);
        script.AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = "Script", AgentType = AgentType.ScriptwriterAgent };
        List<WorkflowStep> steps = [script, ReviewAdjustmentPlannerTests.Step(2, StepType.ReviewLoop, loopTarget: 1)];
        const string scriptOutput = """{"scenes":[{"sceneId":"sc1","voiceover":"Hello there."},{"sceneId":"sc2","voiceover":"Goodbye."}]}""";
        ReviewAdjustmentRequest request = new(steps, steps[1], true, true, LatestOutputJson: _ => scriptOutput);

        ReviewAdjustmentPlanner.Plan(request, [new ReviewAdjustmentProposal(1, "instruction", JsonValue.Create("calmer"), "r")])
            .Decisions.Single().Why.Should().Contain("must name the scene");
        ReviewAdjustmentPlan named = ReviewAdjustmentPlanner.Plan(request, [C(1, "sc2", "instruction", "make it calmer")]);
        named.Decisions.Single().IsApplied.Should().BeTrue();
        named.StepChanges.Single().Payload.Instruction.Should().Be("make it calmer");
        named.StepChanges.Single().Payload.ItemIds.Should().Equal("sc2");
    }

    [Fact]
    public void The_ceiling_counts_each_step_a_review_could_re_enter_once_per_loop_back()
    {
        List<WorkflowStep> steps = Steps();
        WorkflowStep review = steps.Single(s => s.StepType == StepType.ReviewLoop);
        ReviewAdjustmentCeiling.ExtraRunsByStepOrder(steps, s => s.AgentDefinition?.AgentType).Should().BeEmpty("adjustments are off");

        review.ReviewLoopConfigJson = """{"allowAdjustments":true}""";
        review.MaxIterations = 3;
        IReadOnlyDictionary<int, int> extra = ReviewAdjustmentCeiling.ExtraRunsByStepOrder(steps, s => s.AgentDefinition?.AgentType);

        // Voiceover, VideoGenerate (change requests) and the analysis (settings) sit before the loop target (4); each may
        // run again at most once per loop-back (MaxIterations - 1 = 2). Steps from the target on repeat with the loop.
        extra.Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 2, [2] = 2, [3] = 2 });
    }
}
