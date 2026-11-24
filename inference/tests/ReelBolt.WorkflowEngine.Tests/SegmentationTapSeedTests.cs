#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.SegmentationTrackingTests;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A tap names an object instead of words (initiative click-to-select, T-001): the request follows the recorded point-only
/// answer, the object is the one whose mask holds the point ON THE TAPPED FRAME, none there is "not found" (never another
/// object), and a find that found nothing is a refunded failure. Offline: scripted answers and the committed replay pack.
/// </summary>
public sealed class SegmentationTapSeedTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // 12x6 frame, row-major. Object A: 2x2 block at x1..2, y1..2. Object B: 2x2 block at x8..9, y3..4.
    private const string BlockA = "13 2 25 2";
    private const string BlockB = "44 2 56 2";

    private static VideoAnalyzeStepConfig TapConfig(double atSec, double x, double y, ObjectSeedBox? box = null, bool noWords = true) =>
        new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput),
            TrackObjects:
            [
                new ObjectTrackTarget(
                    "Selected object",
                    AtSec: atSec,
                    Region: box is null ? null : new ObjectSeed(atSec, new ObjectSeedPoint(x, y), box).BoxAsQuad(),
                    Method: ObjectTrackMethod.Segmentation,
                    MatchScope: ObjectMatchScope.AllInstances,
                    SeedPoint: new ObjectSeedPoint(x, y),
                    NoWords: noWords)
            ])
        with
        {
            AllowSourceMediaEgress = true,
            MaxSegmentationSeconds = 60
        };

    private static VideoSegmentationResult Scripted(int width, int height, IReadOnlyList<VideoSegmentationFrame> frames) =>
        new("fal-ai/sam-3-1/video-rle", width, height, frames, "falai:model", 0.01m, frames.Count);

    private static VideoSegmentationFrame Frame(int index, params (int Id, string Rle)[] objects) =>
        new(index, [.. objects.Select(o => new VideoSegmentationObjectRle(o.Id, o.Rle))]);

    private static List<VideoSegmentationFrame> TwoObjects() =>
        [.. Enumerable.Range(0, 6).Select(f => Frame(f, (0, BlockA), (1, BlockB)))];

    [Fact]
    public async Task A_tap_on_the_second_object_picks_it_not_the_first()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, TwoObjects())));

        // Pixel (8,3) of a 12x6 frame: x = 8/11, y = 3/5.
        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(0, 8d / 11, 3d / 5));

        outcome.FailureCode.Should().BeNull();
        VideoSegmentationTrack track = outcome.Tracks.Should().ContainSingle().Subject;
        track.BoxKeyframes![0].X.Should().BeApproximately(8d / 12, 1e-9, "object B starts at column 8");
        track.Prompt.Should().Be("Selected object", "the label stays the display name");
    }

    [Fact]
    public async Task A_tap_on_the_first_object_picks_it()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, TwoObjects())));

        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(0, 1d / 11, 1d / 5));

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().ContainSingle().Subject.BoxKeyframes![0].X.Should().BeApproximately(1d / 12, 1e-9);
    }

    [Fact]
    public async Task A_tap_on_no_object_is_not_found_and_never_the_biggest_object()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, TwoObjects())));

        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(0, 0, 0));

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.ObjectNotFoundAtPoint).And.Be("OBJECT_NOT_FOUND_AT_POINT");
        outcome.Tracks.Should().BeEmpty("another object is never an answer");
        outcome.FailureMessage.Should().Be("We could not find an object where you tapped. Tap its centre, or draw a box.");
        // Platform-attributed, so the credit hold is refunded: the customer pays nothing for a find that found nothing.
        outcome.FailureClass.Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public async Task The_object_is_matched_on_the_tapped_frame_not_on_the_first_frames()
    {
        // Object 0 is big and on every frame (the old "most covered pixels" pick). Object 1 is a small block that exists only
        // from frame 3 on. The tap lands on object 1 at frame 4 (0.1667 s at 24 fps) at a pixel object 0 does not cover.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++)
        {
            frames.Add(frame < 3 ? Frame(frame, (0, "13 4 25 4")) : Frame(frame, (0, "13 4 25 4"), (1, BlockB)));
        }

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(4d / 24, 8d / 11, 3d / 5));

        outcome.FailureCode.Should().BeNull();
        VideoSegmentationTrack track = outcome.Tracks.Should().ContainSingle().Subject;
        track.Facts!.FirstNonEmptyFrame.Should().Be(3, "the tapped object is object 1, which appears on frame 3");
    }

    [Fact]
    public async Task A_tap_only_target_is_not_skipped_is_held_for_and_sends_no_words()
    {
        RecordingClient client = new(Scripted(12, 6, TwoObjects()));
        Harness harness = new(new CountingClient(), replay: client);
        VideoAnalyzeStepConfig config = TapConfig(0, 8d / 11, 3d / 5);

        SegmentationSourceOutcome outcome = await harness.RunAsync(config);

        outcome.FailureCode.Should().BeNull();
        client.Requests.Should().ContainSingle();
        VideoSegmentationRequest sent = client.Requests[0];
        sent.Prompt.Should().BeEmpty("the name is not sent as words");
        sent.PointPrompts.Should().ContainSingle().Which.Should().Match<VideoSegmentationPointPrompt>(p => p.FrameIndex == 0);
        sent.BoxPrompts.Should().BeNull("no box was drawn");
        outcome.Diagnostics.Should().BeNull();

        // The credit hold counts the target even though it has no words.
        (int targets, _) = SegmentationCreditPricing.Shape(JsonSerializer.Serialize(config, Web));
        targets.Should().Be(1);
        SegmentationCreditPricing.AddOnMillis(JsonSerializer.Serialize(config, Web)).Should().BePositive();
    }

    [Fact]
    public async Task A_drawn_box_is_sent_with_the_point_at_the_tapped_frame()
    {
        RecordingClient client = new(Scripted(12, 6, TwoObjects()));
        Harness harness = new(new CountingClient(), replay: client);

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            TapConfig(0, 8d / 11, 3d / 5, new ObjectSeedBox(0.6, 0.4, 0.3, 0.4)));

        outcome.FailureCode.Should().BeNull();
        VideoSegmentationRequest sent = client.Requests.Single();
        sent.BoxPrompts.Should().ContainSingle().Which.Width.Should().BeApproximately(0.3, 1e-9);
        sent.PointPrompts.Should().ContainSingle().Which.X.Should().BeApproximately(8d / 11, 1e-9, "the point is the tap, not the box centre");
    }

    [Fact]
    public async Task The_mask_cache_key_differs_per_seed_and_from_the_same_label_without_one()
    {
        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, TwoObjects())));

        await harness.RunAsync(TapConfig(0, 8d / 11, 3d / 5));
        await harness.RunAsync(TapConfig(0, 1d / 11, 1d / 5));
        await harness.RunAsync(TapConfig(0, 8d / 11, 3d / 5, noWords: false));

        List<string> keys = [.. harness.Store.KeysSeen.Select(harness.Store.ContentKey)];
        harness.Store.KeysSeen.Select(k => k.Seed).Distinct().Should().HaveCount(2, "a different point is a different seed");
        keys.Where((_, i) => harness.Store.KeysSeen[i].InstanceOrdinal == 0).Distinct().Count()
            .Should().BeGreaterThan(2, "point-only, other point and with-words keys are all different");
        harness.Store.KeysSeen.Should().OnlyContain(k => k.MatchScope == ObjectMatchScope.SingleTrack, "a seeded target is always one object");
        harness.Store.KeysSeen.Select(k => k.Seed).Should().OnlyContain(s => s.StartsWith(SegmentationTrackingService.TapRuleVersion + ";p:"));
    }

    [Fact]
    public async Task The_committed_point_answer_is_found_by_a_tap_at_the_recorded_point_and_missed_off_the_object()
    {
        // R10-tabby: the real point-only answer (empty prompt, x=380 y=880, frame 48 of a 120-frame 768x1344 clip at 24 fps).
        VideoSegmentationResult answer = ReadReplay("R10-tabby", 768, 1344, 120);

        Harness hit = new(new CountingClient(), replay: new ReplayClient(answer));
        SegmentationSourceOutcome found = await hit.RunAsync(
            TapConfig(48d / 24, 380d / 767, 880d / 1343), durationSec: 5, fps: 24, width: 768, height: 1344);
        found.FailureCode.Should().BeNull();
        found.Tracks.Should().ContainSingle().Subject.Facts!.EmptyFrames.Should().Be(0);

        Harness miss = new(new CountingClient(), replay: new ReplayClient(answer));
        SegmentationSourceOutcome none = await miss.RunAsync(
            TapConfig(48d / 24, 0.02, 0.02), durationSec: 5, fps: 24, width: 768, height: 1344);
        none.FailureCode.Should().Be(SegmentationFailureCodes.ObjectNotFoundAtPoint);
        none.Tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tapped_target_outside_the_hidden_object_analysis_run_fails_as_user_input_and_calls_nothing()
    {
        RecordingClient client = new(Scripted(12, 6, TwoObjects()));
        Harness harness = new(new CountingClient(), replay: client);

        SegmentationSourceOutcome outcome = await harness.RunAsync(TapConfig(0, 8d / 11, 3d / 5), allowSeeded: false);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.SeededTargetNotAllowed);
        outcome.FailureClass.Should().Be(FailureClass.UserInput, "not refunded");
        client.Requests.Should().BeEmpty("no vendor call");
        harness.Presign.Calls.Should().Be(0);
    }

    private static VideoSegmentationResult ReadReplay(string pack, int width, int height, int frames)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "prompted-region-effects", "sam31-rle", pack, "response.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Sam31VideoRleResponseReader.Read(document.RootElement, "fal-ai/sam-3-1/video-rle", width, height, expectedFrames: frames, maxObjects: 4);
    }

    private sealed class RecordingClient(VideoSegmentationResult result) : IVideoSegmentationClient
    {
        public List<VideoSegmentationRequest> Requests { get; } = [];

        public Task<VideoSegmentationResult> SegmentVideoAsync(VideoSegmentationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
