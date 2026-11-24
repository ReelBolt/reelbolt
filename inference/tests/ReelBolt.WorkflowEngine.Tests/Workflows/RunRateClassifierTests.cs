using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

public class RunRateClassifierTests
{
    // Every StepType needs a deliberate decision. A new member without one fails
    // Every_step_type_has_a_deliberate_decision below.
    private static readonly Dictionary<StepType, RunRateBucket> Expected = new()
    {
        [StepType.Agent] = RunRateBucket.Paid,
        [StepType.Conditional] = RunRateBucket.Paid, // no config: cannot prove Expression mode
        [StepType.ForEach] = RunRateBucket.Paid,
        [StepType.ReviewLoop] = RunRateBucket.Paid,
        [StepType.Parallel] = RunRateBucket.Paid,
        [StepType.Extract] = RunRateBucket.Free,
        [StepType.VideoAnalyze] = RunRateBucket.Paid, // default config transcribes (Optional)
        [StepType.VideoCompile] = RunRateBucket.Free,
        [StepType.EditRoom] = RunRateBucket.Paid,
        [StepType.GraphicsRoom] = RunRateBucket.Paid,
        [StepType.ColorGradeRoom] = RunRateBucket.Paid,
        [StepType.VideoGenerate] = RunRateBucket.Paid,
        [StepType.Voiceover] = RunRateBucket.Paid,
        [StepType.ThumbnailGenerate] = RunRateBucket.Free,
        [StepType.Revise] = RunRateBucket.Paid,
        [StepType.DesignGraphic] = RunRateBucket.Paid,
        [StepType.DailiesRoom] = RunRateBucket.Paid,
        [StepType.TimelineRender] = RunRateBucket.Free,
        [StepType.DesignSystem] = RunRateBucket.Free,
        [StepType.ExtractFrame] = RunRateBucket.Free, // one local ffmpeg frame grab, no provider call
        [StepType.AudioGenerate] = RunRateBucket.Paid, // T-108: every take is a paid music/SFX vendor call
        [StepType.PinInsert] = RunRateBucket.Free, // T-206: resolves references and writes a plan, no provider call
        [StepType.ImageGenerate] = RunRateBucket.Paid, // every picture is a paid image vendor call
        [StepType.AudioCleanup] = RunRateBucket.Paid, // no config: cannot prove a local target (the config decides, see the AudioCleanup tests below)
        [StepType.VideoEnhance] = RunRateBucket.Paid, // decided by the config: a LOCAL operation is Free (see VideoEnhance_run_rate below); no or unreadable config is Paid
    };

    private const string OfflineAnalyze =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off","derushPrePass":"Off","screenInstructionLikeSpans":false}""";

    private static WorkflowStep Step(StepType type, string? analyzeJson = null) =>
        new() { StepType = type, VideoAnalyzeConfigJson = analyzeJson };

    private static WorkflowStep Conditional(string? conditionalJson) =>
        new() { StepType = StepType.Conditional, ConditionalConfigJson = conditionalJson };

    private const string ExpressionConditional = """{"version":1,"mode":"Expression"}""";

    /// <summary>A VideoAnalyze config with every provider-calling field off; each case below turns ONE back on.</summary>
    private static VideoAnalyzeStepConfig AllOff() =>
        new(1, new VideoSourceRef(VideoSourceKind.ProjectFile),
            Transcription: VideoTranscriptionMode.Off,
            Vision: VideoVisionMode.Off,
            DerushPrePass: VideoDerushPrePassMode.Off,
            ScreenInstructionLikeSpans: false,
            TrackObjects: null,
            LocateSubjects: false);

    private static readonly VideoInsertSurfaceQuad DrawnRegion = new(0.1, 0.1, 0.2, 0.1, 0.1, 0.2, 0.2, 0.2);

