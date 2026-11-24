using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Compositions.ReplayShapes;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>
/// The dependency analysis and the replay plan a composition revision runs after re-rendering the film
/// (docs/design/composition-revisions.md "Replaying what depends on the film"), over the real staging
/// workflow, every editing template behind a film, and the edge shapes.
/// </summary>
public class CompositionReplayPlannerTests
{
    private static CompositionReplayPlan Plan(List<WorkflowStep> steps, int revised, CompositionCutPolicy policy = CompositionCutPolicy.KeepCut, Dictionary<int, Guid>? results = null) =>
        CompositionReplayPlanner.Plan(steps, results ?? AllRan(steps), revised, policy, Guid.NewGuid(), Guid.NewGuid());

    private static Dictionary<int, ReplayStepDisposition> Dispositions(CompositionReplayPlan plan) =>
        plan.Steps.ToDictionary(s => s.StepOrder, s => s.Disposition);

    [Fact]
    public void The_staging_animated_film_edit_room_workflow_replays_analyze_edit_compile_and_review()
    {
        List<WorkflowStep> steps = AnimatedFilmEditRoomCut();
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(steps);

        graph.DependsOn(5).Keys.Should().BeEquivalentTo([4], "the analysis reads the render and two project files");
        graph.DependsOn(6).Keys.Should().BeEquivalentTo([5], "the edit room's view is the previous step");
        graph.DependsOn(7).Keys.Should().BeEquivalentTo([4, 5, 6], "decision, analysis, and the video the analysis read");
        graph.DownstreamOf(4).Should().BeEquivalentTo([5, 6, 7, 8]);
        graph.FinalVideoStepOrders(4).Should().Equal(7);

        CompositionReplayPlan keep = Plan(steps, 4);
        Dispositions(keep).Should().Equal(new Dictionary<int, ReplayStepDisposition>
        {
            [1] = ReplayStepDisposition.Reuse,
            [2] = ReplayStepDisposition.Reuse,
            [3] = ReplayStepDisposition.Reuse,
            [4] = ReplayStepDisposition.Revise,
            [5] = ReplayStepDisposition.Run,
            [6] = ReplayStepDisposition.KeepIfCompatible,
            [7] = ReplayStepDisposition.Run,
            [8] = ReplayStepDisposition.Run
        });
        keep.HasDownstreamWork.Should().BeTrue();
        keep.HasEditDecisions.Should().BeTrue();
        keep.FinalVideoStepOrders.Should().Equal(7);

        Dispositions(Plan(steps, 4, CompositionCutPolicy.ReEdit))[6].Should().Be(ReplayStepDisposition.Run, "re-edit re-makes the decision");
    }

    public static TheoryData<string> EditingTemplates => new()
    {
        "video-derush-edit",
        "video-derush-edit-graphics",
        "video-derush-edit-music",
        "video-derush-edit-room",
        "video-derush-edit-graphics-room",
        "video-derush-edit-grade-room",
        "video-derush-edit-sfx",
        "video-derush-edit-broll",
        "video-derush-edit-voiceover",
        "video-derush-edit-pickups",
        "video-derush-edit-dub",
        WorkflowTemplateCatalog.NarratedStoryTemplateKey
    };

    [Theory]
    [MemberData(nameof(EditingTemplates))]
    public void Every_editing_template_behind_a_film_replays_every_step_keeping_only_its_decisions(string key)
    {
        List<WorkflowStep> steps = FilmThen(key);
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(steps);

        graph.DownstreamOf(1).Should().BeEquivalentTo(steps.Skip(1).Select(s => s.StepOrder),
            "every step of an edit of the film depends on the film");

        Dictionary<int, ReplayStepDisposition> keep = Dispositions(Plan(steps, 1));
        Dictionary<int, ReplayStepDisposition> reEdit = Dispositions(Plan(steps, 1, CompositionCutPolicy.ReEdit));
        foreach (WorkflowStep step in steps.Skip(1))
        {
            bool decision = WorkflowStepDependencyGraph.IsEditDecisionStep(step);
            keep[step.StepOrder].Should().Be(decision ? ReplayStepDisposition.KeepIfCompatible : ReplayStepDisposition.Run,
                $"{key} step {step.StepOrder} ({step.StepType}/{step.AgentDefinition.AgentType}) under Keep the same cut");
            reEdit[step.StepOrder].Should().Be(ReplayStepDisposition.Run, $"{key} step {step.StepOrder} under Re-edit");
        }

        int compile = steps.Last(s => s.StepType == StepType.VideoCompile).StepOrder;
        graph.FinalVideoStepOrders(1).Should().Equal(compile);
        steps.Any(WorkflowStepDependencyGraph.IsEditDecisionStep).Should().BeTrue($"{key} makes an editorial decision");
    }

