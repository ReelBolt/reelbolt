#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The analyze side of text-prompted segmentation (initiative "prompted region effects", T-004): consent,
/// presign, one vendor call, content cache, mask video, artifact track — every refusal specific, every
/// test offline. The vendor is a counting fake; the content cache is an in-memory store with the real
/// key derivation. No network, no key, no live call.
/// </summary>
public sealed class SegmentationTrackingTests
{
    internal static readonly Guid Project = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Organization = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // -----------------------------------------------------------------
    // Refusals
    // -----------------------------------------------------------------

    [Fact]
    public async Task Without_egress_consent_nothing_leaves_the_host()
    {
        var provider = new CountingClient();
        var presign = new CountingPresign();
        Harness harness = new(provider, presign);

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig(consent: false));

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressRefused);
        outcome.FailureClass.Should().Be(FailureClass.UserInput);
        outcome.FailureMessage.Should().Contain("speedometer").And.Contain("clip.mp4");
        // The customer's message names their own target; the LOG sentence must not (F-1).
        outcome.FailureLogMessage.Should().Contain("clip.mp4").And.NotContain("speedometer");
        presign.Calls.Should().Be(0, "a refused step must not mint a URL to the customer's footage");
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task With_no_provider_the_step_refuses_and_names_the_option()
    {
        Harness harness = new(new CountingClient());
        harness.Provider = null;

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.NoProvider);
        outcome.FailureClass.Should().Be(FailureClass.UserInput);
        outcome.FailureMessage.Should().Contain("default VideoSegmentation provider");
        harness.Presign.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_deployment_with_no_presigner_refuses_with_its_own_code()
    {
        Harness harness = new(new CountingClient(), omitPresigner: true);

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.EgressUnavailable);
        outcome.FailureClass.Should().Be(FailureClass.PlatformInternal);
        outcome.FailureMessage.Should().Contain("presigner");
    }

    [Fact]
    public async Task A_source_longer_than_the_cap_refuses_naming_both_numbers_and_is_never_truncated()
    {
        var provider = new CountingClient();
        Harness harness = new(provider);

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(maxSeconds: 60), durationSec: 125.5);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.SourceTooLong);
        outcome.FailureClass.Should().Be(FailureClass.UserInput);
        outcome.FailureMessage.Should().Contain("125.5").And.Contain("60s").And.Contain("not truncated");
        provider.Calls.Should().Be(0, "refusing on length must happen before the paid call");
        harness.Presign.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_plan_without_the_feature_refuses_before_anything_is_minted_or_called()
    {
        var provider = new CountingClient();
        Harness harness = new(provider, entitlement: new StubEntitlements(new OrganizationEntitlementSet
        {
            Tier = "trial",
            SegmentationAllowed = false
        }));

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().Be(PlanGate.NotEntitledCode);
        outcome.FailureClass.Should().Be(FailureClass.EntitlementDenied);
        outcome.FailureMessage.Should().Contain("segmentation");
        harness.Presign.Calls.Should().Be(0);
        provider.Calls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // The happy path, the cache, and the mask
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_miss_calls_the_vendor_once_and_a_second_identical_run_is_free()
    {
        var provider = new CountingClient();
        Harness harness = new(provider);

        SegmentationSourceOutcome first = await harness.RunAsync(SegmentationConfig());
        SegmentationSourceOutcome second = await harness.RunAsync(SegmentationConfig());

        first.FailureCode.Should().BeNull();
        first.ProviderCalls.Should().Be(1);
        first.CacheHits.Should().Be(0);
        second.ProviderCalls.Should().Be(0, "a content-cache hit makes no vendor call at all");
        second.CacheHits.Should().Be(1);
        provider.Calls.Should().Be(1, "one paid call for two identical runs");

        first.Tracks.Should().HaveCount(1);
        VideoSegmentationTrack track = first.Tracks[0];
        track.Id.Should().BeEmpty("the executor numbers g{n} in artifact order");
        track.Prompt.Should().Be("speedometer");
        track.SourceIndex.Should().Be(0);
        track.FrameCount.Should().Be(6);
        track.Fps.Should().Be(24);
        track.MaskStorageKey.Should().Be(SegmentationMaskStore.MaskStorageKeyFor(Project, first.Tracks[0].MaskStorageKey.Split('/').Last()[..^4]));
        track.MaskStorageKey.Should().StartWith($"projects/{Project:D}/agentFiles/segmentation/");
        track.MaskContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
        track.Facts!.EmptyFrames.Should().Be(0);
    }

    [Fact]
    public async Task A_leading_head_gap_is_recorded_as_facts_and_shifts_the_start()
    {
        // The exact shape the takeover found: the vendor's answer carries no object for the first N
        // frames, so the mask is empty at the head and the effect only starts later. The facts must say
        // where the object was first and last found, and the track's own start must follow it.
        Harness harness = new(new CountingClient { LeadingEmptyFrames = 2 });

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().BeNull();
        VideoSegmentationTrack track = outcome.Tracks.Single();
        track.Facts!.FirstNonEmptyFrame.Should().Be(2);
        track.Facts.LastNonEmptyFrame.Should().Be(5);
        track.Facts.EmptyFrames.Should().Be(2);
        track.StartSec.Should().BeApproximately(2d / 24, 1e-9);
    }

    [Fact]
    public async Task Every_written_mask_frame_lands_on_the_models_own_box()
    {
        // The one claim this whole path rests on: the mask the compile step will apply is aligned with
        // the vendor's own per-frame boxes. Decoded from the pbm frames the service actually wrote, back
        // to pixels, the covered pixels must sit inside the box the vendor reported for that frame — a
        // row/column transposition or an off-by-one shift fails here even though every frame is non-empty.
        Harness harness = new(new CountingClient());

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().BeNull();
        harness.Workspace.PbmFrames.Should().Be(6);

        for (int frame = 1; frame <= 6; frame++)
        {
            byte[] mask = DecodePbm(harness.Workspace.Files[SegmentationMaskArgvBuilder.FrameVirtualPath(frame)]);
            Sam31VideoRleDecoder.SegmentationMaskMeasurement measured =
                Sam31VideoRleDecoder.Measure(mask, 12, 6);

            // The fake reports one 2x2 block at pixel (1,1) on every frame.
            measured.CoveredPixels.Should().Be(4, $"frame {frame}");
            measured.X.Should().BeApproximately(1d / 12, 1e-9);
            measured.Y.Should().BeApproximately(1d / 6, 1e-9);
            measured.W.Should().BeApproximately(2d / 12, 1e-9);
            measured.H.Should().BeApproximately(2d / 6, 1e-9);
        }

        outcome.Tracks[0].BoxKeyframes.Should().NotBeEmpty();
        outcome.Tracks[0].BoxKeyframes![0].X.Should().BeApproximately(1d / 12, 1e-9);
    }

    // -----------------------------------------------------------------
    // Slot-merged selection (T-020): the vendor renumbers the object, we follow it
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_vendor_renumbering_the_object_does_not_drop_its_early_or_late_frames()
    {
        // The measured answer (trials/fal/T020, replayed verbatim): the vendor renumbers the SAME object
        // three times — id 0 on frames 7..17, id 1 on 18..199, id 2 on 211..224 at 60 fps. The merged
        // track must span all three, leave empty only frames 0..6 (no object) and the 11-frame gap
        // 200..210, and paint each id's own frames. The one-slot selector picks id 1 and renders `empty`
        // for frames 0..17 and 200..224 — which is the reported symptom — so this test fails on it.
        Harness harness = new(new CountingClient(), replay: new ReplayClient(T020Reacquire()));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(), durationSec: 3.75, fps: 60, width: 1080, height: 1920);

        outcome.FailureCode.Should().BeNull();
        VideoSegmentationTrack track = outcome.Tracks.Single();
        track.Facts!.FirstNonEmptyFrame.Should().Be(7);
        track.Facts.LastNonEmptyFrame.Should().Be(224);
        track.Facts.EmptyFrames.Should().Be(18, "frames 0-6 and the 11-frame gap 200-210 have no object");
        track.Facts.LongestGapFrames.Should().Be(11);
        track.StartSec.Should().BeApproximately(7d / 60, 1e-9);
        track.EndSec.Should().BeApproximately(224d / 60, 1e-9);

        // The written mask carries EACH merged id's frames, decoded from the PBM the service wrote.
        CoveredPixels(harness, 10, 1080, 1920).Should().BePositive("frame 10 is id 0's span");
        CoveredPixels(harness, 100, 1080, 1920).Should().BePositive("frame 100 is id 1's span");
        CoveredPixels(harness, 220, 1080, 1920).Should().BePositive("frame 220 is id 2's span");
        CoveredPixels(harness, 3, 1080, 1920).Should().Be(0, "frames 0-6 genuinely have no object");
    }

    [Fact]
    public async Task Two_slots_present_on_the_same_frames_are_never_merged()
    {
        // The live F2/FACE shape: one prompt, several heavily overlapping slots. Merging those would
        // paint far more of the picture than the object, so a slot that shares a frame with the track is
        // never merged: the mask is the anchor slot's alone. Id 0 and id 1 are both on every frame; the
        // anchor is id 0 (a covered-pixel tie resolves to the lowest id), and id 1 must not join it.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++)
        {
            frames.Add(Frame(frame, (0, "13 2 25 2"), (1, "19 2 31 2")));
        }

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        // SingleTrack isolates the merge rule: two slots on one frame are never merged into ONE track.
        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(matchScope: ObjectMatchScope.SingleTrack));

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().ContainSingle();
        // If id 1 had been merged the mask would cover two 2x2 blocks (8 px); it must be id 0's 4 alone.
        CoveredPixels(harness, 1, 12, 6).Should().Be(4);
    }

    [Fact]
    public async Task A_slot_further_away_than_the_reacquire_window_is_not_merged()
    {
        // 0.5 s at 24 fps is 12 frames. id 0 on frames 0..5 and id 1 on 30..39 are 24 frames apart —
        // "a different moment of the film" — so only the anchor (id 0) is the object.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++) frames.Add(Frame(frame, (0, "13 2 25 2")));
        for (int frame = 30; frame < 40; frame++) frames.Add(Frame(frame, (1, "13 2 25 2")));

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        // SingleTrack: this test is about the re-acquisition window, not about how many instances a
        // prompt may match — under AllInstances the far slot is legitimately a second instance.
        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(matchScope: ObjectMatchScope.SingleTrack), durationSec: 5, fps: 24);

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().ContainSingle();
        CoveredPixels(harness, 2, 12, 6).Should().Be(4, "the anchor's own frames are painted");
        CoveredPixels(harness, 35, 12, 6).Should().Be(0, "id 1 is beyond the 12-frame window, so it is not the object");
    }

    [Fact]
    public async Task An_earlier_slot_that_joins_the_anchor_is_recovered()
    {
        // The anchor is the MIDDLE id (id 1 covers more pixels), and an earlier id joins it backwards
        // within the window — a segment that precedes the anchor must be recovered too.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++) frames.Add(Frame(frame, (0, "13 2 25 2")));       // id 0: frames 0..5
        for (int frame = 10; frame < 20; frame++) frames.Add(Frame(frame, (1, "26 3 38 3 50 3"))); // id 1: frames 10..19

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig(), durationSec: 1, fps: 30);

        outcome.FailureCode.Should().BeNull();
        CoveredPixels(harness, 2, 12, 6).Should().Be(4, "id 0 precedes the anchor and joins it backwards");
        CoveredPixels(harness, 15, 12, 6).Should().Be(9, "the anchor's own frames are painted");
    }

    [Fact]
    public async Task An_answer_whose_ids_merge_into_more_than_the_cap_is_refused()
    {
        // The merge work is driven by the vendor's own id count, so the rule is a bounded sweep plus an
        // explicit ceiling: 80 ids, each on its own frame and adjacent to the next, would all chain into
        // one object. Past MaxMergedSlots the answer is refused instead of scanned.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < SegmentationTrackingService.MaxMergedSlots + 16; frame++)
        {
            frames.Add(Frame(frame, (frame, "13 2 25 2")));
        }

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(), durationSec: frames.Count / 24.0, fps: 24);

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.BadResponse);
        outcome.FailureClass.Should().Be(FailureClass.PlatformInternal);
        outcome.FailureMessage.Should().Contain(SegmentationTrackingService.MaxMergedSlots.ToString());
    }

    [Fact]
    public async Task Many_ids_under_the_cap_still_merge_into_one_track()
    {
        // The bounded sweep, not a fixed point: 40 consecutive single-frame ids join into one object.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 40; frame++)
        {
            frames.Add(Frame(frame, (frame, "13 2 25 2")));
        }

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(), durationSec: 40 / 24.0, fps: 24);

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().ContainSingle();
        outcome.Tracks[0].Facts!.EmptyFrames.Should().Be(0);
        outcome.Tracks[0].Facts.FirstNonEmptyFrame.Should().Be(0);
        outcome.Tracks[0].Facts.LastNonEmptyFrame.Should().Be(39);
    }

    // -----------------------------------------------------------------
    // MatchScope (T-021, D-23): every instance the prompt matches, never every emitted slot
    // -----------------------------------------------------------------

    [Fact]
    public async Task AllInstances_follows_every_distinct_instance_of_the_prompt()
    {
        // Two comparable blocks present on the same frames: two instances, not one.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++)
        {
            frames.Add(Frame(frame, (0, "13 2 25 2"), (1, "19 2 31 2")));
        }

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().HaveCount(2, "AllInstances is the default: every instance the prompt matches");
        outcome.Tracks.Should().OnlyContain(t => t.Facts!.EmptyFrames == 0);
        outcome.Tracks.Select(t => t.MaskStorageKey).Should().OnlyHaveUniqueItems("each instance has its own mask");
    }

    [Fact]
    public async Task SingleTrack_follows_one_instance_of_the_same_answer()
    {
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++)
        {
            frames.Add(Frame(frame, (0, "13 2 25 2"), (1, "19 2 31 2")));
        }

        Harness harness = new(new CountingClient(), replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(matchScope: ObjectMatchScope.SingleTrack));

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().ContainSingle("SingleTrack keeps the deliberate this-particular-one case");
    }

    [Fact]
    public async Task The_live_person_answer_yields_one_instance_not_one_track_per_slot()
    {
        // THE invariant: the live F2/FACE answer emits 17 slots for one prompt, of which one covers 27 % of
        // the frame and the rest 0.2-1 %. Those are parts and noise, not 17 people, so AllInstances must
        // yield exactly ONE track — otherwise a clip the model answers with many slots becomes "blur
        // everything".
        Harness harness = new(new CountingClient(), replay: new ReplayClient(F2Face()));

        SegmentationSourceOutcome outcome = await harness.RunAsync(
            SegmentationConfig(prompt: "person"), durationSec: 5.875, fps: 24, width: 1366, height: 768);

        outcome.FailureCode.Should().BeNull();
        outcome.Tracks.Should().ContainSingle(
            "AllInstances means every instance the prompt MATCHES, never every object the model emitted");
    }

    [Fact]
    public async Task The_cache_returns_the_whole_instance_set_without_a_vendor_call()
    {
        // Two instances, two cache entries: a repeat run must read the set back (ordinal 0 states how
        // many there are) rather than making a vendor call or serving a single half of the answer.
        var provider = new CountingClient();
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++)
        {
            frames.Add(Frame(frame, (0, "13 2 25 2"), (1, "19 2 31 2")));
        }

        Harness harness = new(provider, replay: new ReplayClient(Scripted(12, 6, frames)));

        SegmentationSourceOutcome first = await harness.RunAsync(SegmentationConfig());
        SegmentationSourceOutcome second = await harness.RunAsync(SegmentationConfig());

        first.Tracks.Should().HaveCount(2);
        second.Tracks.Should().HaveCount(2, "the cached set is read back whole");
        second.ProviderCalls.Should().Be(0);
        second.CacheHits.Should().Be(1);
    }

    [Fact]
    public async Task A_scope_switch_never_reuses_the_other_scopes_cached_mask_set()
    {
        // MatchScope is the user's privacy control ("this one object, not every one"), so the content
        // cache is segmented by it. An AllInstances answer must never answer a SingleTrack plan (that
        // would blur every instance the user deliberately excluded), and the one-track answer must never
        // stand in for the full set.
        List<VideoSegmentationFrame> frames = [];
        for (int frame = 0; frame < 6; frame++)
        {
            frames.Add(Frame(frame, (0, "13 2 25 2"), (1, "19 2 31 2")));
        }

        ReplayClient vendor = new(Scripted(12, 6, frames));
        Harness harness = new(new CountingClient(), replay: vendor);

        SegmentationSourceOutcome all = await harness.RunAsync(SegmentationConfig());
        SegmentationSourceOutcome single = await harness.RunAsync(
            SegmentationConfig(matchScope: ObjectMatchScope.SingleTrack));

        all.Tracks.Should().HaveCount(2);
        single.Tracks.Should().ContainSingle("SingleTrack keeps the deliberate this-particular-one case");
        single.CacheHits.Should().Be(0, "the AllInstances set must not answer another scope");
        vendor.Calls.Should().Be(2, "each scope has its own content key and its own vendor answer");

        // Each scope now hits ITS OWN set; a hit is zero vendor calls.
        SegmentationSourceOutcome singleAgain = await harness.RunAsync(
            SegmentationConfig(matchScope: ObjectMatchScope.SingleTrack));
        SegmentationSourceOutcome allAgain = await harness.RunAsync(SegmentationConfig());

        singleAgain.Tracks.Should().ContainSingle();
        singleAgain.CacheHits.Should().Be(1);
        allAgain.Tracks.Should().HaveCount(2);
        allAgain.CacheHits.Should().Be(1);
        vendor.Calls.Should().Be(2);
    }

    /// <summary>The live F2/FACE answer, replayed through the real reader (17 slots for one prompt).</summary>
    private static VideoSegmentationResult F2Face()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "TestData", "prompted-region-effects", "f2-face", "response.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Sam31VideoRleResponseReader.Read(
            document.RootElement, "fal-ai/sam-3-1/video-rle", 1366, 768, expectedFrames: 141, maxObjects: 4);
    }

    /// <summary>The T-020 fal answer, replayed through the real reader (one object under three ids).</summary>
    private static VideoSegmentationResult T020Reacquire()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "TestData", "prompted-region-effects", "sam31-rle", "T020-reacquire", "response.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Sam31VideoRleResponseReader.Read(
            document.RootElement, "fal-ai/sam-3-1/video-rle", 1080, 1920, expectedFrames: 225, maxObjects: 4);
    }

    /// <summary>A whole answer built from raw frames (no boxes, no scores) — the synthetic shapes above.</summary>
    private static VideoSegmentationResult Scripted(int width, int height, IReadOnlyList<VideoSegmentationFrame> frames) =>
        new("fal-ai/sam-3-1/video-rle", width, height, frames, "falai:model", 0.01m, frames.Count);

    /// <summary>One frame carrying the given (id, rle) objects.</summary>
    private static VideoSegmentationFrame Frame(int index, params (int Id, string Rle)[] objects) =>
        new(index, [.. objects.Select(o => new VideoSegmentationObjectRle(o.Id, o.Rle))]);

    /// <summary>Covered pixels on one written mask frame, decoded from the PBM the service wrote.</summary>
    private static int CoveredPixels(Harness harness, int frame, int width, int height)
    {
        byte[] mask = DecodePbm(harness.Workspace.Files[SegmentationMaskArgvBuilder.FrameVirtualPath(frame + 1)]);
        return Sam31VideoRleDecoder.Measure(mask, width, height).CoveredPixels;
    }

    /// <summary>Reads a binary <c>pbm</c> (P4) back to one byte per pixel, 255 = OBJECT.
    /// A set PBM bit is BLACK, so the object is the CLEAR bit: this inverts, matching the artifact
    /// contract (255 = object) that the encode step writes.</summary>
    private static byte[] DecodePbm(byte[] pbm)
    {
        int cursor = 0;
        while (pbm[cursor++] != (byte)'\n') { }
        int dimensionsEnd = cursor;
        while (pbm[dimensionsEnd] != (byte)'\n') dimensionsEnd++;
        string[] size = System.Text.Encoding.ASCII
            .GetString(pbm, cursor, dimensionsEnd - cursor)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int width = int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture);
        int height = int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture);

        int rowBytes = (width + 7) / 8;
        int dataStart = dimensionsEnd + 1;
        byte[] mask = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte packed = pbm[dataStart + (y * rowBytes) + (x >> 3)];
                mask[(y * width) + x] = (packed & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
            }
        }

        return mask;
    }

    [Fact]
    public async Task The_mask_frames_are_written_one_per_source_frame_and_encoded_once()
    {
        Harness harness = new(new CountingClient());

        await harness.RunAsync(SegmentationConfig());

        harness.Workspace.PbmFrames.Should().Be(6);
        harness.Workspace.FfmpegCalls.Should().ContainSingle();
        harness.Workspace.FfmpegCalls[0].Should().ContainInOrder("-f", "image2", "-i");
        harness.Workspace.FfmpegCalls[0].Should().Contain("ffv1").And.Contain("gray");
    }

    [Fact]
    public async Task The_prompt_never_reaches_a_log_line()
    {
        // F-1 (security verdict "prompted-region-effects"). The prompt is the customer's OWN sentence
        // about the private subject of their footage, and AllowSourceMediaEgress exists because that
        // content leaves the host under consent. A plaintext Information line defeats the boundary at the
        // very moment consent was granted, and log retention is not the project's retention: purging
        // agentFiles/segmentation/ never deletes the line. The length and the content key are the stable
        // correlation handle that survives instead.
        const string secret = "the red umbrella";
        RecordingLogger<SegmentationTrackingService> log = new();
        Harness harness = new(new CountingClient(), logger: log);

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig(prompt: secret));

        outcome.FailureCode.Should().BeNull();
        log.Entries.Should().NotBeEmpty("the successful path must still leave a diagnostic trace");
        foreach ((Microsoft.Extensions.Logging.LogLevel level, string message) in
                 log.Entries.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Information))
        {
            message.Should().NotContain(secret, "the prompt is never logged at information or above");
            message.Should().NotContain("umbrella");
        }

        string contentKey = harness.Store.Entries.Keys.Single();
        string keyPrefix = contentKey[..SegmentationTrackingService.ContentKeyLogPrefixChars];
        log.Entries.Any(e => e.Message.Contains(secret.Length + "-char prompt", StringComparison.Ordinal)
                             && e.Message.Contains(keyPrefix, StringComparison.Ordinal))
            .Should().BeTrue("the prompt's LENGTH and the content-key prefix replace the prompt itself");
    }

    [Fact]
    public async Task The_prompt_is_sanitized_capped_and_never_carries_a_newline()
    {
        Harness harness = new(new CountingClient());
        string noisy = "  the\tspeedo\nmetre   dial " + new string('x', 400);

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig(prompt: noisy));

        string prompt = outcome.PromptsSent.Single();
        prompt.Should().StartWith("the speedo metre dial");
        prompt.Should().NotContain("\n").And.NotContain("\t");
        prompt.Length.Should().BeLessThanOrEqualTo(SegmentationTrackingService.MaxPromptChars);
    }

    // -----------------------------------------------------------------
    // Failure classes
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_platform_provider_failure_is_refunded_and_a_byo_one_is_not()
    {
        Harness platform = new(new CountingClient { Throw = new HttpRequestException("boom"), SucceedTimes = 0 });
        Harness byo = new(new CountingClient { Throw = new HttpRequestException("boom"), SucceedTimes = 0 });
        byo.Provider = byo.Provider! with { IsPlatformManaged = false };

        SegmentationSourceOutcome platformOutcome = await platform.RunAsync(SegmentationConfig());
        SegmentationSourceOutcome byoOutcome = await byo.RunAsync(SegmentationConfig());

        platformOutcome.FailureCode.Should().Be(SegmentationFailureCodes.ProviderError);
        platformOutcome.FailureClass.Should().Be(FailureClass.PlatformProviderError);
        platformOutcome.FailureMessage.Should().Contain("speedometer").And.Contain("boom");
        // The log sentence carries the prompt's length and content key, never the prompt (F-1).
        platformOutcome.FailureLogMessage.Should().Contain("11-char prompt").And.NotContain("speedometer");
        byoOutcome.FailureClass.Should().Be(FailureClass.ByoProviderError, "the customer's own key keeps the consumption");
    }

    [Fact]
    public async Task A_mask_at_the_wrong_size_is_a_bad_response_not_a_rescale()
    {
        Harness harness = new(new CountingClient { Width = 8, Height = 8 });

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig());

        outcome.FailureCode.Should().Be(SegmentationFailureCodes.BadResponse);
        outcome.FailureClass.Should().Be(FailureClass.PlatformInternal);
        outcome.FailureMessage.Should().Contain("8x8").And.Contain("12x6");
    }

    [Fact]
    public async Task A_partial_result_reports_the_track_that_finished_before_the_failure()
    {
        // Two targets: the first succeeds, the second's call fails. The outcome carries the finished
        // track AND the failure, so the report can say which target did what.
        var provider = new CountingClient { SucceedTimes = 1 };
        Harness harness = new(provider);

        SegmentationSourceOutcome outcome = await harness.RunAsync(SegmentationConfig(extraTarget: true));

        outcome.Tracks.Should().ContainSingle();
        outcome.Tracks[0].Prompt.Should().Be("speedometer");
        outcome.FailureCode.Should().Be(SegmentationFailureCodes.ProviderError);
        outcome.PromptsSent.Should().Equal("speedometer", "gauge needle");
    }

    // -----------------------------------------------------------------
    // The bounded view
    // -----------------------------------------------------------------

    [Fact]
    public void The_view_node_is_qualitative_and_never_carries_the_mask_key_or_a_number_per_frame()
    {
        VideoSegmentationTrack track = new(
            Id: "g1",
            SourceIndex: 0,
            Prompt: "speedometer",
            StartSec: 1.5,
            EndSec: 4.25,
            FrameCount: 120,
            Fps: 24,
            Coverage: 0.21,
            Confidence: 0.9,
            MaskStorageKey: "projects/p/agentFiles/segmentation/abc.mkv",
            MaskContentHash: new string('a', 64),
            Model: "fal-ai/sam-3-1/video-rle",
            BoxKeyframes: [new VideoSegmentationBoxFrame(1.5, 0.1, 0.1, 0.2, 0.2)],
            Facts: new VideoSegmentationFacts(3, 2, 0.5));

        JsonArray node = VideoAnalyzeStepExecutor.SegmentationTracksNode([track]);
        string json = node.ToJsonString();

        json.Should().Contain("g1").And.Contain("speedometer");
        json.Should().Contain("large");
        json.Should().NotContain("abc.mkv", "a mask key is a compile input, never model-visible");
        json.Should().NotContain(new string('a', 64));
        node[0]!.AsObject().Select(kv => kv.Key).Should().BeEquivalentTo(["id", "prompt", "src", "size", "continuity", "conf"]);
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    internal sealed class Harness
    {
        private readonly IPresignedUrlService? _presignService;

        public Harness(
            CountingClient provider,
            CountingPresign? presign = null,
            IOrganizationEntitlements? entitlement = null,
            bool omitPresigner = false,
            IVideoSegmentationClientFactory? clients = null,
            Microsoft.Extensions.Logging.ILogger<SegmentationTrackingService>? logger = null,
            IVideoSegmentationClient? replay = null)
        {
            Client = provider;
            Replay = replay;
            Presign = presign ?? new CountingPresign();
            _presignService = omitPresigner ? null : Presign;
            Store = new MemoryStore();
            Workspace = new SessionWorkspace();

            ServiceCollection services = new();
            services.AddSingleton(entitlement ?? new StubEntitlements(new OrganizationEntitlementSet { Tier = "creator" }));
            ServiceProvider = services.BuildServiceProvider();

            Resolver
                .Setup(r => r.ResolveVideoSegmentationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Provider);

            Service = new SegmentationTrackingService(
                Resolver.Object,
                Store,
                ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
                logger ?? NullLogger<SegmentationTrackingService>.Instance,
                clients ?? new Factory(this),
                _presignService);
        }

        public CountingClient Client { get; }

        /// <summary>A pre-built answer the vendor hands back verbatim, in place of <see cref="Client"/>.</summary>
        public IVideoSegmentationClient? Replay { get; }

        public Mock<IInferenceProviderResolver> Resolver { get; } = new();

        public ResolvedImageAnalysisProvider? Provider { get; set; } = new(
            Guid.NewGuid(),
            "fal",
            InferenceProviderKind.FalAi,
            InferenceProviderCapability.VideoSegmentation,
            "https://queue.fal.run",
            "fal-ai/sam-3-1/video-rle",
            "key",
            900);

        public CountingPresign Presign { get; }

        public SessionWorkspace Workspace { get; }

        public MemoryStore Store { get; }

        public ServiceProvider ServiceProvider { get; }

        public SegmentationTrackingService Service { get; }

        public async Task<SegmentationSourceOutcome> RunAsync(
            VideoAnalyzeStepConfig config, double durationSec = 0.25, double fps = 24,
            int width = 12, int height = 6, long sourceBytes = 0, bool allowSeeded = true)
        {
            // The content key hashes the SOURCE FILE, so the fake source must be a real file (its bytes
            // never leave this test: the store below replaces object storage). A large size is a sparse
            // file of zeros: only its LENGTH matters (the vendor-copy bound).
            string sourcePath = Path.GetTempFileName();
            if (sourceBytes > 0)
            {
                await using FileStream sparse = new(sourcePath, FileMode.Create, FileAccess.Write);
                sparse.SetLength(sourceBytes);
            }
            else
            {
                await File.WriteAllBytesAsync(sourcePath, "not really a video"u8.ToArray());
            }
            try
            {
                return await Service.TrackAsync(
                    new SegmentationSourceInput(
                        0, Project, "clip.mp4", sourcePath, durationSec, fps, width, height, Organization, allowSeeded),
                    config,
                    Workspace,
                    _ => Task.CompletedTask,
                    CancellationToken.None);
            }
            finally
            {
                File.Delete(sourcePath);
            }
        }

        private sealed class Factory(Harness harness) : IVideoSegmentationClientFactory
        {
            public IVideoSegmentationClient Get(
                ResolvedImageAnalysisProvider provider, VideoSegmentationFrameSize frameSize) =>
                harness.Replay ?? harness.Client;
        }
    }

    internal static VideoAnalyzeStepConfig SegmentationConfig(
        bool consent = true,
        int maxSeconds = 60,
        string prompt = "speedometer",
        bool extraTarget = false,
        ObjectMatchScope matchScope = ObjectMatchScope.AllInstances)
    {
        List<ObjectTrackTarget> targets = [new(prompt, Method: ObjectTrackMethod.Segmentation, MatchScope: matchScope)];
        if (extraTarget)
        {
            targets.Add(new ObjectTrackTarget("gauge needle", Method: ObjectTrackMethod.Segmentation));
        }

        return new VideoAnalyzeStepConfig(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput),
            TrackObjects: targets)
        with
        {
            AllowSourceMediaEgress = consent,
            MaxSegmentationSeconds = maxSeconds
        };
    }

    /// <summary>The vendor, counted. Six frames of a 12x6 clip; one 2x2 object block per frame.</summary>
    internal sealed class CountingClient : IVideoSegmentationClient
    {
        public int Calls { get; private set; }

        public int Width { get; init; } = 12;

        public int Height { get; init; } = 6;

        public Exception? Throw { get; init; }

        /// <summary>How many calls answer successfully before <see cref="Throw"/> (or a stand-in) is raised.</summary>
        public int SucceedTimes { get; init; } = int.MaxValue;

        /// <summary>How many leading frames answer with NO object at all — the synthetic head gap the takeover asks for.</summary>
        public int LeadingEmptyFrames { get; init; }

        public Task<VideoSegmentationResult> SegmentVideoAsync(
            VideoSegmentationRequest request, CancellationToken ct)
        {
            Calls++;
            if (Calls > SucceedTimes)
            {
                throw Throw ?? new InvalidOperationException("the segmentation model refused the request");
            }

            List<VideoSegmentationFrame> frames = [];
            for (int frame = 0; frame < 6; frame++)
            {
                bool empty = frame < LeadingEmptyFrames;
                // Row-major runs: one 2x2 block at (1,1) of a 12-wide frame.
                frames.Add(new VideoSegmentationFrame(
                    frame,
                    empty ? [] : [new VideoSegmentationObjectRle(1, "13 2 25 2")],
                    empty ? [] : [new VideoSegmentationObjectBox(1, 1d / 12, 1d / 6, 2d / 12, 2d / 6)],
                    empty ? [] : [0.9]));
            }

            return Task.FromResult(new VideoSegmentationResult(
                request.Model, Width, Height, frames, "falai:model", 0.01m, 6));
        }
    }

    /// <summary>A vendor that answers one pre-built result (a replay), counted like the others.</summary>
    internal sealed class ReplayClient(VideoSegmentationResult result) : IVideoSegmentationClient
    {
        public int Calls { get; private set; }

        public Task<VideoSegmentationResult> SegmentVideoAsync(
            VideoSegmentationRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    /// <summary>Every formatted log entry, so a test can assert what never appears there.</summary>
    internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    internal sealed class StubEntitlements(OrganizationEntitlementSet set) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(set);
    }

    internal sealed class CountingPresign : IPresignedUrlService
    {
        public int Calls { get; private set; }

        public List<string> Keys { get; } = [];

        public Task<string> CreateGetAsync(Guid projectId, string key, TimeSpan ttl)
        {
            Calls++;
            Keys.Add(key);
            ttl.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
            return Task.FromResult($"https://store.test/{projectId:D}/{key}?sig=x");
        }

        public Task<PresignedPut> CreatePutAsync(Guid jobId, int n, string contentType, TimeSpan ttl) =>
            throw new NotSupportedException();

        public Task<PresignedPut> CreateBrowserUploadPutAsync(Guid projectId, string key, string contentType, TimeSpan ttl) =>
            throw new NotSupportedException();

        public Task<long> PromoteAsync(string stagingKey, string finalKey, long expectedMaxBytes, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>The real key derivation, an in-memory table instead of the object store.</summary>
    internal sealed class MemoryStore : ISegmentationMaskStore
    {
        private readonly SegmentationMaskStore _keys = new(null!, NullLogger<SegmentationMaskStore>.Instance);

        public Dictionary<string, SegmentationMaskEntry> Entries { get; } = [];

        public HashSet<string> VendorCopies { get; } = [];

        public bool ThrowOnVendorCopyLookup { get; set; }

        public Task<bool> VendorCopyExistsAsync(Guid projectId, string sourceHash, CancellationToken ct) =>
            ThrowOnVendorCopyLookup
                ? throw new InvalidOperationException("storage unavailable")
                : Task.FromResult(VendorCopies.Contains(sourceHash));

        public Task PutVendorCopyAsync(
            Guid projectId, IMediaWorkspace workspace, string virtualPath, string sourceHash, CancellationToken ct)
        {
            VendorCopies.Add(sourceHash);
            return Task.CompletedTask;
        }

        /// <summary>Every mask key the service derived, in order: shows which source hash the masks are keyed on.</summary>
        public List<SegmentationMaskKey> KeysSeen { get; } = [];

        public string ContentKey(SegmentationMaskKey key)
        {
            KeysSeen.Add(key);
            return _keys.ContentKey(key);
        }

        public Task<SegmentationMaskEntry?> TryGetAsync(
            Guid projectId, string contentKey, IMediaWorkspace workspace, CancellationToken ct) =>
            Task.FromResult(Entries.TryGetValue(contentKey, out SegmentationMaskEntry? entry) ? entry : null);

        public Task<SegmentationMaskEntry> PutAsync(
            Guid projectId,
            IMediaWorkspace workspace,
            string localMaskPath,
            string maskVirtualPath,
            SegmentationMaskEntry entry,
            CancellationToken ct)
        {
            SegmentationMaskEntry stored = entry with
            {
                MaskStorageKey = SegmentationMaskStore.MaskStorageKeyFor(projectId, entry.ContentKey),
                MaskContentHash = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.ContentKey + entry.FrameCount)))
            };
            Entries[entry.ContentKey] = stored;
            return Task.FromResult(stored);
        }
    }

    internal sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public int PbmFrames { get; private set; }

        public List<string[]> FfmpegCalls { get; } = [];

        public VideoToolResult? FfmpegResult { get; set; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) => Task.CompletedTask;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
        {
            FfmpegCalls.Add([.. argv]);
            return Task.FromResult(FfmpegResult ?? new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            RunFfmpegAsync(argv, timeout, ct);

        /// <summary>Canned ffprobe answers, one per call (empty stdout once drained).</summary>
        public Queue<string> ProbeOutputs { get; } = new();

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new VideoToolResult(0, ProbeOutputs.Count > 0 ? ProbeOutputs.Dequeue() : PlainProbe, string.Empty, false));

        private const string PlainProbe = """{"streams":[{"codec_type":"video","width":12,"height":6}]}""";

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            if (virtualPath.EndsWith(".pbm", StringComparison.Ordinal)) PbmFrames++;
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct) =>
            WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException(virtualPath);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) => ReadAsync(virtualPath, ct);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? new MediaWorkspaceStat(true, bytes.Length)
                : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);
    }
}
