#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The vendor's run-length encoding, decoded offline (initiative "prompted region effects", plan §4/§6).
/// <para>
/// ONE parser: <see cref="Sam31VideoRleDecoder"/>, in <c>ReelBolt.Shared</c>, which both the client's
/// response reader and the analyze path call. These cases pin the LAYOUT, the measurement, the
/// object-slot selection and the failure modes; the committed REAL responses pin the bytes
/// (<c>FalAiVideoSegmentationTests</c>, T-002's replay pack, including the decoded-mask digest).
/// </para>
/// </summary>
public sealed class Sam31VideoRleDecoderMaskTests
{
    [Fact]
    public void A_run_lands_on_the_row_major_pixels_it_names()
    {
        // 4x2 frame: pixels 0..7. One run over 0..1 = the left half of the TOP row only — a column-major
        // decode would light pixels 0 and 4 instead, which is the exact failure a wrong grid width causes.
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode("0 2", objectId: 0, width: 4, height: 2);

        mask.Pixels.Should().Equal(255, 255, 0, 0, 0, 0, 0, 0);
        mask.Area.Should().Be(2);
    }

    [Fact]
    public void A_run_may_span_rows_and_several_runs_are_kept_apart()
    {
        VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode("2 2 4 2", objectId: 0, width: 4, height: 2);

        mask.Pixels.Should().Equal(0, 0, 255, 255, 255, 255, 0, 0);

        // The second run reaches the frame's right edge and continues on the next row at column 0, so
        // the bounding box spans the whole width — the reason it is measured from the pixels and never
        // derived from the run endpoints.
        mask.X0.Should().Be(0);
        mask.X1.Should().Be(3);
        mask.Y0.Should().Be(0);
        mask.Y1.Should().Be(1);
    }

    [Fact]
    public void An_empty_run_string_is_an_empty_mask_not_a_failure()
    {
        Sam31VideoRleDecoder.Decode("", 0, 4, 2).Pixels.Should().Equal(new byte[8]);
        Sam31VideoRleDecoder.Decode(null, 0, 4, 2).Pixels.Should().Equal(new byte[8]);
        Sam31VideoRleDecoder.Decode("   ", 0, 4, 2).Pixels.Should().Equal(new byte[8]);
    }

    [Theory]
    [InlineData("0 100")]        // far longer than the frame (a 1-32 pixel overshoot is clamped, see Sam31VideoRleOverrunToleranceTests)
    [InlineData("8 1")]          // starts past the end
    [InlineData("0 1 2")]        // odd token count: not start/length pairs
    [InlineData("0 -1")]         // a sign is not in the vocabulary
    [InlineData("a b")]
    public void A_run_that_does_not_fit_the_frame_throws_rather_than_clipping(string rle)
    {
        Action decode = () => Sam31VideoRleDecoder.Decode(rle, 0, 4, 2);

        decode.Should().Throw<VideoSegmentationBadResponseException>();
    }

    [Fact]
    public void The_measurement_is_the_coverage_and_the_normalised_bounding_box()
    {
        // 4x4 frame, one 2x2 block at (1,1): a quarter of the frame, centred.
        byte[] mask = Sam31VideoRleDecoder.Decode("5 2 9 2", 0, 4, 4).Pixels;

        Sam31VideoRleDecoder.SegmentationMaskMeasurement measured = Sam31VideoRleDecoder.Measure(mask, 4, 4);

        measured.CoveredPixels.Should().Be(4);
        measured.Coverage.Should().Be(0.25);
        measured.X.Should().Be(0.25);
        measured.Y.Should().Be(0.25);
        measured.W.Should().Be(0.5);
        measured.H.Should().Be(0.5);
    }

    [Fact]
    public void An_empty_mask_measures_nothing_rather_than_dividing_by_zero()
    {
        Sam31VideoRleDecoder.SegmentationMaskMeasurement measured =
            Sam31VideoRleDecoder.Measure(Sam31VideoRleDecoder.Decode("", 0, 4, 4).Pixels, 4, 4);

        measured.Should().Be(default(Sam31VideoRleDecoder.SegmentationMaskMeasurement));
    }

    [Fact]
    public void A_seeded_target_selects_the_object_slot_its_box_matches()
    {
        // Two slots: 1 is the whole frame, 2 is the small box at the centre. A seed on the centre must
        // pick 2 even though slot 1 covers more pixels — the author pointed at "this one".
        Sam31VideoRleDecoder.SegmentationObjectCandidate[] candidates =
        [
            new(1, CoveredPixels: 16, FirstBox: new Sam31VideoRleDecoder.SegmentationBox(0, 0, 1, 1)),
            new(2, CoveredPixels: 4, FirstBox: new Sam31VideoRleDecoder.SegmentationBox(0.25, 0.25, 0.5, 0.5))
        ];

        int? chosen = Sam31VideoRleDecoder.ChooseObjectId(
            candidates, new Sam31VideoRleDecoder.SegmentationBox(0.3, 0.3, 0.4, 0.4));

        chosen.Should().Be(2);
    }

    [Fact]
    public void An_unseeded_target_selects_the_largest_object_deterministically()
    {
        Sam31VideoRleDecoder.SegmentationObjectCandidate[] candidates =
        [
            new(3, CoveredPixels: 4, FirstBox: new Sam31VideoRleDecoder.SegmentationBox(0, 0, 0.1, 0.1)),
            new(1, CoveredPixels: 16, FirstBox: new Sam31VideoRleDecoder.SegmentationBox(0, 0, 1, 1)),
            new(2, CoveredPixels: 16, FirstBox: new Sam31VideoRleDecoder.SegmentationBox(0.1, 0.1, 0.9, 0.9))
        ];

        // Equal coverage on 1 and 2: the LOWEST id wins, so a repeated run names the same slot.
        Sam31VideoRleDecoder.ChooseObjectId(candidates).Should().Be(1);

        Sam31VideoRleDecoder.SegmentationObjectCandidate[] onlyEmpty =
        [
            new(1, 0, null),
            new(2, 0, null)
        ];

        Sam31VideoRleDecoder.ChooseObjectId(onlyEmpty).Should().BeNull("nothing was ever found on any frame");
    }
}
