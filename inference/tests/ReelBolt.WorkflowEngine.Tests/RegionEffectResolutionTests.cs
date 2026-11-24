using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Fail-closed resolution of <see cref="VideoCompileStepConfig.RegionEffects"/> (plan §5): a target
/// must name exactly one segmentation track the analyze step actually looked for, and the mask
/// bytes must be the ones the artifact recorded. Every refusal is asserted by code AND by the
/// specifics its message has to carry — "nothing matched" is never allowed to look like "we did not
/// know".
/// </summary>
public class RegionEffectResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "region-effect-tests", Guid.NewGuid().ToString("N"));

    public RegionEffectResolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static readonly VideoAnalysisMedia SourceMedia = new(10, 24, 1, 640, 360);

    // ---------------------------------------------------------------------------------------
    // Resolution refusals — no mask is ever downloaded on these paths.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task No_segmentation_tracks_refuses_and_says_what_to_change()
    {
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(tracks: Array.Empty<VideoSegmentationTrack>()),
            Span(0, 10));

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("\"speedometer\"");
        resolution.RefusalMessage.Should().Contain("no segmentation tracks");
        resolution.RefusalMessage.Should().Contain("Method to Segmentation");
        resolution.RefusalLogMessage.Should().Contain("no segmentation tracks").And.NotContain("speedometer");
        resolution.Effects.Should().BeEmpty();
    }

    [Fact]
    public async Task A_target_that_matches_nothing_names_the_tracks_that_do_exist()
    {
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(tracks: new[] { Track("g0", "cat") }, prompts: new[] { "cat" }),
            Span(0, 10));

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("\"speedometer\"");
        resolution.RefusalMessage.Should().Contain("\"cat\" (g0)");
        // The log sentence names the entry and the track ids, never the customer's prompts.
        resolution.RefusalLogMessage.Should().Contain("g0").And.NotContain("cat").And.NotContain("speedometer");
    }

    [Fact]
    public async Task Two_tracks_sharing_a_prompt_resolve_to_one_effect_painting_through_both_masks()
    {
        // D-23: a prompt names a SET of tracks — one per instance the prompt matched — so a target
        // that matches by prompt resolves to every one of them, not to an ambiguity refusal. (The
        // ticket that wrote the old refusal is superseded; EFFECT_TARGET_AMBIGUOUS is no longer
        // raised anywhere.)
        string firstMask = WriteMask(seed: 31);
        string secondMask = WriteMask(seed: 32);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(
                tracks: new[]
                {
                    Track("g0", "cat", hash: HashOf(firstMask)),
                    Track("g1", "cat", startSec: 3, endSec: 4, hash: HashOf(secondMask))
                },
                prompts: new[] { "cat" }),
            Span(0, 10),
            workspace: WorkspaceFor(firstMask, secondMask));

        resolution.RefusalCode.Should().BeNull();
        resolution.Masks.Should().HaveCount(2, "every matched track is acquired, not just the first");
        resolution.Effects.Should().ContainSingle("one target resolves to ONE effect");
        resolution.Effects[0].MaskIndexes.Should().Equal(new[] { 0, 1 }, "the effect paints through both tracks' masks");

        JsonObject entry = resolution.Node["entries"]!.AsArray()[0]!.AsObject();
        entry["trackId"]!.GetValue<string>().Should().Be("g0", "the FIRST matched track stays the entry's trackId");
        entry["prompt"]!.GetValue<string>().Should().Be("cat");
        entry["trackIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("g0", "g1");
        entry["maskContentHashes"]!.AsArray().Should().HaveCount(2);
        resolution.Node["coverageGaps"]!.AsArray().Should().HaveCount(2, "one gap entry per resolved mask");
    }

    [Fact]
    public async Task A_track_whose_prompt_the_analyze_step_never_looked_for_refuses()
    {
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(tracks: new[] { Track("g0", "speedometer") }, prompts: new[] { "cat" }),
            Span(0, 10));

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("never looked for the prompt");
        resolution.RefusalMessage.Should().Contain("\"speedometer\"");
        resolution.RefusalMessage.Should().Contain("\"cat\"");
        // Neither prompt reaches the log sentence.
        resolution.RefusalLogMessage.Should().Contain("g0").And.NotContain("speedometer").And.NotContain("cat");
    }

    [Fact]
    public async Task A_track_that_lies_entirely_in_a_cut_refuses()
    {
        // The track covers 1-2 s of its source, but only 5-10 s is kept.
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(tracks: new[] { Track("g0", "speedometer") }, prompts: new[] { "speedometer" }),
            Span(5, 10));

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("survives into the cut");
        // The target names the customer's prompt here; the LOG sentence carries the track id only (F-1).
        resolution.RefusalLogMessage.Should().Contain("g0").And.NotContain("speedometer");
    }

    [Fact]
    public async Task A_mask_empty_on_every_frame_refuses_before_it_is_downloaded()
    {
        // The no-op the takeover shipped as applied: a mask empty on all frames. The mean coverage is 0
        // exactly when every frame is empty, so that is the refusal trigger — and it fires BEFORE the
        // download, so a compile that cannot render never pays for the bytes.
        Mock<IMediaWorkspace> workspace = new();
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(
                tracks: new[] { Track("g0", "speedometer", coverage: 0, frameCount: 24, emptyFrames: 24) },
                prompts: new[] { "speedometer" }),
            Span(0, 10),
            workspace: workspace);

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_EMPTY");
        resolution.RefusalMessage.Should().Contain("\"speedometer\"");
        resolution.RefusalMessage.Should().Contain("24 frames");
        resolution.RefusalMessage.Should().Contain("would do nothing");
        // The step LOG is a different surface: the prompt describes the private subject of the customer's
        // footage and never reaches it (security finding F-1). The track id is the correlation handle.
        resolution.RefusalLogMessage.Should().Contain("g0").And.NotContain("speedometer");
        resolution.Effects.Should().BeEmpty();
        workspace.Verify(
            w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an all-empty mask is refused before it is downloaded");
    }

    [Fact]
    public async Task A_resolved_masks_coverage_gap_entry_carries_the_first_and_last_found_frame()
    {
        string maskPath = WriteMask(seed: 21);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(
                tracks: new[]
                {
                    Track("g0", "speedometer", hash: HashOf(maskPath), coverage: 0.05,
                        frameCount: 24, emptyFrames: 4, longestGapFrames: 2,
                        firstNonEmptyFrame: 3, lastNonEmptyFrame: 20)
                },
                prompts: new[] { "speedometer" }),
            Span(0, 10),
            workspace: WorkspaceFor(maskPath));

        resolution.RefusalCode.Should().BeNull();
        JsonObject gap = resolution.Node["coverageGaps"]!.AsArray()[0]!.AsObject();
        gap["firstNonEmptyFrame"]!.GetValue<int>().Should().Be(3);
        gap["lastNonEmptyFrame"]!.GetValue<int>().Should().Be(20);
        gap["leadingGapFrames"]!.GetValue<int>().Should().Be(3);
        gap["trailingGapFrames"]!.GetValue<int>().Should().Be(3);
        // The existing keys keep their names and meaning.
        gap["trackId"]!.GetValue<string>().Should().Be("g0");
        gap["emptyFrames"]!.GetValue<int>().Should().Be(4);
        gap["longestGapFrames"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void A_compile_warning_names_where_the_effect_is_actually_missing()
    {
        // A leading gap: the sentence names the head. An INTERNAL gap (no leading gap at all) must not
        // claim the head — "missing from the first 0 s" is a sentence that means nothing.
        List<JsonObject> leading = VideoCompileStepExecutor.BuildRegionEffectWarnings(
        [
            Mask("g0", "speedometer", emptyFrames: 211, longestGapFrames: 211,
                firstNonEmptyFrame: 211, lastNonEmptyFrame: 826, frameCount: 827, fps: 59.94)
        ]).ToList();
        leading.Should().ContainSingle();
        leading[0]["code"]!.GetValue<string>().Should().Be("segmentation_gap");
        leading[0]["message"]!.GetValue<string>().Should().Contain("the first is frame 211")
            .And.Contain("first 3.52 s");

        List<JsonObject> internal_ = VideoCompileStepExecutor.BuildRegionEffectWarnings(
        [
            Mask("g0", "speedometer", emptyFrames: 90, longestGapFrames: 90,
                firstNonEmptyFrame: 0, lastNonEmptyFrame: 300, frameCount: 400, fps: 30)
        ]).ToList();
        internal_.Should().ContainSingle();
        internal_[0]["message"]!.GetValue<string>().Should().Contain("the longest stretch with no object is 90 frames")
            .And.NotContain("from the first");

        // A clean mask contributes nothing at all, so a compile without a gap keeps its exact shape.
        VideoCompileStepExecutor.BuildRegionEffectWarnings(
        [
            Mask("g0", "speedometer", emptyFrames: 2, longestGapFrames: 2,
                firstNonEmptyFrame: 2, lastNonEmptyFrame: 140, frameCount: 141, fps: 59.94)
        ]).Should().BeEmpty();
    }

    private static VideoCompileStepExecutor.ResolvedRegionMask Mask(
        string id, string prompt, int emptyFrames, int longestGapFrames,
        int? firstNonEmptyFrame, int? lastNonEmptyFrame, int frameCount, double fps) =>
        new(id, prompt, 0, null, [], "mask.mkv", "key", "hash", 0.05,
            emptyFrames, longestGapFrames, firstNonEmptyFrame, lastNonEmptyFrame, frameCount, fps);

    [Fact]
    public async Task A_three_instance_effect_combines_three_masks_and_names_all_of_them()
    {
        // D-23: a prompt names a SET of tracks. Three instances must paint through ONE union and the EDL
        // must name all three, not just the first.
        string m0 = WriteMask(seed: 41), m1 = WriteMask(seed: 42), m2 = WriteMask(seed: 43);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(
                tracks: new[]
                {
                    Track("g0", "cat", hash: HashOf(m0), startSec: 0, endSec: 10),
                    Track("g1", "cat", hash: HashOf(m1), startSec: 1, endSec: 9),
                    Track("g2", "cat", hash: HashOf(m2), startSec: 2, endSec: 8)
                },
                prompts: new[] { "cat" }),
            Span(0, 10),
            workspace: WorkspaceFor(m0, m1, m2));

        resolution.RefusalCode.Should().BeNull();
        resolution.Masks.Should().HaveCount(3);
        resolution.Effects.Should().ContainSingle();
        resolution.Effects[0].MaskIndexes.Should().Equal(0, 1, 2);

        string filter = VideoCompileStepExecutor.BuildRegionEffectsFilter(
            resolution.Masks, resolution.Effects, 1, "[vfx0]", "[vfx1]", 640, 360, "24/1", 10.0);
        filter.Should().Contain(
            "[rmk0][rmk1]blend=all_mode=lighten[rmu0p1]",
            "ffmpeg's blend takes exactly two inputs, so three masks are folded pairwise");
        filter.Should().Contain("[rmu0p1][rmk2]blend=all_mode=lighten[rmu0]");
        filter.Should().NotContain("inputs=", "blend has no `inputs` option; that graph would not parse");
        filter.Split("all_mode=lighten").Should().HaveCount(3, "two pairwise blends for three inputs, one union per effect");

        JsonObject entry = resolution.Node["entries"]!.AsArray()[0]!.AsObject();
        entry["trackIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("g0", "g1", "g2");
        entry["instances"]!.AsArray().Select(n => n!["trackId"]!.GetValue<string>())
            .Should().Equal(["g0", "g1", "g2"], "every instance the effect covers is named");
        entry["instances"]!.AsArray().Select(n => n!["maskContentHash"]!.GetValue<string>())
            .Should().Equal(HashOf(m0), HashOf(m1), HashOf(m2));
        entry["instances"]![0]!["outputWindows"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_prompt_matching_more_tracks_than_the_cap_refuses_before_any_download()
    {
        // The match set is vendor-driven, so it is bounded BEFORE a single mask is staged.
        List<VideoSegmentationTrack> tracks = [];
        for (int i = 0; i < VideoCompileStepExecutor.MaxTracksPerEffect + 1; i++)
        {
            tracks.Add(Track($"g{i}", "cat", hash: "abc123"));
        }

        Mock<IMediaWorkspace> workspace = new();
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")), Artifact(tracks: tracks, prompts: new[] { "cat" }), Span(0, 10),
            workspace: workspace);

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_TOO_BROAD");
        resolution.RefusalMessage.Should().Contain($"{VideoCompileStepExecutor.MaxTracksPerEffect}");
        resolution.RefusalLogMessage.Should().NotContain("cat");
        workspace.Verify(
            w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the ceiling fires before any mask is downloaded");
    }

    [Fact]
    public async Task A_mask_whose_bytes_do_not_match_the_recorded_hash_refuses()
    {
        string maskPath = WriteMask(seed: 7);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(
                tracks: new[] { Track("g0", "speedometer", hash: "deadbeef") },
                prompts: new[] { "speedometer" }),
            Span(0, 10),
            workspace: WorkspaceFor(maskPath));

        resolution.RefusalCode.Should().Be("SEGMENTATION_MASK_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("deadbeef");
        resolution.RefusalMessage.Should().Contain("may belong to another clip");
        // The prompt-bearing sentence is the customer's; the LOG sentence names the track and the key.
        resolution.RefusalLogMessage.Should().Contain("Track g0").And.NotContain("speedometer");
    }

    [Fact]
    public async Task A_mask_at_the_wrong_frame_size_refuses()
    {
        string maskPath = WriteMask(seed: 9);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("speedometer")),
            Artifact(
                tracks: new[] { Track("g0", "speedometer", hash: HashOf(maskPath)) },
                prompts: new[] { "speedometer" }),
            Span(0, 10),
            workspace: WorkspaceFor(maskPath),
            probe: ProbeFor(width: 320, height: 180, fpsNum: 24, durationSec: 1.0));

        resolution.RefusalCode.Should().Be("SEGMENTATION_MASK_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("320x180");
        resolution.RefusalMessage.Should().Contain("640x360");
        resolution.RefusalLogMessage.Should().Contain("Track g0").And.NotContain("speedometer");
    }

    // ---------------------------------------------------------------------------------------
    // The happy path: a verified mask, its windows, and the EDL node.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_mask_resolves_with_its_output_windows_and_an_edl_node()
    {
        string maskPath = WriteMask(seed: 11);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("g0", RegionEffect.Blur, invert: true, strength: 1.5, featherPx: 6, opacity: 0.75)),
            Artifact(
                tracks: new[] { Track("g0", "speedometer", hash: HashOf(maskPath)) },
                prompts: new[] { "speedometer" }),
            Span(0, 10),
            workspace: WorkspaceFor(maskPath));

        resolution.RefusalCode.Should().BeNull();
        resolution.Masks.Should().ContainSingle();
        resolution.Masks[0].TrackId.Should().Be("g0");
        resolution.Masks[0].ContentHash.Should().Be(HashOf(maskPath));
        resolution.Masks[0].Windows.Should().ContainSingle();
        resolution.Masks[0].Windows[0].OutputStartSec.Should().Be(1.0);
        resolution.Masks[0].Windows[0].OutputEndSec.Should().Be(2.0);

        resolution.Effects.Should().ContainSingle();
        resolution.Effects[0].MaskIndexes.Should().Equal(0);
        resolution.Effects[0].Effect.Should().Be(RegionEffect.Blur);
        resolution.Effects[0].Invert.Should().BeTrue();
        resolution.Effects[0].Strength.Should().Be(1.5);
        resolution.Effects[0].FeatherPx.Should().Be(6);

        string node = resolution.Node.ToJsonString();
        node.Should().Contain("\"applied\":true");
        node.Should().Contain("\"effect\":\"Blur\"");
        node.Should().Contain("\"invert\":true");
        node.Should().Contain("\"maskContentHash\"");
        node.Should().Contain("\"emptyFrames\":0");

        // The prompt is DATA in the EDL; it must never reach a filter graph.
        string filter = VideoCompileStepExecutor.BuildRegionEffectsFilter(
            resolution.Masks, resolution.Effects, 1, "[vfx0]", "[vfx1]", 640, 360, "24/1", 10.0);
        filter.Should().NotContain("speedometer", because: "the prompt never reaches an argv");
        filter.Should().Contain("[1:v]format=yuv420p");
        filter.Should().Contain("[rmk0]");
        filter.Should().Contain("gblur=sigma=");
        filter.Should().EndWith("[vfx1]");
    }

    /// <summary>
    /// The gate the union change must not move: a one-mask effect's graph is byte-for-byte the graph
    /// the pre-union builder produced. The expected string was captured from the code before the
    /// change; if any of it moves, the change is wrong.
    /// </summary>
    [Fact]
    public async Task A_one_mask_effect_graph_is_byte_identical_to_the_pre_union_graph()
    {
        string maskPath = WriteMask(seed: 11);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("g0", RegionEffect.Blur, invert: true, strength: 1.5, featherPx: 6, opacity: 0.75)),
            Artifact(
                tracks: new[] { Track("g0", "speedometer", hash: HashOf(maskPath)) },
                prompts: new[] { "speedometer" }),
            Span(0, 10),
            workspace: WorkspaceFor(maskPath));

        string filter = VideoCompileStepExecutor.BuildRegionEffectsFilter(
            resolution.Masks, resolution.Effects, 1, "[vfx0]", "[vfx1]", 640, 360, "24/1", 10.0);

        filter.Should().Be(
            "[1:v]format=yuv420p,fps=24/1,setsar=1,setpts=PTS-STARTPTS[rm0raw];" +
            "color=c=black:s=640x360:r=24/1:d=10,format=yuv420p[rm0win0];" +
            "[rm0raw]trim=start=1:end=2,setpts=PTS-STARTPTS+1/TB[rm0seg0];" +
            "[rm0win0][rm0seg0]overlay=x=0:y=0:eof_action=pass:repeatlast=0:format=yuv420[rm0win1];" +
            "[rm0win1]format=gray[rmk0];" +
            "[rmk0]negate,gblur=sigma=6,lut=y='val*0.75'[rmt0e0];" +
            "[vfx0]split=2[ree0b][ree0e];[ree0e]gblur=sigma=8.1,format=yuva420p[ree0a];" +
            "[ree0a][rmt0e0]alphamerge[ree0f];[ree0b][ree0f]overlay=x=0:y=0:format=yuv420[vfx1]");
    }

    /// <summary>
    /// An effect with more than one mask paints through the UNION of those masks: both mask streams
    /// are emitted, exactly one <c>blend=all_mode=lighten</c> combines them into a single union label,
    /// and that union label feeds the effect's own helper chain — so a "everything except the object"
    /// effect is applied once, not once per instance.
    /// </summary>
    [Fact]
    public async Task A_two_mask_effect_combines_its_masks_once_before_the_effect_chain()
    {
        string firstMask = WriteMask(seed: 41);
        string secondMask = WriteMask(seed: 42);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat", RegionEffect.Spotlight)),
            Artifact(
                tracks: new[]
                {
                    Track("g0", "cat", hash: HashOf(firstMask)),
                    Track("g1", "cat", startSec: 3, endSec: 4, hash: HashOf(secondMask))
                },
                prompts: new[] { "cat" }),
            Span(0, 10),
            workspace: WorkspaceFor(firstMask, secondMask));

        resolution.RefusalCode.Should().BeNull();
        string filter = VideoCompileStepExecutor.BuildRegionEffectsFilter(
            resolution.Masks, resolution.Effects, 1, "[vfx0]", "[vfx1]", 640, 360, "24/1", 10.0);

        filter.Should().Contain("[rmk0]");
        filter.Should().Contain("[rmk1]");
        filter.Split("blend=all_mode=lighten").Length.Should().Be(2, "exactly ONE union blend, built once");
        filter.Should().Contain("[rmk0][rmk1]blend=all_mode=lighten[rmu0]");
        filter.Should().NotContain("inputs=", "blend has no `inputs` option; that graph would not parse");
        // The union label feeds the effect's existing helper chain, whose output the effect consumes.
        filter.Should().Contain("[rmu0]null[rmu0m]");
        filter.Should().Contain("[rmu0m]negate[ree0mX]");
        filter.Should().EndWith("[vfx1]");
        filter.Should().NotContain("cat");
    }

    /// <summary>
    /// Per-instance refusals stay per-instance: one empty instance must not be hidden by a good one,
    /// so the compile refuses (naming the empty track) rather than painting only the good instance.
    /// </summary>
    [Fact]
    public async Task An_empty_instance_is_not_hidden_by_a_good_one()
    {
        string goodMask = WriteMask(seed: 51);
        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(
                tracks: new[]
                {
                    Track("g0", "cat", hash: HashOf(goodMask)),
                    Track("g1", "cat", startSec: 3, endSec: 4, coverage: 0, emptyFrames: 24)
                },
                prompts: new[] { "cat" }),
            Span(0, 10),
            workspace: WorkspaceFor(goodMask));

        resolution.RefusalCode.Should().Be("EFFECT_TARGET_EMPTY");
        resolution.RefusalMessage.Should().Contain("\"g1\"", "the refusal names the EMPTY instance");
        resolution.RefusalMessage.Should().Contain("24 frames");
        resolution.RefusalLogMessage.Should().Contain("g1").And.NotContain("cat");
        resolution.Effects.Should().BeEmpty();
    }

    [Fact]
    public void One_track_surviving_two_kept_spans_produces_two_windows_in_output_time()
    {
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 3, 0, 3, 0, 72, SourceIndex: 0),
            new(6, 9, 6, 9, 144, 216, SourceIndex: 0)
        };
        var seams = new List<SeamPlan> { new(0, SeamTreatment.HardCut, 0, 0, "fade", 0, "hard cut") };
        OutputTimeline timeline = OutputTimeline.Build(spans, seams);
        VideoSegmentationTrack track = Track("g0", "speedometer", startSec: 1, endSec: 7);

        IReadOnlyList<RegionMaskWindow> windows =
            VideoCompileStepExecutor.MapTrackWindows(timeline, spans, track);

        windows.Should().HaveCount(2);
        windows[0].Should().Be(new RegionMaskWindow(1, 3, 1, 3));
        // The second window starts at output 3 s (the end of the first kept span), not 6.
        windows[1].Should().Be(new RegionMaskWindow(6, 7, 3, 4));
    }

    // ---------------------------------------------------------------------------------------
    // Config refusals that fire before any resolution work.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Region_effects_on_a_stream_copy_compile_are_refused()
    {
        (string Code, string Message)? refusal =
            VideoCompileStepExecutor.ValidateRegionEffects(Config(Effect("speedometer")) with { Mode = VideoCompileMode.StreamCopy });

        refusal.Should().NotBeNull();
        refusal!.Value.Code.Should().Be("EFFECT_REQUIRES_REENCODE");
        refusal.Value.Message.Should().Contain("Mode=Reencode");
    }

    [Fact]
    public void More_than_the_effect_cap_is_refused_rather_than_dropped()
    {
        var entries = Enumerable.Range(0, RegionEffectRules.MaxEffects + 1)
            .Select(i => new VideoRegionEffect($"g{i}", RegionEffect.Blur))
            .ToList();

        (string Code, string Message)? refusal =
            VideoCompileStepExecutor.ValidateRegionEffects(Config(entries.ToArray()));

        refusal.Should().NotBeNull();
        refusal!.Value.Code.Should().Be("EFFECT_LIMIT_EXCEEDED");
        refusal.Value.Message.Should().Contain($"{RegionEffectRules.MaxEffects}");
    }

    [Fact]
    public void Two_entries_on_a_reencode_compile_are_usable()
    {
        VideoCompileStepExecutor.ValidateRegionEffects(Config(Effect("g0"), Effect("g1"))).Should().BeNull();
        VideoCompileStepExecutor.ValidateRegionEffects(
            new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)).Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------

    private static VideoCompileStepConfig Config(params VideoRegionEffect[] effects) =>
        new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1,
            Mode: VideoCompileMode.Reencode, RegionEffects: effects);

    private static VideoRegionEffect Effect(
        string target, RegionEffect effect = RegionEffect.Blur, bool invert = false,
        double strength = 1.0, double featherPx = 0, double opacity = 1.0) =>
        new(target, effect, invert, strength, featherPx, opacity);

    private static VideoSegmentationTrack Track(
        string id, string prompt, double startSec = 1, double endSec = 2, string hash = "abc123",
        double coverage = 0.05, int frameCount = 24, int emptyFrames = 0, int longestGapFrames = 0,
        int? firstNonEmptyFrame = null, int? lastNonEmptyFrame = null) =>
        new(id, 0, prompt, startSec, endSec, FrameCount: frameCount, Fps: 24.0, Coverage: coverage, Confidence: 0.9,
            MaskStorageKey: $"projects/p/agentFiles/segmentation/{id}.mkv",
            MaskContentHash: hash, Model: "fal-ai/sam-3-1/video-rle",
            Facts: new VideoSegmentationFacts(
                emptyFrames, longestGapFrames, 0.5, false, firstNonEmptyFrame, lastNonEmptyFrame));

    private static VideoAnalysisArtifact Artifact(
        IReadOnlyList<VideoSegmentationTrack> tracks, IReadOnlyList<string>? prompts = null) =>
        new(
            Version: 1,
            Media: SourceMedia,
            Shots: Array.Empty<VideoAnalysisShot>(),
            SilenceSpans: Array.Empty<VideoAnalysisSilenceSpan>(),
            Segments: Array.Empty<VideoAnalysisSegment>(),
            Words: Array.Empty<VideoAnalysisWord>(),
            OfferedIds: Array.Empty<string>(),
            Provenance: new VideoAnalysisProvenance(
                VideoTranscriptionMode.Off, false, false, SegmentationPrompts: prompts),
            Sources: new[] { new VideoAnalysisSourceInfo(0, "projects/p/agentFiles/source.mp4", SourceMedia) },
            SegmentationTracks: tracks);

    private static List<VideoCompileStepExecutor.ResolvedSpan> Span(double start, double end) =>
        new() { new VideoCompileStepExecutor.ResolvedSpan(start, end, start, end, 0, 240, SourceIndex: 0) };

    private string WriteMask(int seed)
    {
        string path = Path.Combine(_root, $"mask-{seed}.mkv");
        var bytes = new byte[512];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string HashOf(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Mock<IMediaWorkspace> WorkspaceFor(params string[] maskPaths)
    {
        var workspace = new Mock<IMediaWorkspace>();
        int materialized = 0;
        workspace
            .Setup(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Masks are acquired in the resolution's own order (artifact order), so the Nth materialize
        // hands back the Nth mask — one path is served to every call.
        workspace
            .Setup(w => w.MaterializeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => maskPaths[Math.Min(materialized++, maskPaths.Length - 1)]);
        return workspace;
    }

    // #1206: a remote (runner) session's MaterializeAsync hands back the session-RELATIVE name, which is not
    // a file on the engine host. The mask is read through the seam and hashed there.
    private static Mock<IMediaWorkspace> RemoteWorkspaceFor(byte[] maskBytes)
    {
        var workspace = new Mock<IMediaWorkspace>();
        workspace.SetupGet(w => w.IsRemoteSession).Returns(true);
        workspace
            .Setup(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        workspace
            .Setup(w => w.MaterializeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string virtualPath, CancellationToken _) => virtualPath);
        workspace
            .Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(maskBytes);
        return workspace;
    }

    [Fact]
    public async Task A_remote_session_mask_is_hashed_through_the_workspace_and_passes()
    {
        byte[] bytes = File.ReadAllBytes(WriteMask(seed: 51));
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Mock<IMediaWorkspace> workspace = RemoteWorkspaceFor(bytes);

        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(tracks: new[] { Track("g0", "cat", hash: hash) }, prompts: new[] { "cat" }),
            Span(0, 10), workspace: workspace);

        resolution.RefusalCode.Should().BeNull();
        workspace.Verify(w => w.ReadAsync("segmentation-g0.mkv", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_remote_session_mask_with_the_wrong_hash_still_refuses()
    {
        byte[] bytes = File.ReadAllBytes(WriteMask(seed: 52));

        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(tracks: new[] { Track("g0", "cat", hash: "deadbeef") }, prompts: new[] { "cat" }),
            Span(0, 10), workspace: RemoteWorkspaceFor(bytes));

        resolution.RefusalCode.Should().Be("SEGMENTATION_MASK_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("deadbeef");
    }

    [Fact]
    public async Task A_remote_session_mask_over_the_read_cap_refuses_fail_closed()
    {
        Mock<IMediaWorkspace> workspace = RemoteWorkspaceFor(Array.Empty<byte>());
        workspace
            .Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MediaWorkspaceFileTooLargeException("segmentation-g0.mkv", long.MaxValue, 1));

        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(tracks: new[] { Track("g0", "cat", hash: "deadbeef") }, prompts: new[] { "cat" }),
            Span(0, 10), workspace: workspace);

        resolution.RefusalCode.Should().Be("SEGMENTATION_MASK_UNVERIFIED");
        resolution.RefusalLogMessage.Should().Contain("Track g0").And.NotContain("cat\"");
    }

    private static Mock<IMediaProbe> ProbeFor(int width, int height, int fpsNum, double durationSec)
    {
        var probe = new Mock<IMediaProbe>();
        probe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(durationSec, fpsNum, 1, width, height, "ffv1", null, null, "gray"));
        return probe;
    }

    private async Task<VideoCompileStepExecutor.RegionEffectResolution> Resolve(
        VideoCompileStepConfig config,
        VideoAnalysisArtifact artifact,
        List<VideoCompileStepExecutor.ResolvedSpan> spans,
        Mock<IMediaWorkspace>? workspace = null,
        Mock<IMediaProbe>? probe = null,
        IReadOnlyDictionary<int, (int Width, int Height)>? decodedSizes = null)
    {
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(_root, "scratch"),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });
        var executor = new VideoCompileStepExecutor(
            new Mock<IVideoToolRunner>().Object,
            (probe ?? ProbeFor(640, 360, 24, 1.0)).Object,
            new Mock<IProjectFileWorkspace>().Object,
            new Mock<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>().Object,
            options,
            NullLogger<VideoCompileStepExecutor>.Instance);

        OutputTimeline timeline = OutputTimeline.Build(spans, Array.Empty<SeamPlan>());
        return await executor.ResolveRegionEffectsAsync(
            config, artifact, timeline, spans, (workspace ?? new Mock<IMediaWorkspace>()).Object,
            SourceMedia, sourcesNormalizedToCanvas: false, coverFit: false, cropFocus: null,
            CancellationToken.None, decodedSizes);
    }

    // #772 M2: an analysis made before the rotation fix records the CODED size; the compile decodes the
    // autorotated frame, so the stored mask would land on the wrong pixels.
    [Fact]
    public async Task A_stale_coded_size_artifact_on_a_rotated_source_is_refused_before_any_download()
    {
        string mask = WriteMask(seed: 41);
        Mock<IMediaWorkspace> workspace = WorkspaceFor(mask);

        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(tracks: new[] { Track("g0", "cat", hash: HashOf(mask)) }, prompts: new[] { "cat" }),
            Span(0, 10), workspace: workspace,
            decodedSizes: new Dictionary<int, (int, int)> { [0] = (360, 640) });

        resolution.RefusalCode.Should().Be("SEGMENTATION_MASK_UNVERIFIED");
        resolution.RefusalMessage.Should().Contain("640x360").And.Contain("360x640").And.Contain("Re-run the analyze step")
            .And.NotContain("projects/p");
        workspace.Verify(w => w.StageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_fresh_artifact_whose_size_equals_the_decoded_size_passes()
    {
        string mask = WriteMask(seed: 42);

        VideoCompileStepExecutor.RegionEffectResolution resolution = await Resolve(
            Config(Effect("cat")),
            Artifact(tracks: new[] { Track("g0", "cat", hash: HashOf(mask)) }, prompts: new[] { "cat" }),
            Span(0, 10), workspace: WorkspaceFor(mask),
            decodedSizes: new Dictionary<int, (int, int)> { [0] = (640, 360) });

        resolution.RefusalCode.Should().BeNull();
    }

    [Fact]
    public void The_stale_check_covers_censor_and_is_silent_without_decoded_sizes_or_on_a_match()
    {
        VideoAnalysisArtifact artifact = Artifact(Array.Empty<VideoSegmentationTrack>());

        VideoCompileStepExecutor.StaleGeometryMessage(artifact, null).Should().BeNull();
        VideoCompileStepExecutor.StaleGeometryMessage(artifact, new Dictionary<int, (int, int)> { [0] = (640, 360) }).Should().BeNull();
        VideoCompileStepExecutor.StaleGeometryMessage(artifact, new Dictionary<int, (int, int)> { [0] = (360, 640) })
            .Should().Contain("Re-run the analyze step").And.NotContain("projects/p");
    }
}