    public static IEnumerable<object[]> ProviderCallingFlagsAlone() => new (string Name, VideoAnalyzeStepConfig Config)[]
    {
        ("transcription Optional", AllOff() with { Transcription = VideoTranscriptionMode.Optional }),
        ("transcription Required", AllOff() with { Transcription = VideoTranscriptionMode.Required }),
        ("transcription undefined member", AllOff() with { Transcription = (VideoTranscriptionMode)99 }),
        ("vision Optional", AllOff() with { Vision = VideoVisionMode.Optional }),
        ("vision Required", AllOff() with { Vision = VideoVisionMode.Required }),
        ("vision undefined member", AllOff() with { Vision = (VideoVisionMode)99 }),
        ("LocateSubjects", AllOff() with { LocateSubjects = true }),
        ("TrackObjects seeded by vision", AllOff() with { TrackObjects = [new ObjectTrackTarget("licence plate")] }),
        ("TrackObjects mixed drawn and vision-seeded", AllOff() with
        {
            TrackObjects = [new ObjectTrackTarget("sign", 1.0, DrawnRegion), new ObjectTrackTarget("licence plate")]
        }),
        ("DerushPrePass Prior", AllOff() with { DerushPrePass = VideoDerushPrePassMode.Prior }),
        ("DerushPrePass Fast", AllOff() with { DerushPrePass = VideoDerushPrePassMode.Fast }),
        ("DerushPrePass undefined member", AllOff() with { DerushPrePass = (VideoDerushPrePassMode)99 }),
        ("ScreenInstructionLikeSpans", AllOff() with { ScreenInstructionLikeSpans = true }),
        ("DescribeEvents with AnalyzeEvents", AllOff() with { AnalyzeEvents = true, DescribeEvents = true }),
        ("DescribeEvents with AnalyzeEvents and a number of looks", AllOff() with { AnalyzeEvents = true, DescribeEvents = true, MaxDescribedEvents = 5 }),
    }.Select(c => new object[] { c.Name, JsonSerializer.Serialize(c.Config, ConfigJson) });

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEnumerable<object[]> AllStepTypes() =>
        Enum.GetValues<StepType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Every_step_type_has_a_deliberate_decision(StepType type)
    {
        Expected.Should().ContainKey(type, "a new StepType needs a paid/free decision in RunRateClassifier");
        RunRateClassifier.Classify([Step(type)]).Should().Be(Expected[type]);
    }

    private static WorkflowStep Cleanup(string? json) =>
        new() { StepType = StepType.AudioCleanup, AudioCleanupConfigJson = json };

    [Theory]
    [InlineData("VoiceBasic")]
    [InlineData("Hum")]
    [InlineData("Wind")]
    [InlineData("Hiss")]
    [InlineData("Echo")]
    public void AudioCleanup_with_a_local_target_is_free(string target) =>
        RunRateClassifier.Classify([Cleanup($$$"""{"target":"{{{target}}}","source":{"kind":"ProjectFile"}}""")]).Should().Be(RunRateBucket.Free);

    [Theory]
    [InlineData("IsolateVoice")]
    [InlineData("RemoveMusic")]
    [InlineData("Nonsense")]
    public void AudioCleanup_with_a_hosted_or_unknown_target_is_paid(string target) =>
        RunRateClassifier.Classify([Cleanup($$$"""{"target":"{{{target}}}","source":{"kind":"ProjectFile"}}""")]).Should().Be(RunRateBucket.Paid);

    [Fact]
    public void AudioCleanup_with_a_missing_or_unreadable_config_is_paid()
    {
        RunRateClassifier.Classify([Cleanup(null)]).Should().Be(RunRateBucket.Paid);
        RunRateClassifier.Classify([Cleanup("{not json")]).Should().Be(RunRateBucket.Paid);
    }

    [Fact]
    public void Empty_workflow_is_paid() =>
        RunRateClassifier.Classify([]).Should().Be(RunRateBucket.Paid);

    [Theory]
    [MemberData(nameof(ProviderCallingFlagsAlone))]
    public void VideoAnalyze_with_one_provider_calling_field_set_alone_is_paid(string name, string json)
    {
        RunRateClassifier.Classify([Step(StepType.VideoAnalyze, json)]).Should().Be(RunRateBucket.Paid, name);
    }

    [Theory]
    [MemberData(nameof(ProviderCallingFlagsAlone))]
    public void CallsAnyProvider_is_true_for_one_provider_calling_field_set_alone(string name, string json)
    {
        JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(json, ConfigJson)!.CallsAnyProvider.Should().BeTrue(name);
    }

    [Fact]
    public void CallsAnyProvider_is_false_with_every_provider_calling_field_off() =>
        AllOff().CallsAnyProvider.Should().BeFalse();

    private static string EnhanceJson(string op, string engine, string extra = "") =>
        $$"""{"op":"{{op}}","engine":"{{engine}}","source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"}{{extra}}}""";

    [Theory]
    [InlineData("Denoise", "Local", ",\"denoiseStrength\":\"Light\"", RunRateBucket.Free)]
    [InlineData("MotionBlur", "Local", "", RunRateBucket.Free)]
    [InlineData("Upscale", "Local", ",\"upscaleFactor\":2", RunRateBucket.Free)]
    [InlineData("Interpolate", "Local", ",\"frameMultiplier\":2", RunRateBucket.Free)]
    [InlineData("Upscale", "Hosted", ",\"upscaleFactor\":2", RunRateBucket.Paid)]
    [InlineData("Interpolate", "Hosted", ",\"frameMultiplier\":2", RunRateBucket.Paid)]
    public void VideoEnhance_is_free_only_when_its_config_names_a_local_operation(string op, string engine, string extra, RunRateBucket expected) =>
        RunRateClassifier.Classify([new WorkflowStep { StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = EnhanceJson(op, engine, extra) }])
            .Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void VideoEnhance_with_no_or_unreadable_config_is_paid(string? json) =>
        RunRateClassifier.Classify([new WorkflowStep { StepType = StepType.VideoEnhance, VideoEnhanceConfigJson = json }])
            .Should().Be(RunRateBucket.Paid);

