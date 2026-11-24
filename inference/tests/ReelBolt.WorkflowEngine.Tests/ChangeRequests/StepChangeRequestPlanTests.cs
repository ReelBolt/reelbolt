using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Compositions.ReplayShapes;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>The replay planner generalised to a non-film target (docs/design/change-requests.md).</summary>
public class StepChangeRequestPlanTests
{
    private static List<WorkflowStep> VoiceoverShape() =>
    [
        Step(1, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
        Step(2, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
        Step(3, AgentType.VideoTransform, StepType.Voiceover, voiceover: """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":2}}"""),
        Step(4, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: """{"version":1,"source":{"kind":"StepOutput","stepOrder":1}}"""),
        Step(5, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Step","stepOrder":4},"analysisStepOrder":4,"enableVoiceover":true,"voiceoverStepOrder":3}"""),
        Step(6, AgentType.VideoReviewAgent, StepType.ReviewLoop, mode: AgentInputContextMode.FullWorkflow, loopTarget: 2)
    ];

    private static CompositionReplayPlan Plan(List<WorkflowStep> steps) =>
        CompositionReplayPlanner.Plan(steps, AllRan(steps), 3, CompositionCutPolicy.KeepCut, Guid.NewGuid(), Guid.NewGuid(),
            targetLabel: "the narration", targetStepType: nameof(StepType.Voiceover));

    [Fact]
    public void A_voiceover_target_revises_itself_reuses_what_precedes_and_reruns_what_reads_it()
    {
        CompositionReplayPlan plan = Plan(VoiceoverShape());
        Dictionary<int, ReplayStepDisposition> d = plan.Steps.ToDictionary(s => s.StepOrder, s => s.Disposition);

        d[1].Should().Be(ReplayStepDisposition.Reuse);
        d[2].Should().Be(ReplayStepDisposition.Reuse);
        d[3].Should().Be(ReplayStepDisposition.Revise);
        d[4].Should().Be(ReplayStepDisposition.Reuse, "an unrelated sibling branch does not read the narration");
        d[5].Should().Be(ReplayStepDisposition.Run, "the compile mixes the new audio");
        d[6].Should().Be(ReplayStepDisposition.Run, "it reviews what the request re-made");
        plan.TargetStepType.Should().Be("Voiceover");
        plan.Steps.Single(s => s.StepOrder == 3).Reason.Should().Contain("the narration");
    }

    [Fact]
    public void Existing_callers_keep_the_film_wording_and_old_json_parses()
    {
        List<WorkflowStep> steps = VoiceoverShape();
        CompositionReplayPlan film = CompositionReplayPlanner.Plan(steps, AllRan(steps), 3, CompositionCutPolicy.KeepCut, Guid.NewGuid(), null);

        film.TargetStepType.Should().BeNull();
        film.Steps.Single(s => s.StepOrder == 3).Reason.Should().Be("the film render this revision replaces");
        film.Steps.Single(s => s.StepOrder == 1).Reason.Should().Be("comes before the film");

        CompositionReplayPlan.TryParse(film.Serialize())!.TargetStepType.Should().BeNull();
        CompositionReplayPlan.TryParse(Plan(steps).Serialize())!.TargetStepType.Should().Be("Voiceover");
        CompositionReplayPlan.TryParse(film.Serialize().Replace(",\"targetStepType\":null", "")).Should().NotBeNull();
    }
}