    [Fact]
    public void The_decision_references_of_each_template_compile_are_read_as_dependencies()
    {
        List<WorkflowStep> broll = FilmThen("video-derush-edit-broll");
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(broll);
        // Author 1, analyze 2, editor 3, shot director 4, generate 5, compile 6, review 7.
        graph.DependsOn(5).Keys.Should().Contain([4, 2], "the generation step reads the shot plan and the analysis");
        graph.DependsOn(6).Keys.Should().Contain([2, 3, 5, 1], "decision, analysis (and its film), generated clips");

        List<WorkflowStep> voiceover = FilmThen("video-derush-edit-voiceover");
        WorkflowStepDependencyGraph vo = WorkflowStepDependencyGraph.Build(voiceover);
        // Author 1, analyze 2, editor 3, narration 4, voiceover 5, compile 6, review 7.
        vo.DependsOn(5).Keys.Should().Contain(4);
        vo.DependsOn(6).Keys.Should().Contain([3, 2, 5]);
    }

    [Fact]
    public void A_film_that_is_the_deliverable_has_nothing_downstream_and_the_revision_stays_one_step()
    {
        // quick-win-promo: ... Author (10) → Review (11, loops back to the Author).
        List<WorkflowStep> steps = FromTemplate(WorkflowTemplateCatalog.StarterTemplateKey);
        int author = steps.Single(s => s.AgentDefinition.AgentType == AgentType.AuthorAgent).StepOrder;

        CompositionReplayPlan plan = Plan(steps, author);

        plan.HasDownstreamWork.Should().BeFalse("the review only reviews the film the owner just asked to change");
        plan.For(author + 1)!.Disposition.Should().Be(ReplayStepDisposition.Skip);
        plan.FinalVideoStepOrders.Should().Equal(author);
    }

    [Fact]
    public void A_whole_workflow_agent_after_the_film_runs_again()
    {
        List<WorkflowStep> steps =
        [
            Step(1, AgentType.AuthorAgent),
            Step(2, AgentType.Custom, mode: AgentInputContextMode.FullWorkflow, label: "Write the video description")
        ];

        CompositionReplayPlan plan = Plan(steps, 1);

        plan.For(2)!.Disposition.Should().Be(ReplayStepDisposition.Run);
        plan.HasDownstreamWork.Should().BeTrue();
    }

    [Fact]
    public void Two_compile_steps_are_both_final_and_a_compile_that_is_re_analysed_is_not()
    {
        string analyze = """{"version":1,"source":{"kind":"StepOutput","stepOrder":1}}""";
        List<WorkflowStep> two =
        [
            Step(1, AgentType.AuthorAgent),
            Step(2, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: analyze),
            Step(3, AgentType.VideoStoryEditor, mode: AgentInputContextMode.PreviousStepOnly),
            Step(4, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Step","stepOrder":3},"analysisStepOrder":2,"outputFormat":"Landscape"}"""),
            Step(5, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Step","stepOrder":3},"analysisStepOrder":2,"outputFormat":"Vertical"}""")
        ];
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(two);
        graph.FinalVideoStepOrders(1).Should().Equal(4, 5);
        Dispositions(Plan(two, 1)).Should().Contain(new KeyValuePair<int, ReplayStepDisposition>(4, ReplayStepDisposition.Run))
            .And.Contain(new KeyValuePair<int, ReplayStepDisposition>(5, ReplayStepDisposition.Run));

        List<WorkflowStep> chained =
        [
            .. two.Take(4),
            Step(5, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: """{"version":1,"source":{"kind":"StepOutput","stepOrder":4}}"""),
            Step(6, AgentType.VideoStoryEditor, mode: AgentInputContextMode.PreviousStepOnly),
            Step(7, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":5}""")
        ];
        WorkflowStepDependencyGraph chain = WorkflowStepDependencyGraph.Build(chained);
        chain.FinalVideoStepOrders(1).Should().Equal(7);
        chain.DownstreamOf(1).Should().BeEquivalentTo([2, 3, 4, 5, 6, 7]);
    }

