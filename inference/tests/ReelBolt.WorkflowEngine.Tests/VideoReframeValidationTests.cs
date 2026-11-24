#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="VideoReframe.Validate"/>: the config-side rules of keyframed reframing
/// (docs/video-editing.md "Keyframed reframing").
/// </summary>
public class VideoReframeValidationTests
{
    private static ReframeKeyframe Key(double at, double zoom = 1, double cx = 0.5, double cy = 0.5, ReframeEasing easing = ReframeEasing.Linear) =>
        new(at, zoom, cx, cy, easing);

    [Fact]
    public void No_reframes_is_valid_and_empty()
    {
        VideoReframe.Validate(null).IsValid.Should().BeTrue();
        VideoReframe.Validate([]).Normalized.Should().BeEmpty();
    }

    [Fact]
    public void A_well_formed_reframe_passes_unchanged()
    {
        var reframe = new VideoReframe(0, 1.0, 4.0, [Key(0, 3, 0.4, 0.7, ReframeEasing.EaseOut), Key(3, 1)]);

        VideoReframeValidation v = VideoReframe.Validate([reframe]);

        v.IsValid.Should().BeTrue(v.Message);
        v.Clamps.Should().BeEmpty();
        v.Normalized.Single().Should().BeEquivalentTo(reframe);
    }

    [Theory]
    [InlineData(0.99)]
    [InlineData(8.01)]
    [InlineData(double.NaN)]
    public void Zoom_outside_one_to_eight_is_rejected(double zoom)
    {
        VideoReframe.Validate([new VideoReframe(0, null, null, [Key(0, zoom)])])
            .ErrorCode.Should().Be("REFRAME_ZOOM_OUT_OF_RANGE");
    }

    [Fact]
    public void Keyframes_must_be_strictly_increasing_in_time()
    {
        VideoReframe.Validate([new VideoReframe(0, null, null, [Key(1), Key(1)])])
            .ErrorCode.Should().Be("REFRAME_KEYFRAMES_UNSORTED");
        VideoReframe.Validate([new VideoReframe(0, null, null, [Key(2), Key(1)])])
            .ErrorCode.Should().Be("REFRAME_KEYFRAMES_UNSORTED");
    }

    [Fact]
    public void At_most_thirty_two_keyframes_and_at_least_one()
    {
        VideoReframe.Validate([new VideoReframe(0, null, null, Enumerable.Range(0, 33).Select(i => Key(i)).ToList())])
            .ErrorCode.Should().Be("REFRAME_TOO_MANY_KEYFRAMES");
        VideoReframe.Validate([new VideoReframe(0, null, null, Enumerable.Range(0, 32).Select(i => Key(i)).ToList())])
            .IsValid.Should().BeTrue();
        VideoReframe.Validate([new VideoReframe(0, null, null, [])])
            .ErrorCode.Should().Be("REFRAME_NO_KEYFRAMES");
    }

    [Theory]
    [InlineData(-1.0, 2.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.0, 1.0)]
    public void The_span_must_be_a_forward_range_in_source_time(double from, double to)
    {
        VideoReframe.Validate([new VideoReframe(0, from, to, [Key(0)])])
            .ErrorCode.Should().Be("REFRAME_RANGE_INVALID");
    }

