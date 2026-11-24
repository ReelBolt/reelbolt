using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The sound-effects-only template: what it is made of, that nothing of a finished video is cut, and what it costs to find the moments.</summary>
public class WorkflowTemplateSoundEffectsTests
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template() =>
        WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.SoundEffectsTemplateKey)
        ?? throw new Xunit.Sdk.XunitException("the sound effects template is not registered");

    [Fact]
    public void It_analyses_plans_and_mixes_with_no_editor_and_no_music()
    {
        WorkflowTemplateDefinition template = Template();

        template.Steps.Select(s => s.StepType).Should().Equal(StepType.VideoAnalyze, StepType.Agent, StepType.VideoCompile);
        template.Steps.Select(s => s.AgentType).Should().Equal(AgentType.VideoTransform, AgentType.SoundDesigner, AgentType.VideoTransform);
        template.Steps[1].AgentInputContextMode.Should().Be(AgentInputContextMode.PreviousStepOnly, "there is no editing decision, only the analysis view");
        template.AutoCreateOnProject.Should().BeFalse();
        template.RequiresUserInput.Should().BeTrue("the customer picks the video");
    }

    [Fact]
    public void Finding_the_moments_is_done_by_code_and_only_the_looks_at_the_picture_call_a_provider()
    {
        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(Template().Steps[0].VideoAnalyzeConfigJson!, ConfigJsonOptions)!;

        analyze.AnalyzeEvents.Should().BeTrue();
        analyze.Outline.Should().BeTrue();
        analyze.OfferSfxClips.Should().BeTrue();
        analyze.IncludeSfxLibrary.Should().BeTrue();
        analyze.SfxWorthyEventsOnly.Should().BeTrue("a caption word or a beat is never offered for a sound");
        analyze.Transcription.Should().Be(VideoTranscriptionMode.Off);
        analyze.Vision.Should().Be(VideoVisionMode.Off);
        analyze.DescribeEvents.Should().BeTrue("the picture model says what each moment shows, when the workspace has one");
        analyze.MaxDescribedEvents.Should().BeNull("the length of the video decides how many looks");
        analyze.SfxOnFootage.Should().BeFalse();
        (analyze with { DescribeEvents = false }).CallsAnyProvider.Should().BeFalse("the moments themselves come from the picture, by code");
        analyze.CallsAnyProvider.Should().BeTrue("a look at the picture is a provider call, so the run is never counted as free");
        (analyze with { MaxDescribedEvents = 0 }).CallsAnyProvider.Should().BeFalse("the customer may switch the looks off with 0");
    }

    [Fact]
    public void The_compile_keeps_the_whole_video_and_only_adds_the_planned_sounds()
    {
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(Template().Steps[2].VideoCompileConfigJson!, ConfigJsonOptions)!;

        compile.KeepWholeSources.Should().BeTrue("a finished edit is never trimmed");
        compile.Mode.Should().Be(VideoCompileMode.Reencode);
        compile.EnableSfx.Should().BeTrue();
        compile.SfxPlan!.StepOrder.Should().Be(2, "the sound designer is step 2");
        compile.AnalysisStepOrder.Should().Be(1);
        compile.MaxSfxCues.Should().BeGreaterThanOrEqualTo(60, "a film full of graphics takes many cues");
        compile.EnableMusic.Should().BeFalse();
        compile.EnableCaptions.Should().BeFalse();
        compile.EnableGraphics.Should().BeFalse();
        compile.Validate().Should().BeEmpty();
    }

    [Fact]
    public void The_most_looks_field_goes_down_to_zero_and_its_help_says_what_zero_and_the_sound_designer_do()
    {
        AppManifestInput looks = Template().AppManifest!.Inputs.Single(i => i.Id == "mostLooks");

        looks.Min.Should().Be(0, "0 switches the looks off");
        looks.Max.Should().Be(ReelBolt.Shared.Workflows.VideoEvents.VisionLookDefaults.MaxEventLooks);
        looks.Help.Should().Contain("0 switches the looks off").And.Contain("up to 6 more");
    }

    [Fact]
    public void A_project_that_holds_only_the_finished_video_is_ready_because_the_built_in_sounds_are_always_offered()
    {
        WorkflowTemplateDefinition template = Template();
        ProjectReadinessSnapshot onlyTheVideo = new(
            VideoFiles: 1, AudioFiles: 0, ImageFiles: 0, CodeFiles: 0,
            new System.Collections.Generic.HashSet<InferenceProviderCapability>(), HasDesignProfile: false, HasSelectableVoice: false);

        (bool satisfied, System.Collections.Generic.IReadOnlyList<ReadinessMissing> missing) = RecipeFit.Evaluate(template, onlyTheVideo);

        missing.Should().BeEmpty();
        satisfied.Should().BeTrue("the customer's own sound files are optional");
        RecipeFit.Describe(template).Needs.Should().Contain("video clips").And.NotContain("sound-effect files");

        // A recipe that offers only the project's own sound files still asks for them.
        WorkflowTemplateDefinition ownFilesOnly = WorkflowTemplateCatalog.GetByKey("video-derush-edit-sfx")!;
        RecipeFit.Describe(ownFilesOnly).Needs.Should().Contain("sound-effect files");
        RecipeFit.Evaluate(ownFilesOnly, onlyTheVideo).Missing.Select(m => m.Need).Should().Contain("sfx");
    }

    [Fact]
    public void It_ships_a_curated_app_page_and_is_found_by_its_goal()
    {
        AppManifest manifest = WorkflowTemplateAppManifests.ByKey[WorkflowTemplateCatalog.SoundEffectsTemplateKey];

        manifest.Title.Should().Be("Sound effects for your video");
        manifest.Inputs.Should().Contain(i => i.Kind == AppManifestVocabulary.KindFiles && i.Role == "video" && i.Max == 1);
        manifest.Category.Should().Be("footage");

        // "add sound effects to my finished video" must land here, not on the recipe that cuts the footage down first
        RecipeFitInfo Info(string key) => RecipeFit.Describe(WorkflowTemplateCatalog.GetByKey(key)!);
        const string goal = "add sound effects to my finished video without cutting it";
        RecipeFit.GoalScore(Info(WorkflowTemplateCatalog.SoundEffectsTemplateKey), goal)
            .Should().BeGreaterThan(RecipeFit.GoalScore(Info("video-derush-edit-sfx"), goal));
    }
}
