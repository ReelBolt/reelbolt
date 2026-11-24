using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-208: the <c>green-screen-insert</c> template puts a screen recording (or any video or image) onto a tracked green
/// plate with no agent and no model credit: a deterministic VideoAnalyze then a VideoCompile that keeps the footage whole.
/// </summary>
public class GreenScreenInsertTemplateTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template() =>
        WorkflowTemplateCatalog.GetByKey("green-screen-insert").Should().NotBeNull().And.Subject.As<WorkflowTemplateDefinition>();

    [Fact]
    public void Template_is_opt_in_and_is_two_deterministic_steps_with_no_agent()
    {
        WorkflowTemplateDefinition template = Template();

        template.AutoCreateOnProject.Should().BeFalse();
        template.Steps.Select(s => s.StepType).Should().Equal(StepType.VideoAnalyze, StepType.VideoCompile);
        template.Steps.Should().OnlyContain(s => s.AgentType == AgentType.VideoTransform);
    }

    [Fact]
    public void Analyze_step_tracks_plates_from_a_project_file_and_calls_no_provider()
    {
        VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(Template().Steps[0].VideoAnalyzeConfigJson!, Options)!;

        config.Source.Kind.Should().Be(VideoSourceKind.ProjectFile);
        config.Source.ProjectFileId.Should().BeNull("the plate video is filled in when the workflow is made");
        config.DetectInsertRegions.Should().BeTrue();
        config.Transcription.Should().Be(VideoTranscriptionMode.Off);
        config.Vision.Should().Be(VideoVisionMode.Off);
        config.CallsAnyProvider.Should().BeFalse();
    }

    [Fact]
    public void Compile_step_keeps_the_footage_whole_and_composites_the_referenced_content()
    {
        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>(Template().Steps[1].VideoCompileConfigJson!, Options)!;

        config.KeepWholeSources.Should().BeTrue();
        config.Mode.Should().Be(VideoCompileMode.Reencode);
        config.EnableInserts.Should().BeTrue();
        config.AnalysisStepOrder.Should().Be(1);
        config.InsertContentRef.Should().BeNull("a reference with no id fails save validation, so the content is added when the workflow is made");
    }

    [Fact]
    public void Both_step_configs_name_their_placeholder_so_a_template_picker_copy_says_what_to_fill_in()
    {
        // QA-P2-15: a copy applied from the picker (not through ProposeGreenScreenInsert) used to be
        // unrunnable with nothing saying which id it was missing. Each config now names the field.
        JsonObject analyze = JsonNode.Parse(Template().Steps[0].VideoAnalyzeConfigJson!)!.AsObject();
        JsonObject compile = JsonNode.Parse(Template().Steps[1].VideoCompileConfigJson!)!.AsObject();

        analyze["placeholders"]!["source.projectFileId"]!.GetValue<string>()
            .Should().Contain("green screen").And.Contain("source.projectFileId");
        compile["placeholders"]!["insertContentRef"]!.GetValue<string>()
            .Should().Contain("insertContentRef").And.Contain("projectFileId");

        // Naming is inert: both configs still read as the deterministic shapes the tests above pin, so
        // the step still calls no provider and still has no content reference to validate.
        JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(analyze.ToJsonString(), Options)!.CallsAnyProvider.Should().BeFalse();
        JsonSerializer.Deserialize<VideoCompileStepConfig>(compile.ToJsonString(), Options)!.InsertContentRef.Should().BeNull();

        // And the report the tool writes names the ids it filled in, so what is left is a real reference.
        analyze["source"] = new JsonObject { ["kind"] = "ProjectFile", ["projectFileId"] = Guid.NewGuid() };
        compile["insertContentRef"] = new JsonObject { ["from"] = "ProjectFile", ["projectFileId"] = Guid.NewGuid() };
        JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(analyze.ToJsonString(), Options)!.Source.ProjectFileId.Should().NotBeNull();
        JsonSerializer.Deserialize<VideoCompileStepConfig>(compile.ToJsonString(), Options)!.InsertContentRef!.ProjectFileId.Should().NotBeNull();
    }

    [Fact]
    public void RunRateClassifier_counts_the_template_as_free()
    {
        List<WorkflowStep> steps = Template().Steps.Select((s, i) => new WorkflowStep
        {
            StepOrder = i + 1,
            StepType = s.StepType,
            VideoAnalyzeConfigJson = s.VideoAnalyzeConfigJson,
            VideoCompileConfigJson = s.VideoCompileConfigJson
        }).ToList();

        RunRateClassifier.Classify(steps).Should().Be(RunRateBucket.Free);
    }
}
