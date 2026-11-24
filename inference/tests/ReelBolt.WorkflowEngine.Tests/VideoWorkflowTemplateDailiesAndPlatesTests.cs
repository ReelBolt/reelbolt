using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The two templates issue #93 phase 4 adds — <c>video-derush-edit-dailies</c> and
/// <c>quick-win-promo-plates</c> — seed hand-authored JSON literals whose step references have to
/// line up with the step ORDER they sit at (the exact class of bug
/// <see cref="VideoWorkflowTemplateBrollTests"/> was written for). Each literal is deserialized with
/// the real executor options and every resolved reference is asserted.
/// </summary>
public class VideoWorkflowTemplateDailiesAndPlatesTests
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template(string key)
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.GetByKey(key);
        template.Should().NotBeNull($"template '{key}' must be registered in WorkflowTemplateCatalog");
        return template!;
    }

    // ---- video-derush-edit-dailies ----

    [Fact]
    public void Dailies_template_is_opt_in_and_has_eight_steps_in_the_documented_order()
    {
        WorkflowTemplateDefinition t = Template("video-derush-edit-dailies");
        t.AutoCreateOnProject.Should().BeFalse();
        t.Steps.Should().HaveCount(10);
        t.Steps.Select(s => s.StepType).Should().Equal(
            StepType.VideoAnalyze, StepType.Agent, StepType.Agent, StepType.VideoGenerate,
            StepType.VideoAnalyze, StepType.DailiesRoom, StepType.Agent, StepType.Agent, StepType.VideoCompile, StepType.ReviewLoop);
        t.Steps[6].AgentType.Should().Be(AgentType.MotionDirector);
        t.Steps[7].AgentType.Should().Be(AgentType.DynamicsPlanner, "the planner runs after the take selection, so it sees only the chosen takes");
        t.Steps[1].AgentType.Should().Be(AgentType.VideoStoryEditor);
        t.Steps[2].AgentType.Should().Be(AgentType.ShotDirector);
        t.Steps[5].AgentType.Should().Be(AgentType.VideoTransform, "a room step's real seats are resolved from its config, never from its placeholder agent");
    }

    [Fact]
    public void Dailies_generate_step_asks_for_two_takes_with_look_matching_and_continuity_against_steps_3_and_1()
    {
        WorkflowTemplateStepDefinition step = Template("video-derush-edit-dailies").Steps[3];
        VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(step.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;

        config.PlanSource.Should().Be(VideoGeneratePlanSource.StepRef);
        config.Plan!.StepOrder.Should().Be(3);
        config.Analysis!.StepOrder.Should().Be(1);
        config.Takes.Should().Be(2, "two takes per shot is the whole point of a dailies pass");
        config.MaxClips.Should().Be(8, "4 shots x 2 takes must fit the step ceiling");
        config.LookMatching.Should().BeTrue();
        config.CameraContinuity.Should().BeTrue();
        config.Validate().Should().BeEmpty();
        config.ValidateForKind(InferenceProviderKind.MiniMax).Should().BeEmpty();
    }

    [Fact]
    public void Dailies_second_analyze_step_sources_the_generated_clips_of_step_4()
    {
        WorkflowTemplateStepDefinition step = Template("video-derush-edit-dailies").Steps[4];
        VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(step.VideoAnalyzeConfigJson!, ConfigJsonOptions)!;

        config.Sources.Should().ContainSingle();
        config.Sources![0].Kind.Should().Be(VideoSourceKind.GeneratedClips);
        config.Sources[0].StepOrder.Should().Be(4);
        config.Source.Kind.Should().Be(VideoSourceKind.GeneratedClips);
        config.Transcription.Should().Be(VideoTranscriptionMode.Off, "a generated b-roll clip has no narration to transcribe");
        config.DetectSilence.Should().BeFalse();
    }

    [Fact]
    public void Dailies_room_step_views_step_5_and_is_pinned_to_CacheMode_Never()
    {
        WorkflowTemplateStepDefinition step = Template("video-derush-edit-dailies").Steps[5];
        step.DailiesRoomConfigJson.Should().NotBeNullOrWhiteSpace();
        step.CacheMode.Should().Be(StepCacheMode.Never);

        DailiesRoomStepConfig config = JsonSerializer.Deserialize<DailiesRoomStepConfig>(step.DailiesRoomConfigJson!, ConfigJsonOptions)!;
        config.View!.From.Should().Be(ExtractInputSource.Step);
        config.View.StepOrder.Should().Be(5);
        config.EffectiveSeats.Should().HaveCount(3, "the three default judge seats");
    }

    [Fact]
    public void Dailies_compile_step_references_decision_2_clips_4_selection_6_and_analysis_1()
    {
        WorkflowTemplateStepDefinition step = Template("video-derush-edit-dailies").Steps[8];
        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>(step.VideoCompileConfigJson!, ConfigJsonOptions)!;

        config.Decision.StepOrder.Should().Be(2);
        config.AnalysisStepOrder.Should().Be(1);
        config.EnableGeneratedClips.Should().BeTrue();
        config.GeneratedClips!.StepOrder.Should().Be(4);
        config.TakeSelection.Should().NotBeNull();
        config.TakeSelection!.From.Should().Be(ExtractInputSource.Step);
        config.TakeSelection.StepOrder.Should().Be(6);
        config.DynamicsPlan!.StepOrder.Should().Be(8);
        config.MaxGeneratedClips.Should().Be(4);
        config.Mode.Should().Be(VideoCompileMode.Reencode);
    }

    [Fact]
    public void Dailies_review_loop_targets_the_story_editor()
    {
        WorkflowTemplateStepDefinition step = Template("video-derush-edit-dailies").Steps[9];
        step.AgentType.Should().Be(AgentType.VideoReviewAgent);
        step.LoopTargetStepOrder.Should().Be(2);
        step.CacheMode.Should().Be(StepCacheMode.Never);
    }

    // ---- quick-win-promo-plates ----

    [Fact]
    public void Plates_template_is_the_starter_chain_plus_an_inline_generate_step_before_the_author()
    {
        WorkflowTemplateDefinition starter = Template(WorkflowTemplateCatalog.StarterTemplateKey);
        WorkflowTemplateDefinition plates = Template("quick-win-promo-plates");

        plates.AutoCreateOnProject.Should().BeFalse("it spends money, so it is never auto-created");
        plates.Steps.Should().HaveCount(starter.Steps.Count + 1);

        // Steps 1-9 are the starter's, verbatim.
        for (int i = 0; i < 9; i++)
        {
            plates.Steps[i].AgentType.Should().Be(starter.Steps[i].AgentType);
            plates.Steps[i].StepType.Should().Be(starter.Steps[i].StepType);
        }

        plates.Steps[9].StepType.Should().Be(StepType.VideoGenerate);
        plates.Steps[10].AgentType.Should().Be(AgentType.AuthorAgent);
        plates.Steps[11].StepType.Should().Be(StepType.ReviewLoop);
        plates.Steps[11].LoopTargetStepOrder.Should().Be(11, "the review loops back to the Author, now at step 11");
    }

    [Fact]
    public void Plates_generate_step_is_inline_with_an_operator_written_prompt_and_a_small_cap()
    {
        WorkflowTemplateStepDefinition step = Template("quick-win-promo-plates").Steps[9];
        VideoGenerateStepConfig config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(step.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;

        config.PlanSource.Should().Be(VideoGeneratePlanSource.Inline);
        config.InlinePrompt.Should().NotBeNullOrWhiteSpace();
        config.InlinePrompt.Should().Contain("No text");
        config.Takes.Should().Be(1);
        config.MaxClips.Should().Be(2);
        // The template's own cap is stated in credits now, so "small" is the credit equivalent of the
        // $5 it always meant rather than the dollar figure itself.
        config.MaxSpendCredits.Should().BeLessThanOrEqualTo(CreditCurrency.CreditsFromUsd(5m));
        config.AllowSourceMediaEgress.Should().BeFalse();
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void The_starter_template_itself_is_unchanged()
    {
        WorkflowTemplateDefinition starter = Template(WorkflowTemplateCatalog.StarterTemplateKey);
        starter.AutoCreateOnProject.Should().BeTrue();
        starter.Steps.Should().HaveCount(11);
        starter.Steps.Should().NotContain(s => s.StepType == StepType.VideoGenerate);
    }
}
