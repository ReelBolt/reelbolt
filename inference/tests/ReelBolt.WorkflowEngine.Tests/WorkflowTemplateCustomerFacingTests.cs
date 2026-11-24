using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The template catalog as a CUSTOMER sees it (findings 10, 11, 18, 26, 32, 33 of the 2026-10-04
/// customer run), plus the story-first <c>narrated-story</c> template and the narrated promo
/// templates. Catalog-wide facts are asserted over <see cref="WorkflowTemplateCatalog.GetAll"/> so a
/// future template inherits them.
/// </summary>
public class WorkflowTemplateCustomerFacingTests
{
    private static readonly JsonSerializerOptions CompileOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template(string key)
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.BaseByKey(key);
        template.Should().NotBeNull($"template '{key}' must be registered");
        return template!;
    }

    private static int IndexOf(WorkflowTemplateDefinition template, StepType stepType) =>
        template.Steps.Select((s, i) => (s, i)).Single(x => x.s.StepType == stepType).i;

    // ---------------------------------------------------------------- catalog-wide, customer copy

    [Fact]
    public void Every_template_has_a_customer_facing_name_and_description_without_pipeline_jargon()
    {
        string[] jargon = ["Derush", "Opt-in", "VideoAnalyze", "VideoCompile", "opaque", "Demonstrates", "ffmpeg", "Phase", "EditRoom", "StepRef", " v1"];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.BaseTemplates)
        {
            template.Name.Should().NotBeNullOrWhiteSpace();
            template.Description.Should().NotBeNullOrWhiteSpace();
            template.Description.Length.Should().BeLessThan(450, $"{template.Key}'s description is a picker blurb, not documentation");
            foreach (string word in jargon)
            {
                template.Name.Should().NotContain(word, $"{template.Key}'s name is read by customers");
                template.Description.Should().NotContain(word, $"{template.Key}'s description is read by customers");
            }
        }

        WorkflowTemplateCatalog.BaseTemplates.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_template_asks_for_the_customers_brief_when_it_runs()
    {
        // Findings 18/26: the auto-created starter and the voiceover template ran with no way to say
        // what video was wanted. Every shipped template's agents read the "User Request" section.
        // The build template is the one exception: it creates a profile and never takes a brief. The
        // green-screen insert is the other: no model reads notes, so a person is not forced to type any.
        WorkflowTemplateCatalog.BaseTemplates
            .Where(t => !t.RequiresUserInput && t.Key != WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey
                && t.Key != WorkflowTemplateCatalog.GreenScreenInsertTemplateKey)
            .Select(t => t.Key)
            .Should().BeEmpty("the build template creates a profile; it does not take a brief");
    }

    [Theory]
    [InlineData("video-derush-edit-music", "music track")]
    public void The_music_template_says_up_front_there_is_no_built_in_library(string key, string what)
    {
        string description = Template(key).Description;
        description.Should().Contain(what);
        description.Should().Contain("no built-in", "finding 32: the customer must know to upload audio first");
        description.Should().Contain("project files first");
    }

    [Fact]
    public void The_sfx_template_points_to_the_built_in_sound_library_or_an_upload()
    {
        string description = Template("video-derush-edit-sfx").Description;
        description.Should().Contain("sound effects");
        description.Should().Contain("built-in sound effects library");
        description.Should().Contain("upload your own");
        description.Should().NotContain("no built-in");
    }

    [Theory]
    [InlineData("video-derush-edit-pickups")]
    [InlineData("video-derush-edit-dub")]
    public void Pickups_and_dub_templates_lead_with_the_footage_they_need(string key)
    {
        // Finding 33: these need dialogue footage, and the description says so FIRST.
        Template(key).Description.Should().StartWith("Needs ").And.Contain("speech");
    }

    [Fact]
    public void Template_keys_are_unchanged_so_tests_and_stored_workflows_still_resolve()
    {
        WorkflowTemplateCatalog.BaseTemplates.Select(t => t.Key).Should().Contain(
        [
            WorkflowTemplateCatalog.StarterTemplateKey, "lean-context-promo", "cinematic-feature-spotlight",
            "story-arc-launch-trailer", "video-derush-edit", "video-derush-edit-graphics", "video-derush-edit-music",
            "video-derush-edit-room", "video-derush-edit-graphics-room", "video-derush-edit-grade-room",
            "video-derush-edit-sfx", "video-derush-edit-broll", "video-derush-edit-voiceover",
            "video-derush-edit-pickups", "video-derush-edit-dub", WorkflowTemplateCatalog.NarratedStoryTemplateKey
        ]);
        WorkflowTemplateCatalog.BaseTemplates.Select(t => t.Key).Should().OnlyHaveUniqueItems();
    }

    // ---------------------------------------------------------------- catalog-wide, wiring

    [Fact]
    public void Every_template_passes_the_same_save_time_validation_a_hand_built_workflow_does()
    {
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.BaseTemplates)
        {
            IEnumerable<CreateWorkflowStepRequest> steps = template.Steps.Select((s, i) => new CreateWorkflowStepRequest(
                AgentDefinitionId: Guid.NewGuid(),
                StepOrder: i + 1,
                EdgeConditionJson: null,
                Label: s.Label,
                StepType: s.StepType.ToString(),
                LoopTargetStepOrder: s.LoopTargetStepOrder,
                MaxIterations: s.MaxIterations,
                MinScore: s.MinScore,
                ExtractConfigJson: s.ExtractConfigJson,
                VideoAnalyzeConfigJson: s.VideoAnalyzeConfigJson,
                VideoCompileConfigJson: s.VideoCompileConfigJson,
                EditRoomConfigJson: s.EditRoomConfigJson,
                GraphicsRoomConfigJson: s.GraphicsRoomConfigJson,
                ColorGradeRoomConfigJson: s.ColorGradeRoomConfigJson,
                VideoGenerateConfigJson: s.VideoGenerateConfigJson,
                VoiceoverConfigJson: s.VoiceoverConfigJson,
                DailiesRoomConfigJson: s.DailiesRoomConfigJson,
                ConditionExpression: s.ConditionExpression,
                TrueBranchStepOrder: s.TrueBranchStepOrder,
                FalseBranchStepOrder: s.FalseBranchStepOrder,
                DesignSystemConfigJson: s.DesignSystemConfigJson));

            StepConfigSaveValidator.Validate(steps).Should().BeEmpty($"template '{template.Key}' must be savable as shipped");
        }
    }

    [Fact]
    public void Every_voiceover_step_reads_an_earlier_step_of_the_agent_its_source_kind_expects()
    {
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.BaseTemplates)
        {
            for (int i = 0; i < template.Steps.Count; i++)
            {
                if (template.Steps[i].StepType != StepType.Voiceover)
                    continue;

                VoiceoverStepConfig config = JsonSerializer.Deserialize<VoiceoverStepConfig>(
                    template.Steps[i].VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;
                (int? order, AgentType[] expected) = config.Source.Kind switch
                {
                    VoiceoverSourceKind.ScriptScenes => (config.Source.ScriptwriterStepOrder, new[] { AgentType.ScriptwriterAgent }),
                    VoiceoverSourceKind.NarrationPlan => (config.Source.NarrationWriterStepOrder, new[] { AgentType.NarrationWriter, AgentType.NarrationTranslator }),
                    VoiceoverSourceKind.PickupPlan => (config.Source.PickupPlannerStepOrder, new[] { AgentType.PickupPlanner }),
                    _ => ((int?)null, Array.Empty<AgentType>())
                };

                order.Should().NotBeNull($"{template.Key} step {i + 1}");
                order!.Value.Should().BeInRange(1, i, $"{template.Key} step {i + 1} must read an EARLIER step");
                template.Steps[order.Value - 1].AgentType.Should().BeOneOf(expected, $"{template.Key} step {i + 1} reads step {order}");
            }
        }
    }

    [Fact]
    public void Every_review_loop_re_runs_a_window_that_contains_the_step_it_judges()
    {
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.BaseTemplates)
        {
            // The build template creates a profile and has nothing to review; every template that
            // renders something still goes through exactly the window assertion below.
            if (!template.Steps.Any(s => s.StepType == StepType.ReviewLoop))
                continue;

            int reviewIndex = IndexOf(template, StepType.ReviewLoop);
            int target = template.Steps[reviewIndex].LoopTargetStepOrder!.Value;

            // The judged artefact is the step immediately before the review; the re-run window is
            // [target, review). A target shifted by an inserted step would loop without re-rendering.
            target.Should().BeInRange(1, reviewIndex, template.Key);
            template.Steps[target - 1].StepType.Should().NotBe(StepType.Voiceover, $"{template.Key} must loop back to a decision, not a recording");
        }
    }

    // ---------------------------------------------------------------- narrated promo templates

    [Theory]
    [InlineData("cinematic-feature-spotlight", 6)]
    [InlineData("story-arc-launch-trailer", 5)]
    public void Narrated_promo_templates_speak_the_script_before_the_director_and_loop_back_to_the_author(string key, int scriptwriterOrder)
    {
        // Finding 50: the 15 s vertical reel shipped with no audio track at all. The promo path's
        // only audio is the narration the Author stages, so these templates record it.
        WorkflowTemplateDefinition template = Template(key);
        int voiceoverIndex = IndexOf(template, StepType.Voiceover);

        template.Steps[scriptwriterOrder - 1].AgentType.Should().Be(AgentType.ScriptwriterAgent);
        voiceoverIndex.Should().Be(scriptwriterOrder, "the voiceover step sits right after the scriptwriter");
        template.Steps[voiceoverIndex].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[voiceoverIndex].CacheMode.Should().BeNull("a deterministic, content-addressed step stays cacheable");

        VoiceoverStepConfig config = JsonSerializer.Deserialize<VoiceoverStepConfig>(
            template.Steps[voiceoverIndex].VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;
        config.Source.Kind.Should().Be(VoiceoverSourceKind.ScriptScenes);
        config.Source.ScriptwriterStepOrder.Should().Be(scriptwriterOrder);

        int directorIndex = template.Steps.Select((s, i) => (s, i)).Single(x => x.s.AgentType == AgentType.DirectorAgent).i;
        directorIndex.Should().BeGreaterThan(voiceoverIndex, "audio-first timing: the director reads the measured line lengths");

        int reviewIndex = IndexOf(template, StepType.ReviewLoop);
        template.Steps[template.Steps[reviewIndex].LoopTargetStepOrder!.Value - 1].AgentType.Should().Be(AgentType.AuthorAgent);
    }

    [Theory]
    [InlineData(WorkflowTemplateCatalog.StarterTemplateKey)]
    [InlineData("lean-context-promo")]
    public void Silent_promo_templates_say_so_in_their_description(string key)
    {
        // These two stay without a Voiceover step (a deployment with no speech provider must still be
        // able to run the auto-created starter), so the customer is told the result is silent.
        WorkflowTemplateDefinition template = Template(key);
        template.Steps.Should().NotContain(s => s.StepType == StepType.Voiceover);
        template.Description.Should().Contain("Silent unless you add a Voiceover step");
    }

    // ---------------------------------------------------------------- narrated-story

    [Fact]
    public void Narrated_story_writes_the_script_before_the_editor_cuts_to_it()
    {
        WorkflowTemplateDefinition t = Template(WorkflowTemplateCatalog.NarratedStoryTemplateKey);
        t.Name.Should().Be("Narrated product story");
        t.RequiresUserInput.Should().BeTrue("the brief is what the story is written from");
        t.AutoCreateOnProject.Should().BeFalse();
        t.Steps.Should().HaveCount(7);

        t.Steps[0].StepType.Should().Be(StepType.VideoAnalyze);
        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(t.Steps[0].VideoAnalyzeConfigJson!, CompileOptions)!;
        analyze.Source.Kind.Should().Be(VideoSourceKind.ProjectFile, "the first step has no previous output to read");
        analyze.Vision.Should().Be(VideoVisionMode.Optional, "the writer picks shots by what they show, and no Vision provider must not fail the run");

        // Story FIRST: the narration writer is step 2, the editor step 3.
        t.Steps[1].AgentType.Should().Be(AgentType.NarrationWriter);
        t.Steps[1].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        t.Steps[1].CacheMode.Should().Be(StepCacheMode.Never);
        t.Steps[2].AgentType.Should().Be(AgentType.VideoStoryEditor);
        t.Steps[2].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow, "the editor needs the view AND the script");
        t.Steps[2].CacheMode.Should().Be(StepCacheMode.Never);

        VoiceoverStepConfig voiceover = JsonSerializer.Deserialize<VoiceoverStepConfig>(t.Steps[3].VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;
        voiceover.Source.Kind.Should().Be(VoiceoverSourceKind.NarrationPlan);
        voiceover.Source.NarrationWriterStepOrder.Should().Be(2);
        StepConfigSaveValidator.ValidateVoiceoverConfig(t.Steps[3].VoiceoverConfigJson).Should().BeEmpty();

        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(t.Steps[5].VideoCompileConfigJson!, CompileOptions)!;
        compile.Decision.From.Should().Be(ExtractInputSource.Step);
        compile.Decision.StepOrder.Should().Be(3, "the decision is the editor's, never the script's");
        compile.AnalysisStepOrder.Should().Be(1);
        compile.EnableVoiceover.Should().BeTrue();
        compile.VoiceoverStepOrder.Should().Be(4);
        compile.EnableCaptions.Should().BeTrue("captions are on by default for narrated video");
        compile.CaptionStyle.Should().Be(CaptionStyle.Karaoke);
        compile.Mode.Should().Be(VideoCompileMode.Reencode);
        compile.OutputFormat.Should().Be(VideoOutputFormat.Source, "left for the customer to choose in the compile step");

        t.Steps[6].StepType.Should().Be(StepType.ReviewLoop);
        t.Steps[6].AgentType.Should().Be(AgentType.VideoReviewAgent);
        t.Steps[6].LoopTargetStepOrder.Should().Be(2, "a low score rewrites the story, then re-cuts and re-records it");
        t.Steps[6].MinScore.Should().BeGreaterThan(ReelBolt.WorkflowEngine.Execution.StepExecutors.ReviewLoopStepExecutor.NarrationOverflowScoreCap);
    }

    [Fact]
    public void Adding_narrated_story_left_the_existing_voiceover_template_cutting_before_it_narrates()
    {
        WorkflowTemplateDefinition t = Template("video-derush-edit-voiceover");
        t.Steps[1].AgentType.Should().Be(AgentType.VideoStoryEditor);
        t.Steps[2].AgentType.Should().Be(AgentType.NarrationWriter);
    }
}