    [Fact]
    public void VideoAnalyze_with_every_provider_calling_field_off_is_free() =>
        RunRateClassifier.Classify([Step(StepType.VideoAnalyze, JsonSerializer.Serialize(AllOff(), ConfigJson))])
            .Should().Be(RunRateBucket.Free);

    [Fact]
    public void VideoAnalyze_that_only_names_DescribeEvents_without_the_events_analysis_or_with_zero_looks_calls_nothing_and_is_free()
    {
        // The executor describes only inside the events analysis, and 0 looks switches them off.
        foreach (VideoAnalyzeStepConfig config in new[]
        {
            AllOff() with { DescribeEvents = true },
            AllOff() with { AnalyzeEvents = true },
            AllOff() with { AnalyzeEvents = true, DescribeEvents = true, MaxDescribedEvents = 0 },
        })
        {
            config.CallsAnyProvider.Should().BeFalse();
            RunRateClassifier.Classify([Step(StepType.VideoAnalyze, JsonSerializer.Serialize(config, ConfigJson))]).Should().Be(RunRateBucket.Free);
        }
    }

    [Fact]
    public void VideoAnalyze_tracking_only_drawn_regions_is_free() =>
        RunRateClassifier.Classify([Step(StepType.VideoAnalyze, JsonSerializer.Serialize(
                AllOff() with { TrackObjects = [new ObjectTrackTarget("sign", 1.0, DrawnRegion)] }, ConfigJson))])
            .Should().Be(RunRateBucket.Free);

    [Fact]
    public void VideoAnalyze_of_an_object_analysis_run_is_paid() =>
        // The hidden ObjectAnalysis workflow: one VideoAnalyze step, a prompt-named (vision-seeded) segmentation target, all else off.
        RunRateClassifier.Classify([Step(StepType.VideoAnalyze, JsonSerializer.Serialize(
                AllOff() with { TrackObjects = [new ObjectTrackTarget("the red car")] }, ConfigJson))])
            .Should().Be(RunRateBucket.Paid);

    [Fact]
    public void Conditional_in_expression_mode_is_free() =>
        RunRateClassifier.Classify([Conditional(ExpressionConditional)]).Should().Be(RunRateBucket.Free);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"version":1,"mode":"Decision"}""")]
    [InlineData("""{"version":1,"mode":1}""")]
    [InlineData("""{"version":1,"mode":42}""")]
    [InlineData("""{"version":1,"mode":"NoSuchMode"}""")]
    public void Conditional_that_is_missing_unreadable_or_not_expression_mode_is_paid(string? json) =>
        RunRateClassifier.Classify([Conditional(json)]).Should().Be(RunRateBucket.Paid);

    [Fact]
    public void One_paid_step_makes_the_whole_workflow_paid() =>
        RunRateClassifier.Classify([Step(StepType.Extract), Step(StepType.Agent), Step(StepType.VideoCompile)])
            .Should().Be(RunRateBucket.Paid);

    [Fact]
    public void All_free_steps_are_free() =>
        RunRateClassifier.Classify(
                [Step(StepType.Extract), Conditional(ExpressionConditional), Step(StepType.VideoCompile), Step(StepType.TimelineRender)])
            .Should().Be(RunRateBucket.Free);

    [Fact]
    public void An_unknown_future_step_type_is_paid() =>
        RunRateClassifier.Classify([Step((StepType)9999)]).Should().Be(RunRateBucket.Paid);

    [Fact]
    public void VideoAnalyze_with_every_provider_call_off_is_free() =>
        RunRateClassifier.Classify([Step(StepType.VideoAnalyze, OfflineAnalyze)]).Should().Be(RunRateBucket.Free);

    [Theory]
    [InlineData("""{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Optional","vision":"Off","derushPrePass":"Off"}""")]
    [InlineData("""{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Optional","derushPrePass":"Off"}""")]
    [InlineData("""{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off","derushPrePass":"Prior"}""")]
    [InlineData("""{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off","derushPrePass":"Off","screenInstructionLikeSpans":true}""")]
    [InlineData("""{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off","derushPrePass":"Off","trackObjects":[{"label":"licence plate"}]}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void VideoAnalyze_that_may_call_a_provider_or_cannot_be_read_is_paid(string? json) =>
        RunRateClassifier.Classify([Step(StepType.VideoAnalyze, json)]).Should().Be(RunRateBucket.Paid);
}
