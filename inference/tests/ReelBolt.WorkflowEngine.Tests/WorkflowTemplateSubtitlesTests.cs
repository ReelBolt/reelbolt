using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The two subtitle templates (lane 3e): what they are made of, that their literals deserialize and save, and that the translation hand-off lines up.</summary>
public class WorkflowTemplateSubtitlesTests
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template(string key) =>
        WorkflowTemplateCatalog.GetByKey(key) ?? throw new Xunit.Sdk.XunitException($"template '{key}' is not registered");

    [Fact]
    public void The_plain_template_has_no_agent_and_burns_the_transcript_of_a_required_transcription()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.SubtitlesTemplateKey);

        template.Steps.Select(s => s.StepType).Should().Equal(StepType.VideoAnalyze, StepType.VideoCompile);
        template.Steps.Should().OnlyContain(s => s.AgentType == AgentType.VideoTransform, "the plain case calls no model");

        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(template.Steps[0].VideoAnalyzeConfigJson!, ConfigJsonOptions)!;
        analyze.Transcription.Should().Be(VideoTranscriptionMode.Required);

        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(template.Steps[1].VideoCompileConfigJson!, ConfigJsonOptions)!;
        compile.KeepWholeSources.Should().BeTrue();
        compile.Mode.Should().Be(VideoCompileMode.Reencode);
        compile.EnableCaptions.Should().BeTrue();
        compile.EnableVoiceover.Should().BeFalse();
        compile.CaptionSource.Should().Be(CaptionSource.Transcript);
        compile.CaptionTranslationStepOrder.Should().BeNull();
        compile.Validate().Should().BeEmpty();
    }

    [Fact]
    public void The_translated_template_adds_one_projection_and_one_translator_and_points_the_compile_at_it()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey);

        template.Steps.Select(s => s.StepType).Should().Equal(StepType.VideoAnalyze, StepType.Extract, StepType.Agent, StepType.VideoCompile);
        template.Steps[2].AgentType.Should().Be(AgentType.NarrationTranslator);
        template.Steps[2].AgentInputContextMode.Should().Be(AgentInputContextMode.PreviousStepOnly);

        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(template.Steps[3].VideoCompileConfigJson!, ConfigJsonOptions)!;
        compile.CaptionSource.Should().Be(CaptionSource.Transcript);
        compile.CaptionTranslationStepOrder.Should().Be(3, "the translator is step 3");
        compile.Validate().Should().BeEmpty();
    }

    [Fact]
    public void The_projection_reads_the_segments_of_the_analysis_view_and_keeps_their_ids()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey);
        ExtractStepConfig extract = JsonSerializer.Deserialize<ExtractStepConfig>(template.Steps[1].ExtractConfigJson!, ConfigJsonOptions)!;

        extract.Operation.Should().Be(ExtractOperation.Project);
        extract.Inputs["source"].StepOrder.Should().Be(1);
        extract.IdField.Should().Be("id");

        const string analysisOutput = """{"view":{"segments":[{"id":"t0","startSec":1,"endSec":2,"text":"Hello"},{"id":"t1","startSec":3,"endSec":4,"text":"World"}]},"meta":{}}""";
        List<string> items = ExpressionEvaluator.ExtractJsonArray(analysisOutput, extract.Path!);
        items.Select(i => JsonNode.Parse(i)!["id"]!.GetValue<string>()).Should().Equal("t0", "t1");
        extract.Fields.Should().Equal("text");
    }

    [Theory]
    [InlineData(WorkflowTemplateCatalog.SubtitlesTemplateKey)]
    [InlineData(WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey)]
    public void Each_template_is_an_opt_in_picker_entry_that_asks_for_a_clip(string key)
    {
        WorkflowTemplateDefinition template = Template(key);
        template.AutoCreateOnProject.Should().BeFalse();
        template.RequiresUserInput.Should().BeTrue("the customer picks the clip");
        template.Description.Should().Contain("SRT and VTT");
    }

    [Fact]
    public void The_translated_template_lets_a_long_segment_reach_the_translator_whole()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey);
        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(template.Steps[0].VideoAnalyzeConfigJson!, ConfigJsonOptions)!;
        ExtractStepConfig extract = JsonSerializer.Deserialize<ExtractStepConfig>(template.Steps[1].ExtractConfigJson!, ConfigJsonOptions)!;

        // The view the projection reads cuts a segment's text at MaxSegmentTextChars (160 by default): a translation of the
        // cut text would replace the whole segment.
        analyze.MaxSegmentTextChars.Should().BeGreaterThanOrEqualTo(1000);
        analyze.MaxViewSegments.Should().BeGreaterThan(400);
        extract.MaxOutputChars.Should().BeGreaterThanOrEqualTo(analyze.MaxOutputChars);

        string longText = string.Join(' ', Enumerable.Repeat("sentence", 40));
        string output = JsonSerializer.Serialize(new { view = new { segments = new[] { new { id = "t0", text = longText } } } });
        JsonNode.Parse(ExpressionEvaluator.ExtractJsonArray(output, extract.Path!).Single())!["text"]!.GetValue<string>().Should().Be(longText);
    }

    [Fact]
    public void A_translation_step_anywhere_before_the_compile_is_a_dependency_of_the_compile()
    {
        WorkflowStep Step(int order, StepType type, string? compile = null) => new() { Id = System.Guid.NewGuid(), StepOrder = order, StepType = type, VideoCompileConfigJson = compile };
        var graph = WorkflowStepDependencyGraph.Build(
        [
            Step(1, StepType.VideoAnalyze), Step(2, StepType.Agent), Step(3, StepType.Extract), Step(4, StepType.Agent),
            Step(5, StepType.VideoCompile, """{"version":1,"decision":{"from":"Step","stepOrder":1},"analysisStepOrder":1,"keepWholeSources":true,"enableCaptions":true,"captionSource":"Transcript","captionTranslationStepOrder":2}""")
        ]);

        graph.DependsOn(5).Keys.Should().Contain(2, "re-running the translation must invalidate the compile that burns it");
    }
}
