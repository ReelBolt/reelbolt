using System;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// T-007: a Source compile honours the run's chosen resolution (canvas = source aspect with the
/// short side at the choice), and every render reports what it produced against what was asked.
/// </summary>
public sealed class OutputResolutionReportTests
{
    [Theory]
    [InlineData(1344, 768, 2160, 3780, 2160)]
    [InlineData(1344, 768, 720, 1260, 720)]
    [InlineData(1344, 768, 1080, 1890, 1080)]
    [InlineData(768, 1344, 2160, 2160, 3780)]
    [InlineData(1000, 1000, 2160, 2160, 2160)]
    public void A_source_canvas_has_the_source_aspect_and_the_chosen_short_side(
        int w, int h, int choice, int expectedW, int expectedH)
    {
        (int Width, int Height)? canvas = SourceResolutionCanvas.For(w, h, choice);

        canvas.Should().Be((expectedW, expectedH));
        (canvas!.Value.Width % 2).Should().Be(0);
        (canvas.Value.Height % 2).Should().Be(0);
        Math.Abs((double)canvas.Value.Width / canvas.Value.Height - (double)w / h).Should().BeLessThan(0.002);
    }

    [Theory]
    [InlineData(1344, 768, null)]
    [InlineData(0, 768, 2160)]
    [InlineData(1344, 0, 2160)]
    public void No_choice_or_no_known_source_size_leaves_the_canvas_alone(int w, int h, int? choice)
    {
        SourceResolutionCanvas.For(w, h, choice).Should().BeNull();
    }

    [Fact]
    public void Source_with_a_choice_sizes_the_canvas_from_the_canonical_clip_and_fixed_formats_are_untouched()
    {
        var canonical = new VideoAnalysisMedia(5, 24, 1, 1344, 768);

        SourceResolutionCanvas.CanvasFor(VideoOutputFormat.Source, 2160, canonical).Should().Be((3780, 2160));
        SourceResolutionCanvas.CanvasFor(VideoOutputFormat.Source, null, canonical).Should().BeNull();
        SourceResolutionCanvas.CanvasFor(VideoOutputFormat.Vertical, 2160, canonical).Should().Be((2160, 3840));
        SourceResolutionCanvas.CanvasFor(VideoOutputFormat.Vertical, null, canonical).Should().Be((1080, 1920));
    }

    [Fact]
    public void The_scaled_source_canvas_upscales_with_a_pad_fit_filter_to_the_exact_size()
    {
        var source = new VideoAnalysisMedia(5, 24, 1, 1344, 768);
        var canvas = source with { Width = 3780, Height = 2160 };

        VideoCompileStepExecutor.BuildCanvasFitFilter(source, canvas, cover: false).Should().Be(
            "scale=3780:2160:force_original_aspect_ratio=decrease,pad=3780:2160:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1");
    }

    [Fact]
    public void An_honoured_report_has_the_requested_label_the_size_and_no_reason()
    {
        OutputResolutionReport.Build("2160p", 3840, 2160, "ignored when honoured")!.ToJsonString().Should().Be(
            """{"requested":"2160p","width":3840,"height":2160,"honoured":true}""");
    }

    [Fact]
    public void A_report_below_the_request_is_not_honoured_and_carries_the_reason()
    {
        const string reason = "timeline renders never upscale above the timeline's own size";
        System.Text.Json.Nodes.JsonObject node = OutputResolutionReport.Build("2160p", 1344, 768, reason)!;

        node["requested"]!.GetValue<string>().Should().Be("2160p");
        node["width"]!.GetValue<int>().Should().Be(1344);
        node["height"]!.GetValue<int>().Should().Be(768);
        node["honoured"]!.GetValue<bool>().Should().BeFalse();
        node["reason"]!.GetValue<string>().Should().Be(reason);
    }

    [Fact]
    public void A_report_that_is_not_honoured_without_a_reason_omits_it()
    {
        OutputResolutionReport.Build("1080p", 1280, 720, null)!.ToJsonString().Should().Be(
            """{"requested":"1080p","width":1280,"height":720,"honoured":false}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public void No_requested_resolution_means_no_report(string? requested)
    {
        OutputResolutionReport.Build(requested, 1280, 720, null).Should().BeNull();
    }

    [Fact]
    public void A_timeline_render_below_the_request_says_it_never_upscales()
    {
        var node = OutputResolutionReport.ForTimelineRender(2160, 1920, 1080, planCapApplied: false)!;

        node["honoured"]!.GetValue<bool>().Should().BeFalse();
        node["reason"]!.GetValue<string>().Should().Be("timeline renders never upscale above the timeline's own size");
    }

    [Fact]
    public void A_timeline_render_held_down_by_the_plan_cap_says_so()
    {
        var node = OutputResolutionReport.ForTimelineRender(2160, 1920, 1080, planCapApplied: true)!;

        node["reason"]!.GetValue<string>().Should().Be("your plan's output cap lowered the render");
    }

    [Fact]
    public void A_timeline_render_at_the_requested_short_side_is_honoured_and_a_downscale_too()
    {
        OutputResolutionReport.ForTimelineRender(1080, 1920, 1080, false)!["honoured"]!.GetValue<bool>().Should().BeTrue();
        OutputResolutionReport.ForTimelineRender(720, 1920, 1080, false)!["honoured"]!.GetValue<bool>().Should().BeFalse();
        OutputResolutionReport.ForTimelineRender(null, 1920, 1080, false).Should().BeNull();
    }

    [Fact]
    public void A_remotion_render_is_reported_at_the_composition_scaled_to_the_choice()
    {
        var node = OutputResolutionReport.ForRemotion(2160, 1920, 1080, planMaxHeight: int.MaxValue)!;

        node.ToJsonString().Should().Be("""{"requested":"2160p","width":3840,"height":2160,"honoured":true}""");
    }

    [Fact]
    public void A_remotion_render_held_down_by_the_plan_cap_is_not_honoured()
    {
        var node = OutputResolutionReport.ForRemotion(2160, 1920, 1080, planMaxHeight: 1080)!;

        node["width"]!.GetValue<int>().Should().Be(1920);
        node["height"]!.GetValue<int>().Should().Be(1080);
        node["honoured"]!.GetValue<bool>().Should().BeFalse();
        node["reason"]!.GetValue<string>().Should().Contain("1080p");
    }

    [Fact]
    public void A_remotion_render_of_an_unknown_composition_size_reports_nothing_it_cannot_know()
    {
        OutputResolutionReport.ForRemotion(2160, 0, 0, int.MaxValue).Should().BeNull();
        OutputResolutionReport.ForRemotion(null, 1920, 1080, int.MaxValue).Should().BeNull();
    }

    [Fact]
    public void A_stream_copy_reports_the_real_source_size_as_not_honoured_with_a_reason()
    {
        var node = OutputResolutionReport.ForStreamCopy(2160, 1344, 768)!;

        node["requested"]!.GetValue<string>().Should().Be("2160p");
        node["width"]!.GetValue<int>().Should().Be(1344);
        node["height"]!.GetValue<int>().Should().Be(768);
        node["honoured"]!.GetValue<bool>().Should().BeFalse();
        node["reason"]!.GetValue<string>().Should().Contain("stream copy");
        OutputResolutionReport.ForStreamCopy(null, 1344, 768).Should().BeNull();
    }
}