    [Fact]
    public void A_keyframe_past_the_span_end_is_rejected()
    {
        // AtSec is relative to the span start: 1 + 3.5 lands past ToSec = 4.
        VideoReframe.Validate([new VideoReframe(0, 1, 4, [Key(0), Key(3.5)])])
            .ErrorCode.Should().Be("REFRAME_KEYFRAME_OUTSIDE_RANGE");
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.5, 1.01)]
    public void A_centre_outside_the_frame_is_rejected(double cx, double cy)
    {
        VideoReframe.Validate([new VideoReframe(0, null, null, [Key(0, 2, cx, cy)])])
            .ErrorCode.Should().Be("REFRAME_CENTER_OUT_OF_RANGE");
    }

    [Fact]
    public void Two_reframes_of_one_source_must_not_overlap()
    {
        VideoReframe.Validate(
        [
            new VideoReframe(0, 0, 3, [Key(0)]),
            new VideoReframe(0, 2, null, [Key(0)])
        ]).ErrorCode.Should().Be("REFRAME_OVERLAP");

        VideoReframe.Validate(
        [
            new VideoReframe(0, 0, 3, [Key(0)]),
            new VideoReframe(0, 3, 5, [Key(0)]),
            new VideoReframe(1, 0, null, [Key(0)])
        ]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_negative_source_index_is_rejected()
    {
        VideoReframe.Validate([new VideoReframe(-1, null, null, [Key(0)])])
            .ErrorCode.Should().Be("REFRAME_UNKNOWN_SOURCE");
    }

    [Fact]
    public void A_centre_whose_window_would_leave_the_frame_is_clamped_and_reported()
    {
        // Zoom 4 shows a quarter of the frame: its centre can sit no closer than 0.125 to an edge.
        VideoReframeValidation v = VideoReframe.Validate([new VideoReframe(0, null, null, [Key(0, 4, 0.05, 0.95)])]);

        v.IsValid.Should().BeTrue();
        ReframeKeyframe k = v.Normalized.Single().Keyframes.Single();
        k.CenterX.Should().Be(0.125);
        k.CenterY.Should().Be(0.875);
        v.Clamps.Should().BeEquivalentTo(new[]
        {
            new ReframeClamp(0, 0, "centerX", 0.05, 0.125),
            new ReframeClamp(0, 0, "centerY", 0.95, 0.875)
        });
    }

    [Fact]
    public void Config_round_trips_through_json_with_string_enums()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        const string json = """
            {"reframes":[{"sourceIndex":0,"fromSec":0,"toSec":3,"keyframes":[
              {"atSec":0,"zoom":2.5,"centerX":0.4,"centerY":0.7,"easing":"EaseOut"},{"atSec":3,"zoom":1}]}],
             "sourceFits":{"1":"Pad"},"subjectSafeArea":0.08}
            """;

        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, options)!;

        config.Reframes!.Single().Keyframes[0].Easing.Should().Be(ReframeEasing.EaseOut);
        config.Reframes!.Single().Keyframes[1].CenterX.Should().Be(0.5, "an omitted centre is the frame centre");
        config.SourceFits![1].Should().Be(VideoOutputFit.Pad);
        config.SubjectSafeArea.Should().Be(0.08);
        VideoCompileStepConfig plain = JsonSerializer.Deserialize<VideoCompileStepConfig>("{}", options)!;
        plain.Reframes.Should().BeNull();
        plain.SourceFits.Should().BeNull();
        plain.SubjectSafeArea.Should().BeNull();
    }

    private static VideoCompileStepConfig Config(VideoCompileMode mode = VideoCompileMode.Reencode, IReadOnlyList<VideoReframe>? reframes = null, double? safeArea = null) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Mode: mode, Reframes: reframes, SubjectSafeArea: safeArea);

    [Fact]
    public void Reframing_a_stream_copy_is_a_config_error()
    {
        VideoReframe.CheckConfig(Config(VideoCompileMode.StreamCopy, [new VideoReframe(0, null, null, [Key(0, 2)])]))
            .ErrorCode.Should().Be("REFRAME_REQUIRES_REENCODE");
        VideoReframe.CheckConfig(Config(VideoCompileMode.StreamCopy)).IsValid.Should().BeTrue("no reframes, nothing to re-encode for");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.26)]
    [InlineData(double.NaN)]
    public void A_safe_area_outside_zero_to_a_quarter_is_a_config_error(double margin)
    {
        VideoReframe.CheckConfig(Config(safeArea: margin)).ErrorCode.Should().Be("SUBJECT_SAFE_AREA_OUT_OF_RANGE");
    }

    [Fact]
    public void Check_config_returns_the_normalized_reframes()
    {
        VideoReframeValidation v = VideoReframe.CheckConfig(Config(reframes: [new VideoReframe(0, null, null, [Key(0, 2, 0.1)])], safeArea: 0.1));

        v.IsValid.Should().BeTrue();
        v.Normalized.Single().Keyframes.Single().CenterX.Should().Be(0.25);
    }
    // ---- caps on the number of reframes (DoS: each is a zoompan expression per span) ----

    private static List<VideoReframe> Reframes(int sources, int perSource, int keyframesEach = 1) =>
        Enumerable.Range(0, sources)
            .SelectMany(src => Enumerable.Range(0, perSource).Select(i =>
                new VideoReframe(src, i, i + 1, Enumerable.Range(0, keyframesEach).Select(k => Key(k / (double)keyframesEach)).ToList())))
            .ToList();

    [Fact]
    public void The_caps_are_sixteen_per_source_sixty_four_in_all_and_five_hundred_twelve_keyframes()
    {
        VideoReframe.MaxReframesPerSource.Should().Be(16);
        VideoReframe.MaxReframesTotal.Should().Be(64);
        VideoReframe.MaxKeyframesTotal.Should().Be(512);
    }

    [Fact]
    public void Sixteen_reframes_of_one_source_pass_and_seventeen_are_refused()
    {
        VideoReframe.Validate(Reframes(1, 16)).IsValid.Should().BeTrue();
        VideoReframe.Validate(Reframes(1, 17)).ErrorCode.Should().Be("REFRAME_TOO_MANY_REFRAMES");
    }

    [Fact]
    public void Sixty_four_reframes_in_all_pass_and_sixty_five_are_refused()
    {
        VideoReframe.Validate(Reframes(4, 16)).IsValid.Should().BeTrue();
        List<VideoReframe> sixtyFive = [.. Reframes(4, 16), new VideoReframe(4, 0, 1, [Key(0)])];
        VideoReframe.Validate(sixtyFive).ErrorCode.Should().Be("REFRAME_TOO_MANY_REFRAMES");
    }

    [Fact]
    public void Five_hundred_twelve_keyframes_in_all_pass_and_five_hundred_thirteen_are_refused()
    {
        VideoReframe.Validate(Reframes(1, 16, 32)).IsValid.Should().BeTrue();
        List<VideoReframe> over = [.. Reframes(1, 16, 32), new VideoReframe(1, 0, 1, [Key(0)])];
        VideoReframe.Validate(over).ErrorCode.Should().Be("REFRAME_TOO_MANY_KEYFRAMES");
    }

    [Fact]
    public void Check_config_refuses_the_same_lists()
    {
        VideoReframe.CheckConfig(Config(reframes: Reframes(1, 17))).ErrorCode.Should().Be("REFRAME_TOO_MANY_REFRAMES");
    }

    [Fact]
    public void Every_reframe_code_is_a_user_input_failure()
    {
        foreach (string code in VideoReframeCodes.All)
        {
            ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.TryGetCode(code, out var failureClass).Should().BeTrue(code);
            failureClass.Should().Be(ReelBolt.Shared.Data.Models.FailureClass.UserInput, code);
        }

        VideoReframeCodes.All.Should().Contain("REFRAME_TOO_MANY_REFRAMES");
    }
}
