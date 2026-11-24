using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The two phase-4 templates (docs/voiceover.md "Pickups" / "Dubs") ship hand-authored JSON
/// literals; this pins that every literal deserializes into the typed config it claims to be,
/// passes the same save-time validation the API applies, and wires each step at the order the
/// surrounding steps actually occupy.
/// </summary>
public class VideoWorkflowTemplatePickupsAndDubTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template(string key) =>
        WorkflowTemplateCatalog.GetAll().Single(t => t.Key == key);

    [Fact]
    public void Pickups_template_wires_planner_voiceover_and_compile_at_the_orders_they_occupy()
    {
        WorkflowTemplateDefinition t = Template("video-derush-edit-pickups");
        t.AutoCreateOnProject.Should().BeFalse("opt-in, like every other video template");
        t.Steps.Should().HaveCount(7);
        t.Steps[2].AgentType.Should().Be(AgentType.PickupPlanner);

        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(t.Steps[0].VideoAnalyzeConfigJson!, Options)!;
        analyze.Transcription.Should().Be(VideoTranscriptionMode.Required, "pickups need the words of every t{n} segment");

        VoiceoverStepConfig voiceover = JsonSerializer.Deserialize<VoiceoverStepConfig>(t.Steps[3].VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;
        voiceover.Source.Kind.Should().Be(VoiceoverSourceKind.PickupPlan);
        voiceover.Source.PickupPlannerStepOrder.Should().Be(3);
        StepConfigSaveValidator.ValidateVoiceoverConfig(t.Steps[3].VoiceoverConfigJson).Should().BeEmpty();

        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(t.Steps[5].VideoCompileConfigJson!, Options)!;
        compile.EnablePickups.Should().BeTrue();
        compile.PickupVoiceoverStepOrder.Should().Be(4);
        compile.Decision.StepOrder.Should().Be(2);
        compile.AnalysisStepOrder.Should().Be(1);
        compile.Mode.Should().Be(VideoCompileMode.Reencode, "pickups need the audio filtergraph");
        compile.EnableVoiceover.Should().BeFalse("the pickups are spliced by the pickup path, not mixed as narration");

        t.Steps[6].StepType.Should().Be(StepType.ReviewLoop);
        t.Steps[6].LoopTargetStepOrder.Should().Be(2);
    }

    [Fact]
    public void Dub_template_translates_the_plan_speaks_it_replaces_the_dialogue_and_burns_captions()
    {
        WorkflowTemplateDefinition t = Template("video-derush-edit-dub");
        t.RequiresUserInput.Should().BeTrue("the target language arrives in the run's request");
        t.Steps.Should().HaveCount(8);
        t.Steps[2].AgentType.Should().Be(AgentType.NarrationWriter);
        t.Steps[3].AgentType.Should().Be(AgentType.NarrationTranslator);
        t.Steps[3].AgentInputContextMode.Should().Be(AgentInputContextMode.PreviousStepOnly);

        VoiceoverStepConfig voiceover = JsonSerializer.Deserialize<VoiceoverStepConfig>(t.Steps[4].VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;
        voiceover.Source.Kind.Should().Be(VoiceoverSourceKind.NarrationPlan);
        voiceover.Source.NarrationWriterStepOrder.Should().Be(4, "the TRANSLATED plan, not the original");
        StepConfigSaveValidator.ValidateVoiceoverConfig(t.Steps[4].VoiceoverConfigJson).Should().BeEmpty();

        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(t.Steps[6].VideoCompileConfigJson!, Options)!;
        compile.EnableVoiceover.Should().BeTrue();
        compile.VoiceoverStepOrder.Should().Be(5);
        compile.VoiceoverMode.Should().Be(VoiceoverMode.ReplaceDialogue);
        compile.EnableCaptions.Should().BeTrue();
        compile.CaptionStyle.Should().Be(CaptionStyle.Subtitle);
        compile.Decision.StepOrder.Should().Be(2);
    }

    [Fact]
    public void Every_template_key_is_unique_and_the_new_ones_are_present()
    {
        IReadOnlyList<WorkflowTemplateDefinition> all = WorkflowTemplateCatalog.GetAll();
        all.Select(t => t.Key).Should().OnlyHaveUniqueItems();
        all.Select(t => t.Key).Should().Contain(["video-derush-edit-voiceover", "video-derush-edit-pickups", "video-derush-edit-dub"]);
    }
}
