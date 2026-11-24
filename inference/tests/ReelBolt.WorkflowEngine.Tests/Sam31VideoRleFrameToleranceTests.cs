using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The frame-count tolerance approved as a follow-up (2026-10-19): the vendor's answer
/// is not frame-exact. The live batch of 2026-10-18 answered a 144-frame 6 s clip with 141 entries, and
/// the first live run (2026-10-19, execution 3e90b07c) answered an 827-frame clip with 825. A short answer
/// is padded at the tail with the last real mask; a long one is truncated to the probe's count; beyond 5%
/// of the probe (rounded up) the document is still refused. Nothing is resampled or interpolated.
/// </summary>
public class Sam31VideoRleFrameToleranceTests
{
    private const string Model = "fal-ai/sam-3-1/video-rle";
    private const int Width = 4;
    private const int Height = 4;

    private static VideoSegmentationResult Read(string json, int? expectedFrames, int maxObjects = 4)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        return Sam31VideoRleResponseReader.Read(
            parsed.RootElement, Model, Width, Height, expectedFrames, maxObjects);
    }

    /// <summary>
    /// One frame per entry, each carrying a one-pixel run that starts at a different index, so a padded
    /// frame can be told apart from the frame it copied. <paramref name="topLevelRleFrames"/> adds the
    /// optional parallel union array with the given length.
    /// </summary>
    private static string Document(int frames, int? topLevelRleFrames = null)
    {
        string Frame(int i)
        {
            int start = i % (Width * Height);
            return "[{\"object_id\":0,\"rle\":\"" + start + " 1\"}]";
        }

        string objects = "[" + string.Join(",", Enumerable.Range(0, frames).Select(Frame)) + "]";
        if (topLevelRleFrames is not { } rleFrames)
        {
            return "{\"objects\":" + objects + "}";
        }

        string rle = "[" + string.Join(",", Enumerable.Range(0, rleFrames).Select(_ => "\"0 1\"")) + "]";
        return "{\"objects\":" + objects + ",\"rle\":" + rle + "}";
    }

    private static string[] Masks(VideoSegmentationFrame frame) => [.. frame.Objects.Select(o => o.Rle)];

    [Fact]
    public void A_response_one_frame_short_is_padded_with_the_last_mask()
    {
        VideoSegmentationResult result = Read(Document(19), expectedFrames: 20);

        result.Frames.Should().HaveCount(20);
        result.Diagnostics!.FramesPadded.Should().Be(1);
        result.Diagnostics.FramesTruncated.Should().Be(0);
        result.Frames[^1].FrameIndex.Should().Be(19);
        Masks(result.Frames[^1]).Should().Equal(Masks(result.Frames[^2]),
            "the missing trailing frame holds the last real mask — no resample, no interpolation");
    }

    [Fact]
    public void Ninety_five_percent_of_the_probe_is_accepted_and_one_frame_fewer_is_refused()
    {
        // 100-frame probe: ceil(5%) = 5, so 95 frames is exactly the boundary and 94 is outside it.
        VideoSegmentationResult atBoundary = Read(Document(95), expectedFrames: 100);
        atBoundary.Frames.Should().HaveCount(100);
        atBoundary.Diagnostics!.FramesPadded.Should().Be(5);

        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Read(Document(94), expectedFrames: 100));
        ex.Code.Should().Be("SEGMENTATION_BAD_RESPONSE");
        ex.Message.Should().Contain("94 frame entries").And.Contain("measured at 100 frames")
            .And.Contain("tolerance");
    }

    [Fact]
    public void A_response_two_frames_long_is_truncated_to_the_probe_count()
    {
        // 40-frame probe: ceil(5%) = 2, so a 42-entry answer is exactly at the boundary.
        VideoSegmentationResult result = Read(Document(42), expectedFrames: 40);

        result.Frames.Should().HaveCount(40);
        result.Diagnostics!.FramesTruncated.Should().Be(2);
        result.Diagnostics.FramesPadded.Should().Be(0);
        result.Frames[^1].FrameIndex.Should().Be(39);
    }

    [Fact]
    public void A_matching_count_reports_neither_padding_nor_truncation()
    {
        VideoSegmentationResult result = Read(Document(7), expectedFrames: 7);

        result.Frames.Should().HaveCount(7);
        result.Diagnostics!.FramesPadded.Should().Be(0);
        result.Diagnostics.FramesTruncated.Should().Be(0);
    }

    [Fact]
    public void The_tolerance_never_bypasses_a_structural_refusal()
    {
        // The count (11) is inside the band of an 11-frame probe, but the top-level rle and objects
        // arrays disagree — a structural refusal that must fire regardless of the tolerance.
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Read(Document(11, topLevelRleFrames: 10), expectedFrames: 11));
        ex.Message.Should().Contain("'rle' has 10 entries but 'objects' has 11");
    }

    [Fact]
    public void A_short_answer_outside_the_band_is_still_refused()
    {
        // 20-frame probe: ceil(5%) = 1, so 18 frames (two short) is outside it.
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Read(Document(18), expectedFrames: 20));
        ex.Message.Should().Contain("18 frame entries").And.Contain("measured at 20 frames")
            .And.Contain("1-frame tolerance");
    }

    [Fact]
    public void A_long_answer_outside_the_band_is_still_refused()
    {
        VideoSegmentationBadResponseException ex = Assert.Throws<VideoSegmentationBadResponseException>(
            () => Read(Document(24), expectedFrames: 20));
        ex.Message.Should().Contain("24 frame entries").And.Contain("measured at 20 frames");
    }
}