    [Fact]
    public void Steps_that_do_not_read_the_film_are_reused_even_after_it()
    {
        // A narration written from the brief alone (PreviousStepOnly of a step before the film) is not
        // touched by re-rendering the film; the compile that reads both is.
        List<WorkflowStep> steps =
        [
            Step(1, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.FullWorkflow),
            Step(2, AgentType.AuthorAgent),
            Step(3, AgentType.VideoTransform, StepType.Voiceover, voiceover: """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}"""),
            Step(4, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: """{"version":1,"source":{"kind":"StepOutput","stepOrder":2}}"""),
            Step(5, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Step","stepOrder":4},"analysisStepOrder":4,"enableVoiceover":true,"voiceoverStepOrder":3}""")
        ];

        Dictionary<int, ReplayStepDisposition> plan = Dispositions(Plan(steps, 2));

        plan[1].Should().Be(ReplayStepDisposition.Reuse);
        plan[3].Should().Be(ReplayStepDisposition.Reuse, "the voiceover reads only the script");
        plan[4].Should().Be(ReplayStepDisposition.Run);
        plan[5].Should().Be(ReplayStepDisposition.Run);
    }

    [Fact]
    public void Which_render_was_revised_decides_what_replays()
    {
        // Two films in one run, each analysed and compiled on its own.
        List<WorkflowStep> steps =
        [
            Step(1, AgentType.MotionGraphicsPlanner, mode: AgentInputContextMode.PreviousStepOnly, label: "Intro film"),
            Step(2, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: """{"version":1,"source":{"kind":"StepOutput","stepOrder":1}}"""),
            Step(3, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":2}"""),
            Step(4, AgentType.AuthorAgent, mode: AgentInputContextMode.SelectedPriorSteps, label: "Main film"),
            Step(5, AgentType.VideoTransform, StepType.VideoAnalyze, analyze: """{"version":1,"source":{"kind":"StepOutput","stepOrder":4}}"""),
            Step(6, AgentType.VideoTransform, StepType.VideoCompile, compile: """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":5}""")
        ];
        steps[3].SelectedPriorStepOrdersJson = "[]";

        Dictionary<int, ReplayStepDisposition> second = Dispositions(Plan(steps, 4));
        second[1].Should().Be(ReplayStepDisposition.Reuse);
        second[2].Should().Be(ReplayStepDisposition.Reuse);
        second[3].Should().Be(ReplayStepDisposition.Reuse);
        second[5].Should().Be(ReplayStepDisposition.Run);
        second[6].Should().Be(ReplayStepDisposition.Run);

        // The second film's author reads the sandbox the first one wrote, so revising the first
        // replays everything after it.
        WorkflowStepDependencyGraph.Build(steps).DownstreamOf(1).Should().BeEquivalentTo([2, 3, 4, 5, 6]);
    }

    [Fact]
    public void Unreadable_configs_and_branches_fall_back_to_depending_on_everything_before()
    {
        List<WorkflowStep> steps =
        [
            Step(1, AgentType.AuthorAgent),
            Step(2, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.PreviousStepOnly),
            Step(3, AgentType.VideoTransform, StepType.VideoCompile, compile: "{not json")
        ];
        WorkflowStepDependencyGraph.Build(steps).DependsOn(3).Keys.Should().BeEquivalentTo([1, 2]);

        List<WorkflowStep> branched =
        [
            Step(1, AgentType.AuthorAgent),
            Step(2, AgentType.Custom, StepType.Conditional),
            Step(3, AgentType.ScriptwriterAgent, mode: AgentInputContextMode.PreviousStepOnly)
        ];
        WorkflowStepDependencyGraph.Build(branched).DependsOn(3).Keys.Should().BeEquivalentTo([1, 2],
            "a branch can skip the previous step, so 'previous' may resolve further back");
        Dispositions(Plan(branched, 1))[2].Should().Be(ReplayStepDisposition.Run, "a branch is re-evaluated");
    }

