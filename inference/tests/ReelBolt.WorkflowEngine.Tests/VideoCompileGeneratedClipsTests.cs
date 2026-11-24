using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Wave-A acceptance tests for the generated-clip PLACEMENT TABLE in
/// <see cref="VideoCompileStepExecutor"/> (T-302): manifest resolution, the per-clip degrade
/// vocabulary, the Cutaway picture-only overlay, the ColdOpen/EndCard concat segments, the
/// <c>MaxGeneratedClips</c> cap, and the load-bearing byte-identity guarantee that
/// <c>EnableGeneratedClips = false</c> (the default) changes nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Self-contained on purpose: this file copies the small harness pieces it needs from
/// <see cref="VideoCompileStepExecutorTests"/> (artifact/decision/context construction, the
/// ffmpeg-argv capture, the media-probe stub) rather than editing that file, which this ticket's
/// <c>files:</c> list does not include. One addition is genuinely required and is why the harness
/// could not simply be reused verbatim: <see cref="GeneratedClipNormalizer"/> probes each
/// downloaded clip through <see cref="IVideoToolRunner.RunFfprobeAsync"/>, which the older harness
/// never stubs.
/// </para>
/// <para>
/// No real ffmpeg runs and no network is touched: every ffmpeg/ffprobe call goes through a Moq
/// <see cref="IVideoToolRunner"/>, every media probe through a Moq <see cref="IMediaProbe"/>, and
/// every byte the executor "downloads" comes from an in-test string.
/// </para>
/// </remarks>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileGeneratedClipsTests
{
    private const string ProjectFileStorageRoot = "video-compile-generated-clips-tests";

    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ExecutionId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid CompileStepId = Guid.Parse("13572468-1357-2468-1357-246811111111");

    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";
    private const string ManifestKey = "projects/p/agentFiles/video-analysis/e/step-3-generated-clips.json";
    private const string EdlKey = "projects/p/agentFiles/video-analysis/e/step-4-edl.json";

    private const int CompileStepOrder = 4;
    private const int ManifestStepOrder = 3;

    /// <summary>Wave C: where a dailies room's TakeSelectionOutput sits in history when a test supplies one.</summary>
    private const int TakeSelectionStepOrder = 5;

    /// <summary>A generated clip key under THIS project's own generated root — the only prefix the ScreenContent route composites.</summary>
    private static string OwnGeneratedKey(int ordinal) => $"projects/{ProjectId}/agentFiles/generated/{ExecutionId}/step-3-v{ordinal}.mp4";

    /// <summary>
    /// Wave C: the exact single-source <c>-filter_complex</c> this fixture's EnableGeneratedClips=false
    /// compile emits — pinned as a literal, independently of the code that produces it, exactly like
    /// <c>VoiceoverBackwardCompatibilityTests.PreVoiceoverFilterGraph</c>. The differential
    /// assertions in the byte-identity test prove "same as without the ref"; this proves "same as
    /// before the feature existed", which a differential test cannot (both sides could drift together).
    /// </summary>
    private const string PreGeneratedClipsFilterGraph =
        "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=20.066667,select='gte(t,0)*lt(t,20)',setpts=N/FRAME_RATE/TB[vout];" +
        "[0:a]aselect='gte(t,0)*lt(t,20)',asetpts=N/SR/TB[aout]";

    private static string ClipKey(int ordinal) => $"projects/p/agentFiles/generated/e/step-3-v{ordinal}.mp4";

    // ==========================================================================================
    // (1) Byte identity: EnableGeneratedClips = false is the untouched pre-wave compile path
    // ==========================================================================================

    [Fact]
    public async Task EnableGeneratedClips_false_is_byte_identical_with_and_without_a_configured_manifest_ref()
    {
        // The load-bearing backward-compatibility guarantee of this whole addition: a config that
        // NAMES a generated-clip manifest but leaves the feature off must produce the exact same
        // EDL and the exact same ffmpeg argv as a config that never mentions it.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        var withoutRef = new RunResult();
        var withRef = new RunResult();

        await RunCompileAsync(
            artifact, decisionJson, ManifestJson(),
            cfg => cfg with { EnableGeneratedClips = false, GeneratedClips = null, PrePaddingMs = 0, PostPaddingMs = 0 },
            withoutRef);

        await RunCompileAsync(
            artifact, decisionJson, ManifestJson(),
            cfg => cfg with
            {
                EnableGeneratedClips = false,
                GeneratedClips = new ExtractInputRef(ExtractInputSource.Step, StepOrder: ManifestStepOrder),
                PrePaddingMs = 0,
                PostPaddingMs = 0
            },
            withRef);

        withoutRef.Status.Should().Be(StepStatus.Completed);
        withRef.Status.Should().Be(StepStatus.Completed);

        withRef.EdlJson.Should().Be(withoutRef.EdlJson, "the default EDL must be unchanged, byte for byte");
        NormalizedInvocations(withRef).Should().BeEquivalentTo(
            NormalizedInvocations(withoutRef),
            o => o.WithStrictOrdering(),
            "no extra ffmpeg input and no extra filter may be added when the feature is off");

        // Specifically: exactly one -i (the single source clip), and no generated-clips key anywhere.
        EncodeInvocation(withoutRef).Count(a => a == "-i").Should().Be(1);
        EncodeInvocation(withRef).Count(a => a == "-i").Should().Be(1);

        withoutRef.OutputJson.Should().NotContain("generatedClips");
        withRef.OutputJson.Should().NotContain("generatedClips");
        withoutRef.EdlJson.Should().NotContain("generatedClips");

        // The golden pin: not merely "the two runs agree", but "the encode IS the pre-feature encode".
        FilterComplex(EncodeInvocation(withoutRef)).Should().Be(PreGeneratedClipsFilterGraph);
        FilterComplex(EncodeInvocation(withRef)).Should().Be(PreGeneratedClipsFilterGraph);
    }

    // ==========================================================================================
    // (7) Wave C: the dailies room's take selection
    // ==========================================================================================

    private static string TakeSelectionJson(params string[] takeIds) =>
        JsonSerializer.Serialize(new
        {
            selected = takeIds.Select(id => new { takeId = id, reason = "test" }),
            planRationale = "test"
        });

    private static JsonObject Take(string clipId, int shotIndex, int take, string anchor, int ordinal) =>
        Clip(clipId, purpose: "Cutaway", status: "succeeded", storageKey: ClipKey(ordinal), anchor: anchor) is var c
            ? Mutate(c, o => { o["shotIndex"] = shotIndex; o["take"] = take; })
            : throw new InvalidOperationException();

    private static JsonObject Mutate(JsonObject o, Action<JsonObject> edit)
    {
        edit(o);
        return o;
    }

    [Fact]
    public async Task TakeSelection_places_only_the_chosen_take_of_a_shot_and_the_first_delivered_take_of_an_unselected_shot()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        // Shot 1 (v1t1/v1t2) anchors s0, shot 2 (v2t1/v2t2) anchors s1. The room picked v1t2 and
        // left shot 2 unselected.
        string manifest = ManifestJson(
            Take("v1t1", 0, 1, "s0", 1),
            Take("v1t2", 0, 2, "s0", 2),
            Take("v2t1", 1, 1, "s1", 3),
            Take("v2t2", 1, 2, "s1", 4));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { TakeSelection = new ExtractInputRef(ExtractInputSource.Step, StepOrder: TakeSelectionStepOrder) },
            run, takeSelectionJson: TakeSelectionJson("v1t2"));

        run.Status.Should().Be(StepStatus.Completed, run.Result.ErrorDetails ?? run.OutputJson);

        JsonObject node = GeneratedClipsNode(run);
        node["appliedCount"]!.GetValue<int>().Should().Be(2, "one take per shot: the chosen v1t2 and shot 2's first delivered v2t1");
        node["cutawaysApplied"]!.GetValue<int>().Should().Be(2);

        node["dropped"]!.AsArray()
            .Select(d => (d!["clipId"]!.GetValue<string>(), d["reason"]!.GetValue<string>()))
            .Should().Equal(("v1t1", "take_not_selected"), ("v2t2", "take_not_selected"));

        JsonObject selection = (JsonObject)node["takeSelection"]!;
        selection["configured"]!.GetValue<bool>().Should().BeTrue();
        selection["applied"]!.GetValue<bool>().Should().BeTrue();
        selection["selectedTakeIds"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("v1t2");

        // The dropped takes were never fetched: only the two placed clips' keys appear anywhere in the argv.
        run.FfmpegInvocations.SelectMany(a => a).Should().NotContain(a => a.Contains("step-3-v1.mp4") || a.Contains("step-3-v4.mp4"));
    }

    [Fact]
    public async Task An_unresolvable_TakeSelection_degrades_to_no_filter_and_says_so_on_the_node()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        string manifest = ManifestJson(Take("v1t1", 0, 1, "s0", 1), Take("v1t2", 0, 2, "s0", 2));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            // Step 9 exists nowhere in history.
            cfg => WithGeneratedClips()(cfg) with { TakeSelection = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 9) },
            run);

        run.Status.Should().Be(StepStatus.Completed);
        JsonObject node = GeneratedClipsNode(run);
        JsonObject selection = (JsonObject)node["takeSelection"]!;
        selection["applied"]!.GetValue<bool>().Should().BeFalse();
        selection["reason"]!.GetValue<string>().Should().Contain("TakeSelection");
        node["dropped"]!.AsArray().Should().NotContain(d => d!["reason"]!.GetValue<string>() == "take_not_selected",
            "with no usable selection every take flows exactly as it did before the feature");
    }

    [Fact]
    public async Task Without_a_TakeSelection_the_node_carries_no_takeSelection_key()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, ManifestJson(Take("v1", 0, 1, "s0", 1)), WithGeneratedClips(), run);

        GeneratedClipsNode(run).ContainsKey("takeSelection").Should().BeFalse("absent, not null — the pre-wave-C node shape");
        GeneratedClipsNode(run).ContainsKey("screenContentRoutedToInserts").Should().BeFalse();
    }

    // ==========================================================================================
    // (8) Wave C: ScreenContent rides the tracked-insert path
    // ==========================================================================================

    private static VideoInsertRegionTrack ChromaPlate(string id, string shotId, double startSec, double endSec) => new(
        Id: id, ShotId: shotId, StartSec: startSec, EndSec: endSec,
        Keyframes:
        [
            new VideoInsertQuadKeyframe(startSec, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72),
            new VideoInsertQuadKeyframe(endSec, 0.35, 0.20, 0.65, 0.22, 0.36, 0.70, 0.66, 0.72)
        ],
        Confidence: 0.9, MeanAreaRatio: 0.12, MeanAspectRatio: 0.6,
        MotionClass: "Slow", ColorName: "green");

    private static void ServeOwnGeneratedClips(Mock<IProjectFileWorkspace> workspace) =>
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(k => k.StartsWith($"projects/{ProjectId}/agentFiles/generated/", StringComparison.Ordinal)),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, [0x0A, 0x0B, 0x0C]);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task ScreenContent_is_composited_into_the_offered_plate_overlapping_its_anchor_through_the_existing_insert_path()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]) with
        {
            InsertRegions = [ChromaPlate("r0", "s1", 12.0, 18.0)],
            OfferedInsertRegionIds = ["r0"]
        };
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        string manifest = ManifestJson(Clip("v1", purpose: "ScreenContent", status: "succeeded", storageKey: OwnGeneratedKey(1), anchor: "s1"));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { EnableInserts = true },
            run, configureWorkspace: ServeOwnGeneratedClips);

        run.Status.Should().Be(StepStatus.Completed, run.Result.ErrorDetails ?? run.OutputJson);

        JsonObject generated = GeneratedClipsNode(run);
        generated["screenContentRoutedToInserts"]!.GetValue<int>().Should().Be(1);
        generated["dropped"]!.AsArray().Should().BeEmpty("a routed ScreenContent clip is not a dropped clip");
        generated["appliedCount"]!.GetValue<int>().Should().Be(0, "a composite is an INSERT, not a placement — it spends MaxInserts, never MaxGeneratedClips");

        JsonObject inserts = (JsonObject)OutputSummary(run)["inserts"]!;
        inserts["appliedInsertCount"]!.GetValue<int>().Should().Be(1);
        JsonObject applied = (JsonObject)inserts["appliedInserts"]!.AsArray()[0]!;
        applied["regionId"]!.GetValue<string>().Should().Be("r0");
        applied["contentOrigin"]!.GetValue<string>().Should().Be("generated");

        // Zero new compositing code: the clip went through the same perspective + alphamerge chain a
        // plan insert does, as its own extra -i.
        string graph = FilterComplex(EncodeInvocation(run));
        graph.Should().Contain("perspective=").And.Contain("alphamerge");
        EncodeInvocation(run).Count(a => a == "-i").Should().Be(2);
    }

    [Fact]
    public async Task ScreenContent_with_no_offered_plate_at_its_anchor_degrades_with_no_insert_region_at_anchor()
    {
        // The only plate lives entirely inside s0; the clip anchors s1.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]) with
        {
            InsertRegions = [ChromaPlate("r0", "s0", 1.0, 5.0)],
            OfferedInsertRegionIds = ["r0"]
        };
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        string manifest = ManifestJson(Clip("v1", purpose: "ScreenContent", status: "succeeded", storageKey: OwnGeneratedKey(1), anchor: "s1"));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { EnableInserts = true },
            run, configureWorkspace: ServeOwnGeneratedClips);

        run.Status.Should().Be(StepStatus.Completed);
        GeneratedClipsNode(run)["dropped"]!.AsArray()
            .Select(d => (d!["clipId"]!.GetValue<string>(), d["reason"]!.GetValue<string>()))
            .Should().Equal(("v1", "no_insert_region_at_anchor"));
        run.FfmpegInvocations.SelectMany(a => a).Should().NotContain(a => a.Contains("step-3-v1.mp4"), "a clip with nowhere to go is never fetched");
    }

    [Fact]
    public async Task ScreenContent_whose_key_is_outside_this_projects_generated_root_is_refused_before_any_fetch()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]) with
        {
            InsertRegions = [ChromaPlate("r0", "s1", 12.0, 18.0)],
            OfferedInsertRegionIds = ["r0"]
        };
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));
        // ClipKey() is "projects/p/..." — a DIFFERENT project's root — and the second is a traversal.
        string manifest = ManifestJson(
            Clip("v1", purpose: "ScreenContent", status: "succeeded", storageKey: ClipKey(1), anchor: "s1"),
            Clip("v2", purpose: "ScreenContent", status: "succeeded", storageKey: $"projects/{ProjectId}/agentFiles/generated/../../userFiles/x.mp4", anchor: "s1"));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { EnableInserts = true },
            run, configureWorkspace: ServeOwnGeneratedClips);

        run.Status.Should().Be(StepStatus.Completed);
        GeneratedClipsNode(run)["dropped"]!.AsArray()
            .Select(d => d!["reason"]!.GetValue<string>())
            .Should().Equal("clip_key_outside_generated_prefix", "clip_key_outside_generated_prefix");
        ((JsonObject)OutputSummary(run)["inserts"]!)["appliedInsertCount"]!.GetValue<int>().Should().Be(0);
    }

    // ==========================================================================================
    // (2) The wave's NOT-IN-THIS-WAVE vocabulary, and the per-clip degrade reasons
    // ==========================================================================================

    [Fact]
    public async Task Unsupported_purposes_degrade_with_their_own_reason_and_change_neither_the_argv_nor_the_output()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        // The baseline has an EMPTY clip list — nothing is planned, so nothing can be applied and
        // the compile falls through to the untouched single-source path.
        var baseline = new RunResult();
        await RunCompileAsync(artifact, decisionJson, ManifestJson(), WithGeneratedClips(), baseline);

        // Every one of these clips must degrade on its own, with its own reason. A SeamBridge with
        // no anchor has nowhere to be spliced; Extend is not placed by this compiler; ScreenContent
        // (wave C) is routed to the tracked-insert path, which this config leaves OFF (EnableInserts
        // defaults false), so it degrades with inserts_disabled; "Fancy" is a word this build has
        // never heard of; the two Cutaways fail the availability precondition (no storage key / a
        // failed status).
        string manifest = ManifestJson(
            Clip("v1", purpose: "SeamBridge", status: "succeeded", storageKey: ClipKey(1), anchor: ""),
            Clip("v2", purpose: "Extend", status: "succeeded", storageKey: ClipKey(2)),
            Clip("v3", purpose: "ScreenContent", status: "succeeded", storageKey: ClipKey(3)),
            Clip("v4", purpose: "Fancy", status: "succeeded", storageKey: ClipKey(4)),
            Clip("v5", purpose: "Cutaway", status: "succeeded", storageKey: null, anchor: "s0"),
            Clip("v6", purpose: "Cutaway", status: "failed", storageKey: ClipKey(6), anchor: "s0"));

        var withClips = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClips(), withClips);

        withClips.Status.Should().Be(StepStatus.Completed, "not one of these can take the cut hostage");

        JsonArray dropped = GeneratedClipsNode(withClips)["dropped"]!.AsArray();
        dropped.Select(d => (d!["clipId"]!.GetValue<string>(), d["reason"]!.GetValue<string>()))
            .Should().Equal(
                ("v1", "anchor_not_in_output"),
                ("v2", "not_supported_in_this_wave"),
                ("v3", "inserts_disabled"),
                ("v4", "unknown_purpose"),
                ("v5", "clip_not_available"),
                ("v6", "clip_not_available"));

        GeneratedClipsNode(withClips)["appliedCount"]!.GetValue<int>().Should().Be(0);
        GeneratedClipsNode(withClips)["enabled"]!.GetValue<bool>().Should().BeTrue();

        // Nothing was applied, so the ENCODE is the baseline's, argument for argument.
        //
        // Only the encode invocation is comparable on the whole run: wave B's Extend path downloads
        // and normalizes a clip BEFORE it can discover that there is no narration deficit to fill, so
        // a refused Extend costs one GeneratedClipNormalizer invocation that the baseline (an empty
        // manifest, nothing to download) does not have. That is a real difference in the run and is
        // stated here rather than hidden by comparing filtered invocation lists — the guarantee that
        // matters is that not one argument of the ENCODE changes, which is what is asserted.
        EncodeInvocation(withClips).Should().Equal(
            EncodeInvocation(baseline), "a degraded clip must not add, remove or reorder a single encode argument");
        OutputWithoutGeneratedClips(withClips.OutputJson)
            .Should().Be(OutputWithoutGeneratedClips(baseline.OutputJson));

        // And the node is present because the feature is ENABLED, not because anything applied.
        baseline.OutputJson.Should().Contain("generatedClips");
        GeneratedClipsNode(baseline)["dropped"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_deferred_purpose_is_never_silently_placed_as_a_cutaway_or_a_segment()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        // ScreenContent carries a perfectly valid anchor and a real storage key. If the placement
        // table fell through to "anything with an anchor is a cutaway", this clip would composite —
        // that is exactly the silent misplacement the wave forbids. Wave C routes it to the
        // tracked-insert path instead, which this config keeps OFF, so it must still touch nothing.
        string manifest = ManifestJson(
            Clip("v1", purpose: "ScreenContent", status: "succeeded", storageKey: ClipKey(1), anchor: "s1"));

        var baseline = new RunResult();
        await RunCompileAsync(artifact, decisionJson, ManifestJson(), WithGeneratedClips(), baseline);

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClips(), run);

        NormalizedInvocations(run).Should().BeEquivalentTo(
            NormalizedInvocations(baseline), o => o.WithStrictOrdering());

        string graph = run.FilterGraph!;
        graph.Should().NotContain("overlay=x=0:y=0", "a deferred purpose must never be composited as a cutaway");
        graph.Should().NotContain("[1:v]", "a deferred purpose must never reach the filtergraph at all");
        run.FfmpegInvocations.SelectMany(a => a).Should().NotContain(ClipKey(1));
    }

    // ==========================================================================================
    // (3) Cutaway: picture-only, over the anchor's OUTPUT window
    // ==========================================================================================

    [Fact]
    public async Task Cutaway_replaces_picture_only_over_the_anchors_output_window_and_adds_no_audio_for_itself()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        // The whole source [0,20] is kept, so s1's own window [10,20] maps to output [10,20].
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string manifest = ManifestJson(
            Clip("v1", purpose: "Cutaway", status: "succeeded", storageKey: ClipKey(1), anchor: "s1",
                durationSeconds: 3));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClips(), run);

        run.Status.Should().Be(StepStatus.Completed);
        GeneratedClipsNode(run)["cutawaysApplied"]!.GetValue<int>().Should().Be(1);
        GeneratedClipsNode(run)["appliedCount"]!.GetValue<int>().Should().Be(1);

        List<string> encode = EncodeInvocation(run);
        string graph = run.FilterGraph!;

        // The generated clip is the LAST -i (after the single source), so its input index is 1.
        encode.Count(a => a == "-i").Should().Be(2);
        encode.Should().NotContain(ClipKey(1), "the -i carries the local scratch path, never the storage key");
        encode.Should().Contain(Path.Combine(Path.GetDirectoryName(FilterScriptPath(encode))!, "gclip-1-norm.mp4"));

        // A picture-overlay bounded to exactly the resolved window, with eof_action=pass so a clip
        // shorter than its window reverts to the base picture instead of freezing.
        graph.Should().Contain("[1:v]scale=1920:1080:force_original_aspect_ratio=decrease");
        graph.Should().Contain("overlay=x=0:y=0:eof_action=pass:shortest=0:enable='between(t,");

        (double Start, double End) = ExtractCutawayWindow(graph);
        // cutaway-clip-start-1: the window is whole frames written half a frame early, and the clip's frame 0 sits on its start.
        double halfFrame = 0.5 * 1001.0 / 30000.0;
        graph.Should().Contain("settb=AVTB,setpts=PTS-STARTPTS+9991667[", "the clip is stamped on the window frame, a quarter frame early (QA-DYN-43)");
        Start.Should().BeApproximately(10.0 - halfFrame, 0.02, "the window is the anchor's OUTPUT-timeline start, never source time");
        End.Should().BeApproximately(13.0 - halfFrame, 0.02, "the window is bounded by the clip's planned duration and by the anchor's own end");

        // NO audio for the cutaway: the picture replacement is over the base audio graph, which
        // keeps the anchor's dialogue running continuously across it (the J/L cut).
        Regex.Matches(graph, @"\[\d+:a\]").Select(m => m.Value).Distinct().Should().Equal("[0:a]");
        graph.Should().NotContain("[1:a]");
        encode.Should().NotContain("-itsoffset");
        encode.Should().Contain("[aout]");

        // A cutaway REPLACES picture in place: it neither lengthens the piece...
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().Be(20.0);

        // ...nor becomes its own concat segment.
        graph.Should().NotContain("concat=n=3");
    }

    [Fact]
    public async Task SeamBridge_is_spliced_between_beats_as_a_muted_cover_fit_segment_and_lengthens_the_piece()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        // ONE kept span covers both shots, so the bridge must split it at s1's start.
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string manifest = ManifestJson(
            Clip("v1", purpose: "SeamBridge", status: "succeeded", storageKey: ClipKey(1), anchor: "s1"));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClips(), run);

        run.Status.Should().Be(StepStatus.Completed);
        GeneratedClipsNode(run)["seamBridgesApplied"]!.GetValue<int>().Should().Be(1);
        GeneratedClipsNode(run)["appliedCount"]!.GetValue<int>().Should().Be(1);
        GeneratedClipsNode(run)["dropped"]!.AsArray().Should().BeEmpty();

        string graph = run.FilterGraph!;

        // Three program spans — s0, the bridge, s1 — concatenated in that order.
        graph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,trim=start=0:end=10");
        graph.Should().Contain("[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=");
        graph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=20.066667,trim=start=10:end=20");
        graph.IndexOf("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,trim=start=0:end=10", StringComparison.Ordinal)
            .Should().BeLessThan(graph.IndexOf("[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=", StringComparison.Ordinal));

        // The bridge fills the canvas; the analysed footage keeps the contain-fit normalization.
        graph.Should().Contain("force_original_aspect_ratio=increase,crop=1920:1080");

        // Picture only: the generated clip's own audio is never read.
        graph.Should().NotContain("[1:a]");

        // It is program time, so the finished piece is longer than the kept source.
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().BeGreaterThan(20.0);
        OutputSummary(run)["retainedRatio"]!.GetValue<double>().Should().Be(1.0, "a bridge is not retained source");
    }

    [Fact]
    public async Task WholeShots_chooses_the_editors_spans_before_the_bridge_is_spliced_so_a_kept_anchor_keeps_its_bridge()
    {
        // Three kept spans with gaps between them: 3 s, 4 s and 10 s. A 8 s target (ceiling 8.8 s) drops
        // the 10 s closer and keeps the first two, and the bridge's anchor (s1) is one of them.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 3.0), ("s1", 4.0, 8.0), ("s2", 9.0, 19.0)],
            offeredIds: ["s0", "s1", "s2"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"), ("s2", "s2", "keep"));
        string manifest = ManifestJson(
            Clip("v1", purpose: "SeamBridge", status: "succeeded", storageKey: ClipKey(1), anchor: "s1"));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClipsAndWholeShots(8), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.OutputJson);

        // The whole-shot decision is taken on the editor's spans, so the chosen cut is 3 + 4 = 7 s and the
        // 10 s closer is the span that is dropped — never the bridge.
        JsonObject fitted = OutputSummary(run)["length"]!["fitted"]!.AsObject();
        fitted["mode"]!.GetValue<string>().Should().Be("WholeShots");
        fitted["achievedSec"]!.GetValue<double>().Should().Be(7);
        fitted["droppedSpans"]!.AsArray().Select(s => s!["index"]!.GetValue<int>()).Should().Equal(2);

        GeneratedClipsNode(run)["seamBridgesApplied"]!.GetValue<int>().Should().Be(1);
        GeneratedClipsNode(run)["dropped"]!.AsArray().Should().BeEmpty();

        // s0, then the bridge, then s1 — and nothing from s2.
        string graph = run.FilterGraph!;
        graph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.066667,trim=start=0:end=3");
        graph.Should().Contain("[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.066667,trim=start=0:end=3");
        graph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=8.066667,trim=start=4:end=8");
        graph.Should().NotContain("trim=start=9");
        graph.IndexOf("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.066667,trim=start=0:end=3", StringComparison.Ordinal)
            .Should().BeLessThan(graph.IndexOf("[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.066667,trim=start=0:end=3", StringComparison.Ordinal));

        // The bridge is program time on top of the fitted cut: 3 + 3 + 4.
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(10.0, 0.1);
    }

    [Fact]
    public async Task A_bridge_whose_anchor_the_whole_shot_fit_dropped_is_reported_unplaced_not_silently_cut()
    {
        // A 5 s target leaves only the 3 s opener, so s1 — the bridge's anchor — is not in the cut at all.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 3.0), ("s1", 4.0, 8.0), ("s2", 9.0, 19.0)],
            offeredIds: ["s0", "s1", "s2"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"), ("s2", "s2", "keep"));
        string manifest = ManifestJson(
            Clip("v1", purpose: "SeamBridge", status: "succeeded", storageKey: ClipKey(1), anchor: "s1"));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClipsAndWholeShots(5), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.OutputJson);

        JsonObject fitted = OutputSummary(run)["length"]!["fitted"]!.AsObject();
        fitted["achievedSec"]!.GetValue<double>().Should().Be(3);
        fitted["droppedSpans"]!.AsArray().Select(s => s!["index"]!.GetValue<int>()).Should().Equal(1, 2);

        // The bridge cannot play without its anchor, and the report says so instead of claiming a bridge
        // the finished piece does not contain (which is what fitting after the splice did).
        GeneratedClipsNode(run)["seamBridgesApplied"]!.GetValue<int>().Should().Be(0);
        JsonArray dropped = GeneratedClipsNode(run)["dropped"]!.AsArray();
        dropped.Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("anchor_not_in_output");
        dropped[0]!["clipId"]!.GetValue<string>().Should().Be("v1");
        dropped[0]!["purpose"]!.GetValue<string>().Should().Be("seambridge");

        run.FilterGraph!.Should().NotContain("[1:v]");
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(3.0, 0.1);
    }

    [Fact]
    public async Task Cutaway_whose_anchor_was_cut_away_degrades_with_anchor_not_in_output()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        // Only s0 survives, so an anchor on s1 names a moment the finished edit does not contain.
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        string manifest = ManifestJson(
            Clip("v1", purpose: "Cutaway", status: "succeeded", storageKey: ClipKey(1), anchor: "s1",
                durationSeconds: 3));

        var baseline = new RunResult();
        await RunCompileAsync(artifact, decisionJson, ManifestJson(), WithGeneratedClips(), baseline);

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClips(), run);

        run.Status.Should().Be(StepStatus.Completed);
        JsonArray dropped = GeneratedClipsNode(run)["dropped"]!.AsArray();
        dropped.Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("anchor_not_in_output");
        dropped[0]!["clipId"]!.GetValue<string>().Should().Be("v1");
        dropped[0]!["purpose"]!.GetValue<string>().Should().Be("cutaway");
        GeneratedClipsNode(run)["appliedCount"]!.GetValue<int>().Should().Be(0);

        // Only the ENCODE argv is comparable here: the placement table downloads and normalizes a
        // clip before it can discover that the clip's anchor was cut away (the anchor is resolved
        // against the output timeline, which exists independently of the clip). What matters is
        // that a dropped cutaway adds no input and no filter of its own to the encode.
        EncodeInvocation(run).Should().BeEquivalentTo(EncodeInvocation(baseline), o => o.WithStrictOrdering());
    }

    // ==========================================================================================
    // (4) Cold open / end card: concat segments, and the program envelope counts them
    // ==========================================================================================

    [Fact]
    public async Task ColdOpen_and_EndCard_are_concat_segments_and_the_program_envelope_is_computed_against_the_total_including_them()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string manifest = ManifestJson(
            Clip("v1", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(1), durationSeconds: 10),
            Clip("v2", purpose: "EndCard", status: "succeeded", storageKey: ClipKey(2), durationSeconds: 10));

        // 5s in + 20s out against a 20s body would COLLAPSE (both fades shrink to totalSec/3 ≈
        // 6.667s on the pre-wave arithmetic). Against the final 40s (20s body + two 10s generated
        // segments) the requested 5s/20s stand — so the resolved envelope is a direct read on
        // WHICH total the executor used.
        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { ProgramFadeInMs = 5000, ProgramFadeOutMs = 20000 },
            run);

        run.Status.Should().Be(StepStatus.Completed);

        JsonObject generated = GeneratedClipsNode(run);
        generated["coldOpensApplied"]!.GetValue<int>().Should().Be(1);
        generated["endCardsApplied"]!.GetValue<int>().Should().Be(1);
        generated["appliedCount"]!.GetValue<int>().Should().Be(2);

        // 20s body + 10s cold open + 10s end card.
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().Be(40.0);
        JsonElement edl = JsonDocument.Parse(run.EdlJson).RootElement;
        edl.GetProperty("programFade").GetProperty("videoOutSec").GetDouble()
            .Should().Be(20.0, "the envelope is resolved against the FINAL total, cold open and end card included");

        List<string> encode = EncodeInvocation(run);
        string graph = run.FilterGraph!;

        encode.Count(a => a == "-i").Should().Be(3, "one source plus two generated segments");

        // The concat stage's segment order IS the placement: cold open first, the program body in
        // the middle, the end card last — each generated segment contributing its own video and
        // (probed-present) audio branch.
        graph.Should().Contain(
            "[gcold0][acold0][vbodyf][abodyf][gend0][aend0]concat=n=3:v=1:a=1",
            "cold open, program body, end card — three segments, in that order");
        graph.Should().Contain("fade=t=out:st=20:d=20", "the fade-out lands 20s from the END of the lengthened piece");
        graph.Should().Contain("fade=t=in:st=0:d=5");
    }

    [Fact]
    public Task The_music_bed_spans_the_cold_open_the_body_and_the_end_card_when_the_source_is_muted() =>
        MusicBedSpansProgram(mute: true);

    [Fact]
    public Task Lift_windows_planned_for_the_body_do_not_run_past_the_program_end() =>
        MusicBedSpansProgram(mute: false);

    private async Task MusicBedSpansProgram(bool mute)
    {
        // Run ec066f71: a 6.9 s body between a cold open and an end card (the harness probes each clip at 10 s). The bed was sized to the
        // body and mixed into it, so the audio stopped at 6.9 s while the film ran on.
        VideoAnalysisArtifact artifact = BuildArtifact(shots: [("s0", 0.0, 6.9)], offeredIds: ["s0"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string manifest = ManifestJson(
            Clip("v1", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(1), durationSeconds: 9),
            Clip("v2", purpose: "EndCard", status: "succeeded", storageKey: ClipKey(2), durationSeconds: 4));
        Guid musicFileId = Guid.NewGuid();
        const string musicKey = "projects/p/userFiles/music.mp3";

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with
            {
                EnableMusic = true,
                MusicTrackProjectFileId = musicFileId,
                MuteSourceAudio = mute
            },
            run,
            configureWorkspace: workspace =>
            {
                workspace
                    .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new List<ProjectWorkspaceFile>
                    {
                        new(musicFileId, ProjectId, "music.mp3", null, "userFiles", musicKey, "audio/mpeg", 4096, DateTime.UtcNow, null)
                    });
                workspace
                    .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), musicKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
                    {
                        File.WriteAllBytes(dest, [0, 1, 2]);
                        return Task.CompletedTask;
                    });
            });

        run.Status.Should().Be(StepStatus.Completed);
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(26.9, 0.05);

        JsonElement music = JsonDocument.Parse(run.EdlJson).RootElement.GetProperty("music");
        music.GetProperty("applied").GetBoolean().Should().BeTrue();
        music.GetProperty("outputDurationSec").GetDouble().Should().BeApproximately(26.9, 0.05);
        music.GetProperty("playEndSec").GetDouble().Should().BeApproximately(26.9, 0.05);

        // The bed is mixed onto the CONCATENATED program (not into the body), so it covers all of it.
        run.FilterGraph.Should().Contain("concat=n=3:v=1:a=1");
        // Unmuted source with no speech detected: the planner lifts the whole BODY, so after the shift
        // the lift must end at the program's end, not run past it (planned against the program length
        // it became [10, 36.9]; the end card has its own sound).
        if (!mute)
        {
            music.GetProperty("liftWindows").GetInt32().Should().BeGreaterThan(0);
            // The clips carry their own sound: the lift is the body only, [10, 16.9], and never covers the end card.
            run.FilterGraph.Should().Contain("(16.9-t)");
            run.FilterGraph.Should().NotContain("(26.9-t)").And.NotContain("(36.9-t)", "a window planned against the program and then shifted ran past the body");
        }
        run.FilterGraph.Should().Contain("[acatm]");
        run.FilterGraph.Should().Contain("[acatm][amus]amix=inputs=2:duration=first");
    }

    [Fact]
    public async Task GeneratedClipAudio_Mute_replaces_a_cold_opens_sound_with_silence_and_reports_each_clip()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: [("s0", 0.0, 10.0)], offeredIds: ["s0"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string manifest = ManifestJson(
            Clip("v1", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(1), durationSeconds: 10));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { GeneratedClipAudio = GeneratedAudioMode.Mute }, run);

        run.Status.Should().Be(StepStatus.Completed);
        run.FilterGraph.Should().Contain("anullsrc=r=48000:cl=stereo:d=10[acold0]", "the clip's own sound is not read");
        run.FilterGraph.Should().NotContain("[1:a]atrim=end=10,asetpts=PTS-STARTPTS");
        JsonObject clip = GeneratedClipsNode(run)["clips"]!.AsArray()[0]!.AsObject();
        clip["clipId"]!.GetValue<string>().Should().Be("v1");
        clip["audio"]!.GetValue<string>().Should().Be("muted");
    }

    [Fact]
    public async Task GeneratedClipAudio_Auto_keeps_a_cold_opens_sound_and_reports_it_as_dialogue()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: [("s0", 0.0, 10.0)], offeredIds: ["s0"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        string manifest = ManifestJson(
            Clip("v1", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(1), durationSeconds: 10));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, manifest, WithGeneratedClips(), run);

        run.Status.Should().Be(StepStatus.Completed);
        run.FilterGraph.Should().Contain("[1:a]atrim=end=10,asetpts=PTS-STARTPTS");
        GeneratedClipsNode(run)["clips"]!.AsArray()[0]!["audio"]!.GetValue<string>().Should().Be("dialogue");
    }

    [Fact]
    public async Task At_most_MaxGeneratedClips_are_applied_and_the_excess_degrades_with_max_generated_clips_exceeded()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        string manifest = ManifestJson(
            Clip("v1", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(1), durationSeconds: 5),
            Clip("v2", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(2), durationSeconds: 5),
            Clip("v3", purpose: "ColdOpen", status: "succeeded", storageKey: ClipKey(3), durationSeconds: 5));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, manifest,
            cfg => WithGeneratedClips()(cfg) with { MaxGeneratedClips = 2 },
            run);

        run.Status.Should().Be(StepStatus.Completed);

        JsonObject generated = GeneratedClipsNode(run);
        generated["maxGeneratedClips"]!.GetValue<int>().Should().Be(2);
        generated["coldOpensApplied"]!.GetValue<int>().Should().Be(2);
        generated["appliedCount"]!.GetValue<int>().Should().Be(2);
        generated["dropped"]!.AsArray().Select(d => (d!["clipId"]!.GetValue<string>(), d["reason"]!.GetValue<string>()))
            .Should().Equal(("v3", "max_generated_clips_exceeded"));

        List<string> encode = EncodeInvocation(run);
        encode.Count(a => a == "-i").Should().Be(3, "the capped clip is never even downloaded, let alone fed to ffmpeg");
        run.FilterGraph.Should().Contain("[gcold0][acold0][gcold1][acold1][vbodyf][abodyf]concat=n=3:v=1:a=1");

        // 20s body + the two applied 10s cold opens; the capped third contributes nothing.
        OutputSummary(run)["outputDurationSec"]!.GetValue<double>().Should().Be(40.0);
    }

    // ==========================================================================================
    // (5) The one hard error, and the purely-soft manifest failures
    // ==========================================================================================

    [Fact]
    public async Task EnableGeneratedClips_with_StreamCopy_fails_GENERATED_CLIPS_REQUIRE_REENCODE()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(shots: [("s0", 0.0, 10.0)], offeredIds: ["s0"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, ManifestJson(),
            cfg => WithGeneratedClips()(cfg) with { Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true },
            run);

        run.Status.Should().Be(StepStatus.Failed);
        ErrorCode(run).Should().Be("GENERATED_CLIPS_REQUIRE_REENCODE");
        run.FfmpegInvocations.Should().BeEmpty("the config error is caught before any encode work");
    }

    [Fact]
    public async Task An_unresolvable_manifest_degrades_softly_with_plan_unresolved_and_never_fails_the_compile()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", 0.0, 10.0), ("s1", 10.0, 20.0)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s1", "keep"));

        var baseline = new RunResult();
        await RunCompileAsync(artifact, decisionJson, ManifestJson(), WithGeneratedClips(), baseline);

        // No manifest storage key on the generating step's history entry: the compile has nothing
        // to place and must behave exactly as if no clips had been planned.
        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, ManifestJson(),
            WithGeneratedClips(), run, manifestAvailable: false);

        run.Status.Should().Be(StepStatus.Completed);
        GeneratedClipsNode(run)["reason"]!.GetValue<string>().Should().Be("plan_unresolved");
        GeneratedClipsNode(run)["appliedCount"]!.GetValue<int>().Should().Be(0);

        NormalizedInvocations(run).Should().BeEquivalentTo(
            NormalizedInvocations(baseline), o => o.WithStrictOrdering());
        OutputWithoutGeneratedClips(run.OutputJson).Should().Be(OutputWithoutGeneratedClips(baseline.OutputJson));
    }

    // ==========================================================================================
    // Config / manifest JSON helpers
    // ==========================================================================================

    /// <summary>
    /// The opt-in compile config every placement test starts from. <c>PrePaddingMs</c>/
    /// <c>PostPaddingMs</c> are zeroed so the resolved spans (and therefore the cutaway windows)
    /// are exactly the artifact's own segment times.
    /// </summary>
    private static Func<VideoCompileStepConfig, VideoCompileStepConfig> WithGeneratedClipsAndWholeShots(int targetSec) =>
        cfg => WithGeneratedClips()(cfg) with
        {
            TargetDurationSec = targetSec,
            TargetDurationMode = TargetDurationMode.WholeShots
        };

    private static Func<VideoCompileStepConfig, VideoCompileStepConfig> WithGeneratedClips() =>
        cfg => cfg with
        {
            EnableGeneratedClips = true,
            GeneratedClips = new ExtractInputRef(ExtractInputSource.Step, StepOrder: ManifestStepOrder),
            MaxGeneratedClips = 8,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

    private static string ManifestJson(params JsonObject[] clips)
    {
        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["planStepOrder"] = ManifestStepOrder,
            ["analysisStepOrder"] = 1,
            ["providerId"] = Guid.NewGuid().ToString(),
            ["model"] = "MiniMax-H3",
            ["resolution"] = "768P",
            ["ratio"] = "16:9",
            ["takes"] = 1,
            ["clips"] = new JsonArray(clips.Select(c => (JsonNode)c).ToArray()),
            ["meta"] = new JsonObject()
        };
        return manifest.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    /// <summary>One manifest clip in the exact shape <c>VideoGenerateStepExecutor</c> writes.</summary>
    private static JsonObject Clip(
        string clipId,
        string purpose,
        string status,
        string? storageKey,
        string anchor = "s0",
        int durationSeconds = 5) => new()
        {
            ["clipId"] = clipId,
            ["shotIndex"] = 1,
            ["take"] = 1,
            ["purpose"] = purpose,
            ["anchor"] = anchor,
            ["firstFrame"] = null,
            ["lastFrame"] = null,
            ["camera"] = "Static",
            ["duration"] = "Short",
            ["durationSeconds"] = durationSeconds,
            ["status"] = status,
            ["storageKey"] = storageKey,
            ["cost"] = 0.32m,
            ["costBasis"] = "actual",
            ["keyframeRequested"] = false,
            ["keyframeApplied"] = false,
            ["keyframeReason"] = null,
            ["failureReason"] = null,
            ["detail"] = null,
            ["skipReason"] = null
        };

    private static string BuildDecisionJson(params (string FromId, string ToId, string Reason)[] spans)
    {
        var keep = spans.Select(s => new { fromId = s.FromId, toId = s.ToId, reason = s.Reason });
        return JsonSerializer.Serialize(new { keep, editRationale = "test", suggestedTitle = "Test Edit" });
    }

    private static VideoAnalysisArtifact BuildArtifact(
        (string Id, double Start, double End)[] shots,
        string[] offeredIds,
        double? durationSec = null,
        int fpsNum = 30,
        int fpsDen = 1)
    {
        double duration = durationSec ?? shots.Max(s => s.End);
        return new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(duration, fpsNum, fpsDen, 1920, 1080),
            Shots: shots.Select(s => new VideoAnalysisShot(s.Id, s.Start, s.End)).ToList(),
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: offeredIds.ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));
    }

    private static JsonSerializerOptions ConfigOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static JsonSerializerOptions ArtifactOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ==========================================================================================
    // Assertion helpers
    // ==========================================================================================

    /// <summary>Everything one compile run produced, for comparing two runs against each other.</summary>
    private sealed class RunResult
    {
        public StepExecutionResult Result { get; set; } = null!;
        public string OutputJson => Result.Output;
        public StepStatus Status => Result.Status;
        public string? EdlJson { get; set; }

        /// <summary>The run's scratch root, so two runs' argv can be compared on their content rather than on their temp paths.</summary>
        public string ScratchRoot { get; set; } = string.Empty;

        /// <summary>The encode's filtergraph, read off disk WHILE ffmpeg is being invoked (the scratch space is disposed before <c>ExecuteAsync</c> returns).</summary>
        public string? FilterGraph { get; set; }

        public List<List<string>> FfmpegInvocations { get; } = new();
    }

    /// <summary>
    /// The single ENCODE invocation, with the run's scratch root folded out.
    /// <para>
    /// Discriminated on <c>-map</c>: that is emitted by the encode and by nothing else, whereas
    /// <see cref="GeneratedClipNormalizer"/>'s own per-clip re-encode (which runs for every clip
    /// the placement table gets as far as downloading) is captured by the same mock.
    /// </para>
    /// </summary>
    private static List<string> EncodeInvocation(RunResult run)
    {
        List<List<string>> encodes = run.FfmpegInvocations.Where(a => a.Contains("-map")).ToList();
        encodes.Should().HaveCount(1, "these compiles issue exactly one encode call");
        return Normalize(encodes[0], run.ScratchRoot);
    }

    /// <summary>Every captured ffmpeg invocation, scratch-path-normalized so two runs are comparable.</summary>
    private static List<List<string>> NormalizedInvocations(RunResult run) =>
        run.FfmpegInvocations.Select(a => Normalize(a, run.ScratchRoot)).ToList();

    private static List<string> Normalize(IReadOnlyList<string> args, string scratchRoot)
    {
        string root = Path.GetFullPath(scratchRoot);
        return args
            .Select(a => a.StartsWith(root, StringComparison.Ordinal)
                ? "<scratch>" + a[root.Length..].Replace('\\', '/')
                : a)
            .ToList();
    }

    private static JsonObject OutputSummary(RunResult run) => (JsonObject)JsonNode.Parse(run.OutputJson)!;

    private static JsonObject GeneratedClipsNode(RunResult run) =>
        (JsonObject)OutputSummary(run)["generatedClips"]!;

    private static string OutputWithoutGeneratedClips(string outputJson)
    {
        var node = (JsonObject)JsonNode.Parse(outputJson)!;
        node.Remove("generatedClips");
        // The registered render's key carries a fresh file id per run ({fileId}.mp4), so two runs differ there by design.
        if (node["outputStorageKey"] is JsonValue key && key.GetValue<string>().Contains("/outputFiles/", StringComparison.Ordinal))
            node["outputStorageKey"] = "<registered render>";
        return node.ToJsonString();
    }

    private static string ErrorCode(RunResult run)
    {
        using JsonDocument doc = JsonDocument.Parse(run.OutputJson);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;
    }

    /// <summary>The single-source path's inline <c>-filter_complex</c> value.</summary>
    private static string FilterComplex(IReadOnlyList<string> args)
    {
        int index = args.ToList().IndexOf("-filter_complex");
        index.Should().BeGreaterThan(-1, "the single-source path passes its filtergraph inline");
        return args[index + 1];
    }

    private static string FilterScriptPath(IReadOnlyList<string> args)
    {
        int index = args.ToList().IndexOf("-filter_complex_script");
        index.Should().BeGreaterThan(-1, "the segmented path always scripts its filtergraph to a file");
        return args[index + 1];
    }

    private static (double Start, double End) ExtractCutawayWindow(string filterGraph)
    {
        System.Text.RegularExpressions.Match match = Regex.Match(filterGraph, @"enable='between\(t,([0-9.]+),([0-9.]+)\)'");
        match.Success.Should().BeTrue($"expected a bounded cutaway window in: {filterGraph}");
        return (
            double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // ==========================================================================================
    // Execution harness
    // ==========================================================================================

    /// <summary>
    /// Runs one full compile for <paramref name="artifact"/>/<paramref name="decisionJson"/> and
    /// records the EDL and every ffmpeg invocation into <paramref name="capture"/>.
    /// </summary>
    private static async Task RunCompileAsync(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string manifestJson,
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configOverride,
        RunResult capture,
        bool manifestAvailable = true,
        // Wave C (appended, defaulted): the dailies room's TakeSelectionOutput, served as history
        // step TakeSelectionStepOrder, and a hook for per-test workspace setups (e.g. a generated
        // clip key under THIS project's own generated prefix).
        string? takeSelectionJson = null,
        Action<Mock<IProjectFileWorkspace>>? configureWorkspace = null)
    {
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, manifestJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride, manifestAvailable, takeSelectionJson);
        configureWorkspace?.Invoke(workspace);

        // Re-configured AFTER CreateContext's own upload setup (Moq resolves overlapping setups to
        // the most recently configured one) so the EDL's bytes can be captured while the temp file
        // still exists. The EDL is uploaded twice — the second write carries timelineStorageKey —
        // and the last write wins, which is the complete document.
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                if (path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                {
                    capture.EdlJson = File.ReadAllText(path);
                }
            })
            .ReturnsAsync(EdlKey);
        workspace.SetupRegisteredRenderPublish();

        List<List<string>> invocations = new();
        string? graph = null;

        // The drawtext/amix/perspective/color-grade availability probes are process-lifetime static
        // caches inside the executor; reset them so each test's own mocked runner is consulted.
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... drawtext perspective alphamerge colorbalance colorlevels hue eq ...", string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, ProbeJson, string.Empty, false));
        // Catch-alls, set up BEFORE the narrower "filter=amix" setup below (Moq resolves
        // overlapping setups to the most recently configured one).
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => Capture(args, invocations))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>(
                (args, _, _, _) => Capture(args, invocations))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... normalize ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

        // The filtergraph lives in the step's scratch space, which is disposed before ExecuteAsync
        // returns — so it has to be read back off disk HERE, while ffmpeg is being invoked.
        void Capture(IReadOnlyList<string> args, List<List<string>> sink)
        {
            List<string> list = args.ToList();
            sink.Add(list);

            int scriptIndex = list.IndexOf("-filter_complex_script");
            if (graph is null && scriptIndex >= 0)
            {
                graph = File.ReadAllText(list[scriptIndex + 1]);
            }
        }

        string scratchRoot = Path.Combine(Path.GetTempPath(), ProjectFileStorageRoot, Guid.NewGuid().ToString("N"));

        var executor = new VideoCompileStepExecutor(
            toolRunner.Object,
            mediaProbe.Object,
            workspace.Object,
            new Mock<IServiceScopeFactory>().Object,
            Options.Create(new VideoEditingOptions
            {
                ScratchPath = scratchRoot,
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 30,
                CompileTimeoutSeconds = 30
            }),
            NullLogger<VideoCompileStepExecutor>.Instance);

        capture.Result = await executor.ExecuteAsync(context);
        capture.FfmpegInvocations.AddRange(invocations);
        capture.FilterGraph = graph;
        capture.ScratchRoot = scratchRoot;
    }

    private const string ProbeJson = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "r_frame_rate": "30/1", "duration": "10" } ], "format": { "duration": "10" } }
        """;

    /// <summary>
    /// Builds the full step context: the analysis artifact, the editorial decision, the step's own
    /// config, and a <see cref="IProjectFileWorkspace"/> that serves every storage key this compile
    /// touches from in-test strings.
    /// </summary>
    private static StepExecutionContext CreateContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string manifestJson,
        out Mock<IProjectFileWorkspace> workspace,
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configOverride,
        bool manifestAvailable = true,
        string? takeSelectionJson = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), ManifestKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, manifestJson);
                return Task.CompletedTask;
            });

        // Every generated clip's own storage key: the content is irrelevant, only the two-stage
        // download -> normalize -> probe path matters.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(k => k.StartsWith("projects/p/agentFiles/generated/", StringComparison.Ordinal)),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, [0x0A, 0x0B, 0x0C]);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(EdlKey);
        workspace.SetupRegisteredRenderPublish();

        workspace
            .Setup(w => w.UploadBinaryFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(),
                It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string _, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                CancellationToken _, string category, string? _) =>
                // Deterministic storage key AND id: two runs of the same compile must be
                // comparable string-for-string, and a random Guid here would make them differ.
                new ProjectWorkspaceFile(
                    Guid.Parse("00000000-0000-0000-0000-0000000000aa"), projectId, fileName, null, category,
                    $"projects/{projectId}/{category}/{fileName}", mime, 1024, DateTime.UnixEpoch, null));
        workspace.SetupRegisteredRenderPublish();

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1);
        config = configOverride(config);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = CompileStepId,
            StepOrder = CompileStepOrder,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        // VideoCompileStepExecutor resolves the source video by reading the referenced VideoAnalyze
        // step's OWN VideoAnalyzeConfigJson.Source (never a StepOutputHistory heuristic), so a real
        // analyze step matching AnalysisStepOrder must be present in AllSteps.
        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new(ManifestStepOrder, "VideoGenerate", "{}", OutputStorageKey: null,
                ArtifactStorageKey: manifestAvailable ? ManifestKey : null)
        ];
        if (takeSelectionJson is not null)
        {
            history.Add(new StepOutputHistoryEntry(TakeSelectionStepOrder, "DailiesRoom", takeSelectionJson, OutputStorageKey: null, ArtifactStorageKey: null));
        }

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = history.Count,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }
}
