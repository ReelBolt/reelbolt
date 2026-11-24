#nullable enable
using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The mask's own encode path: the argv is first-party and pinned exactly (the media department's rule
/// for any filtergraph/argv builder), and the 1-bit frame packing is the polarity the compile step
/// assumes.
/// </summary>
public sealed class SegmentationMaskArgvBuilderTests
{
    [Fact]
    public void The_encode_argv_is_exact()
    {
        IReadOnlyList<string> argv = SegmentationMaskArgvBuilder.BuildEncodeArgs(
            "segmentation/mask-%06d.pbm", 23.976, "segmentation/mask.mkv");

        argv.Should().Equal(
            "-hide_banner",
            "-nostdin",
            "-y",
            "-f", "image2",
            "-start_number", "1",
            "-framerate", "23.976",
            "-i", "segmentation/mask-%06d.pbm",
            "-an",
            "-sn",
            "-dn",
            "-c:v", "ffv1",
            "-pix_fmt", "gray",
            "-r", "23.976",
            "segmentation/mask.mkv");
    }

    [Fact]
    public void The_frame_rate_is_formatted_invariantly()
    {
        SegmentationMaskArgvBuilder.BuildEncodeArgs(
            SegmentationMaskArgvBuilder.FramePattern, 30, "out.mkv")
            .Should().Contain("30").And.ContainInOrder("-framerate", "30", "-i");
    }

    [Fact]
    public void A_frame_is_packed_one_bit_per_pixel_row_major_msb_first()
    {
        // 4x2 frame: pixels 0,1 and 5 are the OBJECT. A set PBM bit is BLACK, so the packing is
        // inverted: row 1 (pixels 0..3) = 0011 -> 0x30; row 2 (pixels 4..7) = 1011 -> 0xB0.
        byte[] mask = Sam31VideoRleDecoder.Decode("0 2 5 1", 0, 4, 2).Pixels;

        byte[] pbm = SegmentationMaskArgvBuilder.EncodePbmFrame(mask, 4, 2);

        // Header is "P4\n4 2\n" (7 bytes), then one byte per row.
        pbm.Should().Equal((byte)'P', (byte)'4', (byte)'\n', (byte)'4', (byte)' ', (byte)'2', (byte)'\n', 0x30, 0xB0);
    }

    [Fact]
    public void A_row_that_is_not_a_whole_byte_is_padded_and_keeps_its_own_alignment()
    {
        // 3x2: setting pixel 2 (top row) and pixel 3 (bottom row's first) must not bleed across rows.
        byte[] mask = Sam31VideoRleDecoder.Decode("2 1 3 1", 0, 3, 2).Pixels;

        byte[] pbm = SegmentationMaskArgvBuilder.EncodePbmFrame(mask, 3, 2);

        pbm[^2].Should().Be(0xC0, "the top row's object pixel 2 leaves bits 0 and 1 set (inverted)");
        pbm[^1].Should().Be(0x60, "the bottom row's object pixel starts a new byte with bits 1 and 2 set (inverted)");
    }

    [Fact]
    public void A_short_mask_buffer_is_refused_rather_than_read_out_of_bounds()
    {
        Action encode = () => SegmentationMaskArgvBuilder.EncodePbmFrame(new byte[3], 4, 2);

        encode.Should().Throw<ArgumentException>();
    }
}