    [Fact]
    public void A_step_that_never_ran_before_the_film_is_skipped_and_after_it_is_run()
    {
        List<WorkflowStep> steps = AnimatedFilmEditRoomCut();
        Dictionary<int, Guid> results = AllRan(steps);
        results.Remove(2);
        results.Remove(6);

        CompositionReplayPlan plan = Plan(steps, 4, results: results);

        plan.For(2)!.Disposition.Should().Be(ReplayStepDisposition.Skip);
        plan.For(6)!.Disposition.Should().Be(ReplayStepDisposition.Run, "there is no decision to keep");
    }

    [Fact]
    public void The_plan_round_trips_through_its_json_column()
    {
        CompositionReplayPlan plan = Plan(AnimatedFilmEditRoomCut(), 4);

        CompositionReplayPlan parsed = CompositionReplayPlan.TryParse(plan.Serialize())!;

        parsed.Should().NotBeNull();
        parsed.Steps.Select(s => (s.StepOrder, s.Disposition, s.SourceStepResultId)).Should()
            .Equal(plan.Steps.Select(s => (s.StepOrder, s.Disposition, s.SourceStepResultId)));
        parsed.RevisedStepOrder.Should().Be(4);
        parsed.HasEditDecisions.Should().BeTrue();
        plan.Serialize().Should().Contain("\"KeepIfCompatible\"").And.Contain("\"cutPolicy\":\"KeepCut\"");
        CompositionReplayPlan.TryParse("{garbage").Should().BeNull();
        CompositionReplayPlan.TryParse("""{"version":99,"steps":[],"finalVideoStepOrders":[]}""").Should().BeNull();
    }

    [Fact]
    public void The_step_cloner_copies_every_scalar_column_of_a_step()
    {
        // A config column added to WorkflowStep must reach the replay snapshot; this list is the
        // contract — extend it when WorkflowStep grows a column.
        WorkflowStepCloner.CopiedPropertyNames.Should().BeEquivalentTo(
        [
            "AgentDefinitionId", "AgentInputContextMode", "AudioCleanupConfigJson", "AudioGenerateConfigJson", "CacheMode", "ColorGradeRoomConfigJson",
            "ConditionExpression", "ConditionalConfigJson", "ContinueCompositionJson", "DailiesRoomConfigJson", "DesignSystemConfigJson",
            "EdgeConditionJson", "EditRoomConfigJson",
            "ExtractConfigJson", "ExtractFrameConfigJson", "FalseBranchStepOrder", "GraphicsRoomConfigJson", "ImageGenerateConfigJson", "InputMappingJson", "Label",
            "LoopSourceExpression", "LoopTargetStepOrder", "MaxIterations", "MinScore", "ParallelAgentIdsJson", "PinInsertConfigJson", "VideoEnhanceConfigJson",
            "RegisterOutputAsProjectFile", "ReviewLoopConfigJson", "SelectedPriorStepOrdersJson", "StepOrder", "StepType",
            "ThumbnailGenerateConfigJson", "TimelineRenderConfigJson", "TrueBranchStepOrder", "VideoAnalyzeConfigJson",
            "VideoCompileConfigJson", "VideoGenerateConfigJson", "VoiceoverConfigJson"
        ]);

        WorkflowStep source = AnimatedFilmEditRoomCut()[6];
        Guid workflow = Guid.NewGuid();
        WorkflowStep copy = WorkflowStepCloner.Clone(source, workflow);
        copy.Id.Should().NotBe(source.Id);
        copy.WorkflowDefinitionId.Should().Be(workflow);
        copy.VideoCompileConfigJson.Should().Be(source.VideoCompileConfigJson);
        copy.AgentDefinition.Should().BeNull("a navigation would make EF insert the agent row again");
    }
}
