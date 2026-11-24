#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.SegmentationTrackingTests;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// QA (click-to-select): the box fallback of the tapped-frame match. A tap that misses every mask but carries a USER-DRAWN box
/// takes the object the box overlaps best. A point-only tap never falls back: no mask holding the point is "could not find" (free).
/// A box must only travel when a person drew it; a box laid around a tap by the client would turn a near miss into a billed find.
/// </summary>
public sealed class SegmentationTapSeedQaTests
{
    // 12x6 frame, row-major. Object A: 2x2 block at x1..2, y1..2. Object B: 2x2 block at x8..9, y3..4.
    private const string BlockA = "13 2 25 2";
    private const string BlockB = "44 2 56 2";

    private static VideoAnalyzeStepConfig TapConfig(double x, double y, ObjectSeedBox? box) =>
        new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput),
            TrackObjects:
            [
                new ObjectTrackTarget(
                    "Selected object",
                    AtSec: 0,
                    Region: box is null ? null : new ObjectSeed(0, new ObjectSeedPoint(x, y), box).BoxAsQuad(),
                    Method: ObjectTrackMethod.Segmentation,
                    MatchScope: ObjectMatchScope.SingleTrack,
                    SeedPoint: new ObjectSeedPoint(x, y),
                    NoWords: true)
            ])
        with
        {
            AllowSourceMediaEgress = true,
            MaxSegmentationSeconds = 60
        };

    private static VideoSegmentationResult TwoObjects() =>
        new("fal-ai/sam-3-1/video-rle", 12, 6,
            [.. Enumerable.Range(0, 6).Select(f => new VideoSegmentationFrame(f,
                [new VideoSegmentationObjectRle(0, BlockA), new VideoSegmentationObjectRle(1, BlockB)]))],
            "falai:model", 0.01m, 6);

    // A drawn box around the point: a 0.2 square centred on it, slid inside the frame.
    private static ObjectSeedBox DefaultBoxAround(double x, double y) =>
        new(System.Math.Clamp(x - 0.1, 0, 0.8), System.Math.Clamp(y - 0.1, 0, 0.8), 0.2, 0.2);

    [Fact]
    public async Task A_tap_beside_an_object_with_a_drawn_box_finds_that_object()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(TwoObjects()));

        // Pixel (7,3) is one column left of object B: no mask holds it, but the drawn box overlaps B.
        double x = 7d / 11, y = 3d / 5;
        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(x, y, DefaultBoxAround(x, y)));

        outcome.FailureCode.Should().BeNull("the box fallback answers when no mask holds the point");
        outcome.Tracks.Should().ContainSingle().Subject.BoxKeyframes![0].X.Should().BeApproximately(8d / 12, 1e-9, "object B");
    }

    [Fact]
    public async Task The_same_tap_without_a_drawn_box_is_not_found_and_never_falls_back()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(TwoObjects()));

        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(7d / 11, 3d / 5, null));

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.ObjectNotFoundAtPoint);
        outcome.Tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tap_whose_box_overlaps_no_object_is_not_found()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(TwoObjects()));

        // Pixel (5,5): bottom middle, the drawn box (x 0.35..0.55, y 0.8..1.0) touches neither block.
        double x = 5d / 11, y = 1;
        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(x, y, DefaultBoxAround(x, y)));

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.ObjectNotFoundAtPoint);
        outcome.Tracks.Should().BeEmpty();
    }
}
