using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The object-tracking orchestration (seeds from a vision locator or a placed region, de-duplicated
/// against existing tracks) and the compile step's censor resolution — see docs/video-editing.md
/// "Tracking objects" / "Censoring tracked objects".
/// </summary>
public class ObjectTrackingAndCensorTests
{
    private const int W = 160, H = 90;

    /// <summary>A static textured square at (60..90, 30..60) on a flat background, every frame.</summary>
    private static byte[] StaticPatchClip(int frames)
    {
        var data = new byte[W * H * frames];
        for (int f = 0; f < frames; f++)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    data[f * W * H + y * W + x] = (byte)(x >= 60 && x < 90 && y >= 30 && y < 60
                        ? ((x * 37 + y * 91 + (x * y) % 17) % 200 + 40)
                        : 30);
        return data;
    }

    private static (ObjectTrackingService Service, Mock<IObjectLocator> Locator, Mock<IInferenceProviderResolver> Providers) Service(int frames)
    {
        var sampler = new Mock<IFrameGridSampler>();
        sampler.Setup(s => s.SampleGrayAsync(It.IsAny<string>(), It.IsAny<VideoScratchSpace>(), It.IsAny<double>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrameGridResult(StaticPatchClip(frames), W, H, 10, frames));
        var keyframes = new Mock<IKeyframeExtractor>();
        keyframes.Setup(k => k.ExtractKeyframeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        keyframes.Setup(k => k.ExtractCroppedKeyframeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var locator = new Mock<IObjectLocator>();
        var providers = new Mock<IInferenceProviderResolver>();
        var service = new ObjectTrackingService(sampler.Object, keyframes.Object, locator.Object, providers.Object,
            NullLogger<ObjectTrackingService>.Instance);
        return (service, locator, providers);
    }

    private static VideoScratchSpace Scratch() =>
        VideoScratchSpace.Create(new VideoEditingOptions { ScratchPath = Path.Combine(Path.GetTempPath(), "rb-obj-tests") }, Guid.NewGuid(), Guid.NewGuid());

    private static VideoAnalyzeStepConfig Config(params ObjectTrackTarget[] targets) =>
        new(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput), TrackObjects: targets);

    [Fact]
    public async Task A_placed_region_is_tracked_with_no_vision_call()
    {
        (ObjectTrackingService service, Mock<IObjectLocator> locator, Mock<IInferenceProviderResolver> providers) = Service(10);
        var region = new VideoInsertSurfaceQuad(60.0 / W, 30.0 / H, 90.0 / W, 30.0 / H, 60.0 / W, 60.0 / H, 90.0 / W, 60.0 / H);
        using VideoScratchSpace scratch = Scratch();

        ObjectTrackingResult result = await service.TrackAsync("in.mp4", scratch, W, H, 1.0,
            Config(new ObjectTrackTarget("sign", 0, region)), _ => Task.CompletedTask, CancellationToken.None);

        result.Tracks.Should().HaveCount(1);
        VideoInsertRegionTrack t = result.Tracks[0];
        t.Method.Should().Be("feature");
        t.Label.Should().Be("sign");
        t.Tuning.Should().NotBeNull();
        t.Keyframes.Should().HaveCount(10);
        t.Keyframes[^1].X0.Should().BeApproximately(60.0 / W, 0.02, "the patch never moves");
        result.VisionCalls.Should().Be(0);
        providers.Verify(p => p.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        locator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_labelled_target_is_found_by_the_locator_and_repeat_sightings_are_not_tracked_twice()
    {
        (ObjectTrackingService service, Mock<IObjectLocator> locator, Mock<IInferenceProviderResolver> providers) = Service(20);
        providers.Setup(p => p.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "vision", default, "http://x", "m", "key", 30));
        // The full-frame and the zoomed lookups both "find" the patch; the zoom reports it in the
        // middle of its crop, which is where CropAround centred it.
        locator.SetupSequence(l => l.LocateAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<string>(), "licence plate", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LocatedBox(60.0 / W, 30.0 / H, 90.0 / W, 60.0 / H)])
            .ReturnsAsync([new LocatedBox(0.3, 0.3, 0.7, 0.7)])
            .ReturnsAsync([new LocatedBox(60.0 / W, 30.0 / H, 90.0 / W, 60.0 / H)])
            .ReturnsAsync([new LocatedBox(0.3, 0.3, 0.7, 0.7)]);
        using VideoScratchSpace scratch = Scratch();

        ObjectTrackingResult result = await service.TrackAsync("in.mp4", scratch, W, H, 2.0,
            Config(new ObjectTrackTarget("licence plate")) with { ObjectDetectEverySec = 1.0, MaxObjectDetectFrames = 2 },
            _ => Task.CompletedTask, CancellationToken.None);

        result.SeedCount.Should().Be(2, "one sighting per lookup");
        result.Tracks.Should().HaveCount(1, "the second sighting is the object the first track already follows");
        result.VisionCalls.Should().Be(4, "a coarse and a zoomed lookup per sampled frame");
    }

    [Fact]
    public async Task No_vision_provider_degrades_with_a_reason_instead_of_failing()
    {
        (ObjectTrackingService service, _, Mock<IInferenceProviderResolver> providers) = Service(5);
        providers.Setup(p => p.ResolveVisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedInferenceProvider?)null);
        using VideoScratchSpace scratch = Scratch();

        ObjectTrackingResult result = await service.TrackAsync("in.mp4", scratch, W, H, 0.5,
            Config(new ObjectTrackTarget("licence plate")), _ => Task.CompletedTask, CancellationToken.None);

        result.Tracks.Should().BeEmpty();
        result.Degraded.Should().BeTrue();
        result.DegradedReason.Should().Contain("Vision provider");
    }

    [Fact]
    public async Task A_video_longer_than_its_file_says_is_not_a_clean_run_so_a_censor_compile_refuses()
    {
        // The frame pass stops at a fixed number of frames. A file with no length, or a longer one than its header
        // says, fills that buffer before its end: the track stops there while the video plays on.
        int cap = (int)FfmpegFrameGridSampler.PlanMaxGrayFrames(3000);
        (ObjectTrackingService service, _, _) = Service(cap);
        var region = new VideoInsertSurfaceQuad(60.0 / W, 30.0 / H, 90.0 / W, 30.0 / H, 60.0 / W, 60.0 / H, 90.0 / W, 60.0 / H);
        using VideoScratchSpace scratch = Scratch();

        ObjectTrackingResult result = await service.TrackAsync("in.mp4", scratch, W, H, 0,
            Config(new ObjectTrackTarget("sign", 0, region)), _ => Task.CompletedTask, CancellationToken.None);

        result.Tracks.Should().HaveCount(1, "what was read is still tracked");
        result.Degraded.Should().BeTrue();
        result.DegradedReason.Should().Be(ObjectTrackingService.SamplingStoppedEarlyReason);

        string refusal = VideoCompileStepExecutor.CensorDegradedMessage(result.DegradedReason);
        refusal.Should().Contain(ObjectTrackingService.SamplingStoppedEarlyReason).And.NotContain("Vision provider");
        // Running the same file again reads the same frames: the refusal must not send the customer in a circle.
        refusal.Should().Contain("save the video again").And.NotContain("once that is settled");
    }

    [Fact]
    public void A_reason_a_new_run_cannot_change_ends_with_its_own_next_step()
    {
        string limit = ObjectTrackingService.TrackLimitReason(3, 8);
        limit.Should().Be("3 sighting(s) were not tracked because MaxObjectTracks (8) was reached", "the recorded words are unchanged");

        VideoCompileStepExecutor.CensorDegradedMessage(limit)
            .Should().Contain(limit).And.Contain("raise MaxObjectTracks").And.NotContain("once that is settled").And.NotContain("Vision provider");
        VideoCompileStepExecutor.CensorDegradedMessage(ObjectTrackingService.SamplingStoppedEarlyReason)
            .Should().Contain("save the video again").And.NotContain("once that is settled");

        // A cause that passes on its own still says to run again; the Vision reasons still point at the provider.
        VideoCompileStepExecutor.CensorDegradedMessage(VideoAnalyzeStepExecutor.ObjectTrackingFailureReason(new ScratchBusyException()))
            .Should().EndWith("run the analysis again once that is settled.");
        VideoCompileStepExecutor.CensorDegradedMessage(ObjectTrackingService.NoVisionProviderReason).Should().EndWith("fix the Vision provider and run again.");
        VideoCompileStepExecutor.CensorDegradedMessage(ObjectTrackingService.VisionLookupFailedReason).Should().EndWith("fix the Vision provider and run again.");
    }

    [Theory]
    [InlineData(3000, 3000, false)]
    [InlineData(3001, 3000, false)]
    [InlineData(3067, 3000, false)]
    [InlineData(3068, 3000, true)]
    [InlineData(30, 30, false)]
    [InlineData(39, 30, true)]
    public void Only_a_full_frame_buffer_counts_as_stopped_early(int frames, int asked, bool stoppedEarly) =>
        ObjectTrackingService.SamplingStoppedEarly(frames, asked).Should().Be(stoppedEarly);

    [Fact]
    public void A_tracking_failure_names_its_cause_and_the_censor_refusal_blames_the_vision_provider_only_for_vision()
    {
        VideoAnalyzeStepExecutor.ObjectTrackingFailureReason(new ScratchBusyException()).Should().Contain("busy");
        VideoAnalyzeStepExecutor.ObjectTrackingFailureReason(VideoToolTimedOutException.For("ffmpeg gray sampling failed for 'a.mp4' (exitCode=-1, timedOut=True).", true, 600))
            .Should().Contain("took too long");
        string unreadable = VideoAnalyzeStepExecutor.ObjectTrackingFailureReason(
            VideoToolTimedOutException.For("ffmpeg gray sampling failed for 'a.mkv' (exitCode=1, timedOut=False).", false, 600));
        unreadable.Should().Contain("could not be read").And.NotContain("a.mkv", "the reason never carries the exception's own text");
        VideoAnalyzeStepExecutor.ObjectTrackingFailureReason(new InvalidOperationException("anything else")).Should().Be("object tracking failed");

        VideoCompileStepExecutor.CensorDegradedMessage(unreadable).Should().Contain(unreadable).And.NotContain("Vision provider");
        VideoCompileStepExecutor.CensorDegradedMessage("a vision lookup failed").Should().Contain("fix the Vision provider");
        VideoCompileStepExecutor.CensorDegradedMessage("no Vision provider is configured to find labelled objects").Should().Contain("fix the Vision provider");
        VideoCompileStepExecutor.CensorDegradedMessage(null).Should().Contain("fix the Vision provider").And.NotContain("()");
    }

    [Fact]
    public void Detection_times_cover_the_whole_clip_even_past_the_lookup_cap()
    {
        ObjectTrackingService.DetectionTimes(10, 1, 20).Should().HaveCount(11);
        IReadOnlyList<double> capped = ObjectTrackingService.DetectionTimes(60, 1, 6);
        capped.Should().HaveCount(6);
        capped[^1].Should().BeGreaterThan(45, "the cap spreads lookups over the clip instead of stopping early");
    }

    [Fact]
    public void Locator_boxes_are_normalized_ordered_and_filtered()
    {
        var reply = new VisionObjectLocator.LocatorReply(
        [
            new VisionObjectLocator.LocatorBox(600, 400, 500, 300),   // reversed corners
            new VisionObjectLocator.LocatorBox(10, 10, 10, 10)        // degenerate
        ]);

        IReadOnlyList<LocatedBox> boxes = VisionObjectLocator.ToBoxes(reply);

        boxes.Should().ContainSingle();
        boxes[0].Should().Be(new LocatedBox(0.5, 0.3, 0.6, 0.4));
    }

    // ---------------------------------------------------------------------
    // Censor resolution
    // ---------------------------------------------------------------------

    private static VideoInsertRegionTrack PlateTrack(string label = "licence plate") => new(
        Id: "r0", ShotId: "s0", StartSec: 1, EndSec: 9,
        Keyframes:
        [
            new VideoInsertQuadKeyframe(1, 0.40, 0.50, 0.50, 0.50, 0.40, 0.55, 0.50, 0.55),
            new VideoInsertQuadKeyframe(9, 0.45, 0.50, 0.55, 0.50, 0.45, 0.55, 0.55, 0.55)
        ],
        Confidence: 0.2, MeanAreaRatio: 0.005, MeanAspectRatio: 3.5, MotionClass: "Slow", ColorName: "none",
        Method: "feature", Label: label);

    private static VideoAnalysisArtifact Artifact(params VideoInsertRegionTrack[] tracks) => new(
        Version: 2,
        Media: new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
        Shots: [new VideoAnalysisShot("s0", 0, 10)],
        SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"],
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        InsertRegions: tracks);

    private static OutputTimeline TwoPieceCut()
    {
        // Keeps 0-3 s and 6-10 s of the source: the plate (1-9 s) survives into BOTH pieces.
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 3, 0, 3, 0, 90),
            new(6, 10, 6, 10, 180, 300)
        };
        return OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));
    }

    private static VideoCompileStepConfig CensorConfig(params string[] labels) =>
        new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1,
            CensorLabels: labels, CensorHoldSec: 0);

    [Fact]
    public void Censor_covers_every_piece_of_the_cut_the_object_survives_into()
    {
        (List<ResolvedCensorRegion> regions, var node) = VideoCompileStepExecutor.ResolveCensorRegions(
            CensorConfig("Licence Plate"), Artifact(PlateTrack()), TwoPieceCut(),
            new VideoAnalysisMedia(10, 30, 1, 1920, 1080), sourcesNormalizedToCanvas: false);

        regions.Should().HaveCount(2, "an insert maps to its first piece only; a censor must cover both");
        regions[0].OutputStartSec.Should().BeApproximately(1, 1e-9);
        regions[0].OutputEndSec.Should().BeApproximately(3, 1e-9);
        regions[1].OutputStartSec.Should().BeApproximately(3, 1e-9);
        regions[1].OutputEndSec.Should().BeApproximately(6, 1e-9);
        node["unmatchedLabels"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Censor_ignores_confidence_and_reports_labels_that_matched_nothing()
    {
        (List<ResolvedCensorRegion> regions, var node) = VideoCompileStepExecutor.ResolveCensorRegions(
            CensorConfig("licence plate", "face"), Artifact(PlateTrack()), TwoPieceCut(),
            new VideoAnalysisMedia(10, 30, 1, 1920, 1080), sourcesNormalizedToCanvas: false);

        regions.Should().NotBeEmpty("a 0.2-confidence track is still obscured — over-obscuring is the safe failure");
        node["unmatchedLabels"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("face");
    }

    [Fact]
    public void Censor_padding_is_a_margin_of_the_longer_side_on_every_side()
    {
        (List<ResolvedCensorRegion> regions, _) = VideoCompileStepExecutor.ResolveCensorRegions(
            CensorConfig("*") with { CensorPadding = 0.5 }, Artifact(PlateTrack()), TwoPieceCut(),
            new VideoAnalysisMedia(10, 30, 1, 1920, 1080), sourcesNormalizedToCanvas: false);

        // The plate is 0.10 x 0.05 of the frame: 192 x 54 px, so the longer side is 192 px and
        // the margin 96 px on every side — the box grows to about 384 x 246 px.
        CensorFilterBuilder.MaskBox box = CensorFilterBuilder.BoxesFor(regions[0], 1, 1)[0];
        box.Width.Should().BeInRange(380, 400);
        box.Height.Should().BeInRange(240, 260, "a thin plate gets as much margin vertically as horizontally");
    }

    [Fact]
    public void Hold_extends_a_track_at_both_ends()
    {
        VideoInsertRegionTrack held = VideoCompileStepExecutor.HoldTrackEnds(PlateTrack(), 0.5);
        held.StartSec.Should().Be(0.5);
        held.EndSec.Should().Be(9.5);
        held.Keyframes.First().TimeSec.Should().Be(0.5);
        held.Keyframes.Last().TimeSec.Should().Be(9.5);
    }

    [Fact]
    public void Censor_chain_obscures_once_and_moves_one_mask_box_per_chunk()
    {
        var region = new ResolvedCensorRegion("r0", "licence plate", 1, 3,
            [new InsertQuadFrame(0, 100, 100, 300, 100, 100, 160, 300, 160), new InsertQuadFrame(60, 200, 100, 400, 100, 200, 160, 400, 160)],
            30, 1);

        string chain = CensorFilterBuilder.BuildFilterChain("[vcen0]", [region, region with { RegionId = "r1" }], 1920, 1080, CensorStyle.Pixelate, "[vout]", 10);

        chain.Should().StartWith("[vcen0]split=2[cnb][cnc]");
        System.Text.RegularExpressions.Regex.Matches(chain, "flags=neighbor").Count.Should().Be(1, "the picture is obscured once, whatever the region count");
        chain.Should().NotContain("perspective", "perspective overflows on small targets");
        System.Text.RegularExpressions.Regex.Matches(chain, "overlay=x='").Count.Should().Be(4, "two regions x two one-second chunks");
        chain.Should().Contain("enable='between(t,1,2)'").And.Contain("enable='between(t,2,3)'");
        chain.Should().Contain("alphamerge").And.EndWith("shortest=1[vout]");
    }

    [Fact]
    public void Mask_boxes_follow_the_region_and_cover_its_largest_extent_in_each_chunk()
    {
        var region = new ResolvedCensorRegion("r0", "x", 0, 2,
            [new InsertQuadFrame(0, 0, 0, 100, 0, 0, 20, 100, 20), new InsertQuadFrame(60, 50, 10, 250, 10, 50, 50, 250, 50)], 30, 1);

        List<CensorFilterBuilder.MaskBox> boxes = CensorFilterBuilder.BoxesFor(region, 1, 1);

        boxes.Should().HaveCount(2);
        boxes[0].Width.Should().BeGreaterThanOrEqualTo(200, "the chunk spans both keyframes, so its box fits the larger one");
        boxes[0].TopLeft.First().TimeSec.Should().Be(0);
    }

    [Fact]
    public async Task Vision_calls_retry_with_a_plainer_format_when_the_provider_rejects_json_schema()
    {
        // DeepSeek's real answer to a json_schema response_format.
        var client = new Mock<Microsoft.Extensions.AI.IChatClient>();
        int calls = 0;
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<Microsoft.Extensions.AI.ChatMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<Microsoft.Extensions.AI.ChatMessage>, Microsoft.Extensions.AI.ChatOptions?, CancellationToken>((_, o, _) =>
            {
                calls++;
                if (o?.ResponseFormat is Microsoft.Extensions.AI.ChatResponseFormatJson { Schema: not null })
                    throw new InvalidOperationException("HTTP 400 (invalid_request_error) This response_format type is unavailable now");
                return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                    new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "{\"objects\": []}")));
            });

        Microsoft.Extensions.AI.ChatResponse response = await StructuredChatFallback.GetResponseAsync(
            client.Object, [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "find")],
            Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<VisionObjectLocator.LocatorReply>(), 100,
            NullLogger.Instance, CancellationToken.None);

        calls.Should().Be(2, "the schema request is rejected, plain JSON mode is accepted");
        response.Text.Should().Contain("objects");
    }

    [Fact]
    public void Only_format_errors_count_as_format_rejections()
    {
        StructuredChatFallback.IsFormatRejection(new Exception("This response_format type is unavailable now")).Should().BeTrue();
        StructuredChatFallback.IsFormatRejection(new Exception("HTTP 401 unauthorized")).Should().BeFalse();
    }

    [Fact]
    public async Task An_unreadable_vision_reply_is_a_failed_lookup_not_an_empty_frame()
    {
        // A refusal or truncated answer says nothing about the frame; counting it as "no plates"
        // would let a censor run publish whatever was there.
        var client = new Mock<Microsoft.Extensions.AI.IChatClient>();
        client.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<Microsoft.Extensions.AI.ChatMessage>>(),
                It.IsAny<Microsoft.Extensions.AI.ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "I can't help with identifying plates.")));
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(client.Object);
        var locator = new VisionObjectLocator(factory.Object, NullLogger<VisionObjectLocator>.Instance);
        string image = Path.Combine(Path.GetTempPath(), $"locator-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(image, [0xFF, 0xD8, 0xFF]);

        Func<Task> act = () => locator.LocateAsync(
            new ResolvedInferenceProvider(Guid.NewGuid(), "v", default, "http://x", "m", "k", 30), image, "licence plate", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
