using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Automatic caption placement (docs/voiceover.md "Captions" — "Keeping clear of the picture's own
/// text"). The rule's numbers are the edge densities measured on the launch reel's real sources.
/// </summary>
public class CaptionPlacementProbeTests
{
    [Theory]
    [InlineData(12.9, 3.1, true)]   // screen recording: the app's title sits in the bottom band
    [InlineData(16.8, 6.1, true)]   // monitor over a busy keyboard
    [InlineData(0.2, 8.3, false)]   // clean desk bottom
    [InlineData(8.0, 6.6, false)]   // phone held in front of a face: both bands busy alike
    [InlineData(7.9, 0.0, false)]   // just under the busy floor
    public void Moves_up_only_when_the_bottom_is_busy_and_the_top_is_clearly_calmer(double bottom, double top, bool up) =>
        CaptionPlacementProbe.ShouldMoveUp(bottom, top).Should().Be(up);

    [Fact]
    public void Reads_the_band_mean_signalstats_prints()
    {
        CaptionPlacementProbe.ParseDensity("[Parsed_metadata_6 @ 0x1] frame:0 pts:0\nlavfi.signalstats.YAVG=12.8998\n").Should().Be(12.8998);
        CaptionPlacementProbe.ParseDensity("nothing").Should().BeNull();
    }

    [Fact]
    public void The_bands_sit_where_the_caption_would_and_mirror_it_under_the_top_margin()
    {
        CaptionLayout layout = CaptionLayout.Resolve(1080, 1920, CaptionStyle.Karaoke, 0, CaptionPosition.Auto);

        var (bottom, upper) = CaptionPlacementProbe.Bands(layout);

        // Portrait: the bottom margin is 20% of the height, so the caption band ends at 0.8.
        (bottom.Top + bottom.Height).Should().BeApproximately(0.8, 1e-3);
        upper.Top.Should().BeApproximately(0.12, 1e-3);
        upper.Height.Should().Be(bottom.Height);
    }
}
