using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

public class RunStyleOverridesTests
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static VideoCompileStepConfig Compile() =>
        new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1);

    private static WorkflowExecution Execution(StyleConfig? style = null, RunOutputFormat? format = null) => new()
    {
        Id = Guid.NewGuid(),
        StyleConfigJson = style is null ? null : StyleJson.Serialize(new RunStyleSnapshot("s", null, null, style)),
        OutputFormatOverride = format
    };

    [Fact]
    public void A_run_without_choices_returns_the_very_same_config()
    {
        VideoCompileStepConfig config = Compile();

        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(config, Execution(), out var applied);

        result.Should().BeSameAs(config);
        applied.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RunOutputFormat.Vertical, VideoOutputFormat.Vertical)]
    [InlineData(RunOutputFormat.Square, VideoOutputFormat.Square)]
    [InlineData(RunOutputFormat.Portrait, VideoOutputFormat.Portrait)]
    public void A_portrait_or_square_override_sets_the_format_and_reframes_the_default_crop(RunOutputFormat run, VideoOutputFormat expected)
    {
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(Compile(), Execution(format: run), out var applied);

        result.OutputFormat.Should().Be(expected);
        result.OutputFit.Should().Be(VideoOutputFit.Subject);
        applied.Should().Contain($"outputFormat={expected}");
    }

    [Fact]
    public void A_landscape_override_keeps_the_fit()
    {
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(Compile(), Execution(format: RunOutputFormat.Landscape), out _);

        result.OutputFormat.Should().Be(VideoOutputFormat.Landscape);
        result.OutputFit.Should().Be(VideoOutputFit.Crop);
    }

    [Fact]
    public void A_step_that_chose_pad_keeps_pad()
    {
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(
            Compile() with { OutputFit = VideoOutputFit.Pad }, Execution(format: RunOutputFormat.Vertical), out _);

        result.OutputFit.Should().Be(VideoOutputFit.Pad);
    }

    [Fact]
    public void A_step_that_explicitly_chose_crop_keeps_crop()
    {
        // Production run a76cd793: a step patched to outputFit "Crop" still reported fit Subject, because the
        // Vertical run format turned the (indistinguishable) default Crop into Subject.
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(
            Compile(), Execution(format: RunOutputFormat.Vertical), out var applied, outputFitSetByStep: true);

        result.OutputFit.Should().Be(VideoOutputFit.Crop);
        result.OutputFormat.Should().Be(VideoOutputFormat.Vertical);
        applied.Should().NotContain("outputFit=Subject");
    }

    [Theory]
    [InlineData("{\"outputFit\":\"Crop\"}", true)]
    [InlineData("{\"OutputFit\":\"Crop\",\"version\":1}", true)]
    [InlineData("{\"outputFit\":\"Pad\"}", true)]
    [InlineData("{\"outputFit\":null}", false)]
    [InlineData("{\"version\":1}", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Whether_the_step_set_its_fit_is_read_from_the_stored_json(string? json, bool expected) =>
        RunStyleOverrides.OutputFitSetByStep(json).Should().Be(expected);

    [Fact]
    public void A_format_override_turns_a_stream_copy_step_into_a_reencode()
    {
        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(
            Compile() with { Mode = VideoCompileMode.StreamCopy }, Execution(format: RunOutputFormat.Square), out var applied);

        result.Mode.Should().Be(VideoCompileMode.Reencode);
        applied.Should().Contain("mode=Reencode");
    }

    [Fact]
    public void Smooth_transitions_turn_an_off_policy_into_auto_but_keep_a_chosen_one()
    {
        WorkflowExecution smooth = Execution(new StyleConfig(Editing: StyleEditing.SmoothTransitions));

        RunStyleOverrides.ApplyToCompile(Compile(), smooth, out _).TransitionPolicy.Should().Be(VideoTransitionPolicy.Auto);
        RunStyleOverrides.ApplyToCompile(Compile() with { TransitionPolicy = VideoTransitionPolicy.AudioOnly }, smooth, out _)
            .TransitionPolicy.Should().Be(VideoTransitionPolicy.AudioOnly);
        RunStyleOverrides.ApplyToCompile(Compile(), Execution(new StyleConfig(Editing: StyleEditing.JumpCuts)), out _)
            .TransitionPolicy.Should().Be(VideoTransitionPolicy.Off);
    }

    [Fact]
    public void A_caption_look_restyles_burned_captions_but_never_turns_them_on()
    {
        WorkflowExecution punch = Execution(new StyleConfig(Captions: StyleCaptions.Punch));

        RunStyleOverrides.ApplyToCompile(Compile() with { EnableCaptions = true }, punch, out _)
            .CaptionStyle.Should().Be(CaptionStyle.Punch);

        VideoCompileStepConfig off = RunStyleOverrides.ApplyToCompile(Compile(), punch, out _);
        off.EnableCaptions.Should().BeFalse();
        off.CaptionStyle.Should().Be(CaptionStyle.Subtitle);

        RunStyleOverrides.ApplyToCompile(Compile() with { EnableCaptions = true, CaptionStyle = CaptionStyle.Karaoke }, punch, out _)
            .CaptionStyle.Should().Be(CaptionStyle.Karaoke, "a caption style the step chose is kept");
    }

    [Fact]
    public void Portrait_has_a_fixed_4_by_5_canvas()
    {
        VideoCompileStepExecutor.FixedCanvasSize(VideoOutputFormat.Portrait).Should().Be((1080, 1350));
    }

    private static List<WorkflowStep> StepsWithCompile(VideoOutputFormat format) =>
    [
        new WorkflowStep { StepOrder = 1, StepType = StepType.VideoAnalyze },
        new WorkflowStep
        {
            StepOrder = 3,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(Compile() with { OutputFormat = format }, ConfigJson)
        }
    ];

    [Fact]
    public void The_target_format_is_the_override_else_the_first_fixed_compile_format()
    {
        RunStyleOverrides.ResolveTargetFormat(Execution(), StepsWithCompile(VideoOutputFormat.Source)).Should().BeNull();
        RunStyleOverrides.ResolveTargetFormat(Execution(), StepsWithCompile(VideoOutputFormat.Square)).Should().Be(RunOutputFormat.Square);
        RunStyleOverrides.ResolveTargetFormat(Execution(format: RunOutputFormat.Vertical), StepsWithCompile(VideoOutputFormat.Square))
            .Should().Be(RunOutputFormat.Vertical);
    }

    private static VideoAnalysisArtifact Artifact(params (int W, int H)[] sources) =>
        new(
            Version: 2,
            Media: new VideoAnalysisMedia(10, 30, 1, sources[0].W, sources[0].H),
            Shots: [],
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: [],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: sources.Select((s, i) => new VideoAnalysisSourceInfo(i, $"k{i}", new VideoAnalysisMedia(10, 30, 1, s.W, s.H))).ToList());

    [Fact]
    public void A_single_source_view_without_a_target_is_left_untouched()
    {
        var view = new JsonObject { ["shots"] = new JsonArray() };
        string before = view.ToJsonString();

        RunStyleOverrides.AnnotateOrientation(view, Artifact((1920, 1080)), Execution(), StepsWithCompile(VideoOutputFormat.Source));

        view.ToJsonString().Should().Be(before);
    }

    [Fact]
    public void A_target_marks_which_sources_match_it()
    {
        var view = new JsonObject();

        RunStyleOverrides.AnnotateOrientation(
            view, Artifact((1920, 1080), (1080, 1920), (1080, 1080)), Execution(format: RunOutputFormat.Vertical), []);

        JsonNode orientation = view["orientation"]!;
        orientation["target"]!["format"]!.GetValue<string>().Should().Be("Vertical");
        orientation["target"]!["orientation"]!.GetValue<string>().Should().Be("portrait");
        JsonArray sources = orientation["sources"]!.AsArray();
        sources.Select(s => s!["orientation"]!.GetValue<string>()).Should().Equal("landscape", "portrait", "square");
        sources.Select(s => s!["matchesTarget"]!.GetValue<bool>()).Should().Equal(false, true, false);
        orientation["preferSrc"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(1);
    }

    [Fact]
    public void Several_sources_get_their_orientation_even_without_a_target()
    {
        var view = new JsonObject();

        RunStyleOverrides.AnnotateOrientation(view, Artifact((1920, 1080), (1080, 1920)), Execution(), []);

        JsonNode orientation = view["orientation"]!;
        orientation["target"].Should().BeNull();
        orientation["preferSrc"].Should().BeNull();
        orientation["sources"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void A_single_source_with_a_target_gets_a_source_entry()
    {
        var view = new JsonObject();

        RunStyleOverrides.AnnotateOrientation(view, Artifact((1920, 1080)), Execution(), StepsWithCompile(VideoOutputFormat.Vertical));

        view["orientation"]!["source"]!["matchesTarget"]!.GetValue<bool>().Should().BeFalse();
        view["orientation"]!["source"]!["aspect"]!.GetValue<double>().Should().Be(1.78);
    }

    [Fact]
    public void The_cache_component_is_absent_without_choices_and_distinguishes_styles_and_formats()
    {
        List<WorkflowStep> steps = StepsWithCompile(VideoOutputFormat.Source);

        RunStyleOverrides.CacheKeyComponent(Execution(), steps).Should().BeNull();

        string? fast = RunStyleOverrides.CacheKeyComponent(Execution(new StyleConfig(Pacing: StylePacing.Fast)), steps);
        string? slow = RunStyleOverrides.CacheKeyComponent(Execution(new StyleConfig(Pacing: StylePacing.Slow)), steps);
        string? vertical = RunStyleOverrides.CacheKeyComponent(Execution(format: RunOutputFormat.Vertical), steps);

        new[] { fast, slow, vertical }.Should().OnlyHaveUniqueItems().And.NotContainNulls();
        RunStyleOverrides.CacheKeyComponent(Execution(), StepsWithCompile(VideoOutputFormat.Square)).Should().NotBeNull(
            "an analysis annotated with a target must not be served to a run cut for another shape");
    }

    [Fact]
    public void The_style_name_does_not_change_the_cache_component()
    {
        var config = new StyleConfig(Pacing: StylePacing.Fast);
        var a = new WorkflowExecution { StyleConfigJson = StyleJson.Serialize(new RunStyleSnapshot("A", null, null, config)) };
        var b = new WorkflowExecution { StyleConfigJson = StyleJson.Serialize(new RunStyleSnapshot("B", "preset", Guid.NewGuid(), config)) };

        RunStyleOverrides.CacheKeyComponent(a, []).Should().Be(RunStyleOverrides.CacheKeyComponent(b, []));
    }

    [Fact]
    public void A_run_style_changes_the_step_cache_key_and_its_absence_does_not_add_a_field()
    {
        var builder = new StepCacheKeyBuilder();
        var inputs = new StepCacheKeyInputs
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.Agent,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 0,
            ProjectFileFingerprint = "fp"
        };

        string plain = builder.Build(inputs);
        builder.Build(inputs with { RunStyle = null }).Should().Be(plain);
        builder.Build(inputs with { RunStyle = "style=-|format=Vertical|target=Vertical" }).Should().NotBe(plain);
        builder.Build(inputs with { RunStyle = string.Empty }).Should().NotBe(plain, "an empty component is still a component");
    }
}
