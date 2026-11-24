using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Prod run 85444334 (2026-10-19): a 1080x1920 frame's last run ended at 2,073,601 of 2,073,600 pixels. A
/// tiny vendor overrun is clamped and reported; a grossly wrong geometry still fails closed.
/// </summary>
public class Sam31VideoRleOverrunToleranceTests
{
    private const int W = 1080;
    private const int H = 1920;
    private const int Bound = Sam31VideoRleDecoder.MaxTolerableOverrunPixels;

    [Fact]
    public void The_exact_prod_run_is_clamped_and_reported()
    {
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode("2073584 17", 0, W, H);

        mask.OverrunPixelsClamped.Should().Be(1);
        mask.Area.Should().Be(16);
        mask.Pixels[W * H - 1].Should().Be(Sam31VideoRleDecoder.ObjectValue);
    }

    [Fact]
    public void An_overrun_exactly_at_the_bound_is_clamped()
    {
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode($"{W * H - 10} {10 + Bound}", 0, W, H);

        mask.OverrunPixelsClamped.Should().Be(Bound);
        mask.Area.Should().Be(10);
    }

    [Fact]
    public void An_overrun_one_past_the_bound_is_refused()
    {
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Sam31VideoRleDecoder.Decode($"{W * H - 10} {10 + Bound + 1}", 0, W, H));

        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain("outside");
    }

    [Theory]
    [InlineData("2073600 1")]   // starts outside the frame
    [InlineData("100 2073600")] // grossly wrong
    public void Gross_geometry_errors_still_fail_closed(string rle)
    {
        Assert.Throws<VideoSegmentationBadResponseException>(() => Sam31VideoRleDecoder.Decode(rle, 0, W, H));
    }

    [Fact]
    public void An_in_range_run_reports_no_overrun()
    {
        Sam31VideoRleDecoder.Decode("0 5", 0, W, H).OverrunPixelsClamped.Should().Be(0);
    }
}
