using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression test: found live producing the alpha launch film, by pulling the finished file and
/// hashing decoded frames across its duration.
///
/// The spill-suppression band (<see cref="ScreenInsertFilterBuilder.AppendInsertFootprint"/>) is
/// the third input of <c>maskedmerge</c>, and <c>maskedmerge</c> is a FRAMESYNC filter: handed one
/// input whose first frame sits at the insert's start time, it discards every frame of the other
/// two — the base picture among them — until all three inputs have delivered. Delaying the band
/// with <c>setpts</c> alone therefore truncated the finished film to the part at or after the
/// FIRST matted insert.
///
/// Measured on the real output before the fix: the video stream began at 20.433s (the monitor
/// insert's own start) carrying 166 frames instead of 749, while the audio — which never passes
/// through this filter — was intact from 0. A player holds one frame for the first twenty
/// seconds, which is what "the video is frozen" looked like from the outside.
///
/// The ffmpeg behaviour was confirmed directly at the time:
///   maskedmerge(base@0, despilled@0, band@6) over a 10s base -> start_time=6.0, 120 frames;
///   the same graph with the band padded from 0 -> start_time=0.0, 300 frames.
/// </summary>
public class ScreenInsertBandTimelineTests
{
    private static ResolvedScreenInsert Insert(double outStart, double outEnd = 24.9) =>
        new(
            "r3", "/scratch/insert.mp4", outStart, outEnd,
            [
                new InsertQuadFrame(0, 576, 216, 1152, 238, 595, 756, 1171, 778),
                new InsertQuadFrame(60, 672, 216, 1248, 238, 691, 756, 1267, 778)
            ],
            30, 1,
            Matte: new VideoInsertChromaKey(0, 232, 0, 0.30, 0.10, 0.9));

    [Fact]
    public void The_band_is_padded_from_zero_so_maskedmerge_cannot_eat_the_base()
    {
        var segments = new List<string>();

        ScreenInsertFilterBuilder.AppendInsertFootprint(segments, Insert(20.433008), 1920, 1080, "fp0");

        string band = segments.Single();
        band.Should().Contain("tpad=start_duration=20.433008:start_mode=add:color=black");

        // The delay must NOT also be expressed as a setpts offset — that would double the shift.
        band.Should().Contain("setpts=PTS-STARTPTS,tpad=");
        band.Should().NotContain("PTS-STARTPTS+");
    }

    [Fact]
    public void Black_is_the_pad_colour_because_maskedmerge_takes_the_base_where_the_mask_is_black()
    {
        var segments = new List<string>();

        ScreenInsertFilterBuilder.AppendInsertFootprint(segments, Insert(5.0), 1920, 1080, "fp0");

        // White would apply the despilled picture to the WHOLE frame before the insert appears.
        segments.Single().Should().Contain("start_mode=add:color=black");
    }

    [Fact]
    public void An_insert_starting_at_zero_needs_no_padding_and_keeps_the_original_string()
    {
        var segments = new List<string>();

        ScreenInsertFilterBuilder.AppendInsertFootprint(segments, Insert(0.0), 1920, 1080, "fp0");

        string band = segments.Single();
        band.Should().EndWith("setpts=PTS-STARTPTS[fp0]");
        band.Should().NotContain("tpad");
    }

    [Fact]
    public void Padding_is_applied_after_the_warp_so_the_perspective_frame_indices_are_untouched()
    {
        var segments = new List<string>();

        ScreenInsertFilterBuilder.AppendInsertFootprint(segments, Insert(12.2), 1920, 1080, "fp0");

        string band = segments.Single();
        // BuildPerspectiveFilter's expressions count `in` from the insert's OWN first frame, so a
        // pad inserted before the warp would renumber every corner keyframe.
        band.IndexOf("perspective", System.StringComparison.Ordinal)
            .Should().BeLessThan(band.IndexOf("tpad", System.StringComparison.Ordinal));
    }
}
