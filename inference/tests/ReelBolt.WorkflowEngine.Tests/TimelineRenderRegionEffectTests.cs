#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Interchange;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Magic blur: <c>MediaClip.regionEffects</c> render through their segmentation masks at the clip's source
/// geometry, before its crop/transform/grade. A timeline without region effects is untouched (the golden argv
/// of <see cref="TimelineRenderSegmentationMaskTests"/> pins that).
/// </summary>
public class TimelineRenderRegionEffectTests
{
    /// <summary>The two per-input whitelists every file input carries (security ENG-3), as they read in a joined argv.</summary>
    private const string InputGuard = "-protocol_whitelist file -format_whitelist " + UserMediaInputFormats.Whitelist + " ";

    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Analysis = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static ShapeMask Seg(bool invert = false, double feather = 0, Guid? analysis = null, params string[] tracks) =>
        new(ShapeMask.SegmentationKind, Invert: invert, AnalysisStepResultId: analysis ?? Analysis, TrackIds: tracks.Length == 0 ? ["g1"] : tracks, FeatherPx: feather);

    private static RegionClipEffect Fx(string word = "blur", double strength = 1, ShapeMask? mask = null, bool enabled = true) =>
        new(word, strength, mask ?? Seg(), enabled);

    private static MediaClip Clip(IReadOnlyList<RegionClipEffect>? effects, ShapeMask? mask = null, double rate = 1.0, bool reverse = false, bool hold = false, ClipTransform? transform = null, long recordOut = 30) =>
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "clip", 0, recordOut, AssetA, 30, rate, true, null,
            null, transform, null, reverse, hold, 0, 0, null, mask, RegionEffects: effects);

    private static EditTimeline Doc(params Item[] items) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings,
            [new Track(Guid.Parse("22222222-0000-4000-8000-000000000000"), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items)],
            [], null, null, null);

    private static ResolvedRenderTarget Target() =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static Dictionary<SegmentationMaskRef, LocalSegmentationMask> Masks(string hash1 = "aa", Guid? analysis = null) => new()
    {
        [new SegmentationMaskRef(analysis ?? Analysis, "g1")] = new LocalSegmentationMask("/tmp/g1.mkv", hash1),
        [new SegmentationMaskRef(Analysis, "g2")] = new LocalSegmentationMask("/tmp/g2.mkv", "bb"),
    };

    private static RegionFilterGraphBuilder.RegionArgv Build(EditTimeline doc, IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? masks = null) =>
        RegionFilterGraphBuilder.Build(SegmentPlanner.Plan(doc, null).Regions[0], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf",
            n => $"/scratch/t-{n}.txt", 2, segmentationMasks: masks ?? Masks());

    private static string GraphOf(RegionFilterGraphBuilder.RegionArgv argv) => argv.Args[argv.Args.ToList().IndexOf("-filter_complex") + 1];

    private const string Leg = "trim=duration=1,setpts=PTS-STARTPTS,fps=30/1";

    [Theory]
    [InlineData("blur", RegionEffect.Blur)]
    [InlineData("pixelate", RegionEffect.Pixelate)]
    [InlineData("highlight", RegionEffect.Highlight)]
    [InlineData("spotlight", RegionEffect.Spotlight)]
    [InlineData("colorpop", RegionEffect.ColorPop)]
    [InlineData("outline", RegionEffect.Outline)]
    public void Each_effect_is_the_workflow_builders_chain_through_the_clips_mask_on_the_source_clock(string word, RegionEffect effect)
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(Clip([Fx(word, 0.5)])));
        string graph = GraphOf(argv);
        string expected = RegionEffectFilterBuilder.BuildEffectChain("[l0rp]", "[l0re0mh]", "[l0re0o]", 0, effect, 320, 180, 0.5, "30/1", 1.0);
        graph.Should().Contain($"[0:v]{Leg}[l0rp];[1:v]{Leg},format=gray[l0re0mk0];[l0re0mk0]null[l0re0mh];{expected};");
        Joined(argv).Should().Contain(InputGuard + "-ss 1 -t 1.5 -i /tmp/a.mp4 " + InputGuard + "-ss 1 -t 1.5 -i /tmp/g1.mkv -filter_complex");
        graph.Should().Contain("[l0re0o]null,", "the picture was already decoded; the clip's own chain starts from the effect output");
    }

    private static string Joined(RegionFilterGraphBuilder.RegionArgv argv) => string.Join(" ", argv.Args);

    [Fact]
    public void Invert_and_feather_go_on_the_mask_and_tracks_are_unioned()
    {
        string graph = GraphOf(Build(Doc(Clip([Fx("blur", 1, Seg(invert: true, feather: 4, tracks: ["g1", "g2"]))]))));
        graph.Should().Contain(
            $"[1:v]{Leg},format=gray[l0re0mk0];[2:v]{Leg},format=gray[l0re0mk1];[l0re0mk0][l0re0mk1]blend=all_mode=lighten[l0re0mu1];[l0re0mu1]negate,gblur=sigma=4[l0re0mh];");
    }

    [Fact]
    public void Strength_up_to_two_is_passed_through_unchanged()
    {
        string graph = GraphOf(Build(Doc(Clip([Fx("blur", 2)]))));
        graph.Should().Contain(RegionEffectFilterBuilder.BuildEffectChain("[l0rp]", "[l0re0mh]", "[l0re0o]", 0, RegionEffect.Blur, 320, 180, 2, "30/1", 1.0));
    }

    [Fact]
    public void Speed_reverse_and_hold_apply_to_the_picture_and_the_mask_alike()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(Clip([Fx()], rate: 2.0, reverse: true)));
        string leg = "trim=duration=2,setpts=PTS-STARTPTS,reverse,setpts=PTS-STARTPTS,setpts=PTS/2,fps=30/1";
        GraphOf(argv).Should().Contain($"[0:v]{leg}[l0rp];[1:v]{leg},format=gray[l0re0mk0]");
        Joined(argv).Should().Contain(InputGuard + "-ss 1 -t 2.5 -i /tmp/a.mp4 " + InputGuard + "-ss 1 -t 2.5 -i /tmp/g1.mkv");

        RegionFilterGraphBuilder.RegionArgv held = Build(Doc(Clip([Fx()], hold: true)));
        string hold = "trim=end_frame=1,setpts=PTS-STARTPTS,loop=loop=-1:size=1:start=0,trim=duration=1,setpts=N/(30/1)/TB,fps=30/1";
        GraphOf(held).Should().Contain($"[0:v]{hold}[l0rp];[1:v]{hold},format=gray[l0re0mk0]");
    }

    [Fact]
    public void Several_effects_chain_in_order_with_unique_slots_and_the_transform_comes_after()
    {
        string graph = GraphOf(Build(Doc(Clip([Fx("blur"), Fx("pixelate"), Fx("highlight", enabled: false), Fx("outline")],
            transform: new ClipTransform(Rotation: new Keyframed<double>(10))))));
        graph.IndexOf("[l0re0o]", StringComparison.Ordinal).Should().BeLessThan(graph.IndexOf("[l0re1o]", StringComparison.Ordinal));
        graph.Should().Contain("[l0re1o]").And.Contain("[l0re2o]").And.NotContain("[l0re3o]", "the disabled entry is skipped");
        graph.Should().Contain("ree0b").And.Contain("ree1b").And.Contain("ree2o0");
        graph.IndexOf("[l0re2o]", StringComparison.Ordinal).Should().BeLessThan(graph.IndexOf("rotate=", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clip_with_a_segmentation_mask_and_region_effects_uses_independent_inputs()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(Clip([Fx()], mask: Seg(tracks: "g2"))));
        string graph = GraphOf(argv);
        // input 0 picture, 1 the clip mask (g2), 2 the effect mask (g1)
        Joined(argv).Should().Contain("-i /tmp/a.mp4 " + InputGuard + "-ss 1 -t 1.5 -i /tmp/g2.mkv " + InputGuard + "-ss 1 -t 1.5 -i /tmp/g1.mkv");
        graph.Should().Contain($"[2:v]{Leg},format=gray[l0re0mk0]").And.Contain($"[1:v]{Leg},format=gray[l0mk0]");
        graph.Should().Contain("[l0re0o]null,format=rgba[l0pic];[l0pic][l0mh]alphamerge[l0sm]");
    }

    [Fact]
    public void A_transition_leg_carries_the_effect_like_it_carries_the_matte()
    {
        MediaClip from = Clip([Fx("pixelate")], recordOut: 60);
        Transition transition = new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), "x", 60, 90, "fade");
        MediaClip to = new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "b", 90, 150, AssetA, 0, 1.0, true, null);
        RenderPlan plan = SegmentPlanner.Plan(Doc(from, transition, to), null);
        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            plan.Regions[1], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2, segmentationMasks: Masks());
        Joined(argv).Should().Contain(InputGuard + "-ss 3 -t 1.5 -i /tmp/a.mp4 " + InputGuard + "-ss 0 -t 1.5 -i /tmp/a.mp4 " + InputGuard + "-ss 3 -t 1.5 -i /tmp/g1.mkv");
        string graph = GraphOf(argv);
        graph.Should().Contain("[0:v]").And.Contain("[x0are0o]").And.Contain("[x0are0o]null,").And.Contain("pixelize=");
        graph.Should().Contain("[x0a][x0b]xfade=transition=fade");
    }

    [Fact]
    public void An_unstaged_effect_mask_throws_rather_than_rendering_unaffected()
    {
        Action act = () => Build(Doc(Clip([Fx()])), new Dictionary<SegmentationMaskRef, LocalSegmentationMask>());
        act.Should().Throw<InvalidOperationException>();
    }

    // ── The recipe hash ──────────────────────────────────────────────────────

    private static string Hash(IReadOnlyList<RegionClipEffect>? effects, IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? masks = null)
    {
        RenderRegion region = SegmentPlanner.Plan(Doc(Clip(effects)), null).Regions[0];
        return RegionRecipeHasher.Hash(region, Target(), Assets, "font", null, masks ?? Masks());
    }

    [Fact]
    public void The_hash_covers_every_effect_input_and_ignores_a_disabled_entry()
    {
        RegionRecipeHasher.RendererVersion.Should().Be("timeline-render-region-v7");
        string baseline = Hash([Fx()]);
        Hash([Fx()]).Should().Be(baseline, "deterministic");
        Hash(null).Should().NotBe(baseline, "an effect-only clip is not the identity recipe");
        Hash([Fx("pixelate")]).Should().NotBe(baseline);
        Hash([Fx(strength: 0.5)]).Should().NotBe(baseline);
        Hash([Fx(mask: Seg(invert: true))]).Should().NotBe(baseline);
        Hash([Fx(mask: Seg(feather: 3))]).Should().NotBe(baseline);
        Hash([Fx(mask: Seg(analysis: Guid.NewGuid()))], Masks(analysis: Guid.Empty)).Should().NotBe(baseline);
        Hash([Fx()], Masks(hash1: "cc")).Should().NotBe(baseline, "a re-cut mask re-renders the region");
        Hash([Fx(), Fx("spotlight", enabled: false)]).Should().Be(baseline, "a disabled entry is ignored");
        Hash([Fx(enabled: false)]).Should().Be(Hash(null), "all entries disabled is the same picture as none");
    }

    // ── The resolver ─────────────────────────────────────────────────────────

    private static async System.Threading.Tasks.Task<(TimelineSegmentationMasks.Resolution Result, int Downloads)> ResolveAsync(
        EditTimeline doc, string maskContentHash, int maskWidth, bool knownAnalysis = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "seg-fx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string key = $"projects/{ProjectId:D}/agentFiles/segmentation/m.mkv";
        string artifact = "{\"sources\":[{\"sourceIndex\":0,\"storageKey\":\"x\",\"projectFileId\":\"" + AssetA.ProjectFileId +
            "\"}],\"segmentationTracks\":[{\"id\":\"g1\",\"sourceIndex\":0,\"frameCount\":300,\"fps\":30,\"maskStorageKey\":\"" +
            key + "\",\"maskContentHash\":\"" + maskContentHash + "\"}]}";
        int downloads = 0;
        try
        {
            using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(artifact));
            VideoAnalysisArtifact? parsed = await SegmentationTrackIdLookup.ParseAsync(stream, default);
            TimelineSegmentationMasks.Resolution result = await TimelineSegmentationMasks.ResolveAsync(
                doc, ProjectId, Assets,
                (ids, _) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<(Guid, string?)>>(
                    knownAnalysis ? [(Analysis, $"projects/{ProjectId:D}/agentFiles/video-analysis/a.json")] : []),
                (_, _) => System.Threading.Tasks.Task.FromResult(parsed),
                async (_, local, ct) => { downloads++; await File.WriteAllTextAsync(local, "mask-bytes", ct); },
                (_, _) => System.Threading.Tasks.Task.FromResult(new MediaProbeResult(1, 30, 1, maskWidth, 180, "ffv1", null, null)),
                n => Path.Combine(dir, $"m{n}.mkv"),
                default);
            return (result, downloads);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task The_resolver_verifies_effect_masks_and_refuses_foreign_analysis_hash_and_size()
    {
        string good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("mask-bytes"u8.ToArray()));
        EditTimeline doc = Doc(Clip([Fx()]));
        (await ResolveAsync(doc, good, 320)).Result.Refusal.Should().BeNull();
        (await ResolveAsync(doc, good, 320)).Result.Masks.Should().ContainKey(new SegmentationMaskRef(Analysis, "g1"));
        (await ResolveAsync(doc, new string('0', 64), 320)).Result.Refusal.Should().Contain("hash");
        (await ResolveAsync(doc, good, 640)).Result.Refusal.Should().Contain("640x180");
        (await ResolveAsync(doc, good, 320, knownAnalysis: false)).Result.Refusal.Should().Contain("analysis");
        (await ResolveAsync(Doc(Clip([Fx(mask: Seg(analysis: Guid.NewGuid()))])), good, 320)).Result.Refusal.Should().Contain("analysis");
        (await ResolveAsync(Doc(Clip([Fx(enabled: false, mask: Seg(analysis: Guid.NewGuid()))])), good, 320)).Result.Refusal.Should().BeNull("a disabled entry is not rendered");
    }

    [Fact]
    public async System.Threading.Tasks.Task An_identical_analysis_track_pair_on_the_mask_and_an_effect_is_downloaded_once()
    {
        string good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("mask-bytes"u8.ToArray()));
        (TimelineSegmentationMasks.Resolution result, int downloads) = await ResolveAsync(Doc(Clip([Fx(), Fx("pixelate")], mask: Seg())), good, 320);
        result.Refusal.Should().BeNull();
        downloads.Should().Be(1);
    }

    // ── Interchange ──────────────────────────────────────────────────────────

    [Fact]
    public void Otio_round_trips_region_effects_through_the_reelbolt_document_and_edl_fcpxml_still_write()
    {
        EditTimeline doc = Doc(Clip([Fx("outline", 1.5, Seg(invert: true, feather: 2)), Fx("blur", 0.25, enabled: false)]));
        string otio = TimelineInterchange.WriteOtio(doc);
        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(otio);
        TimelineInterchange.OtioImportResult restored = TimelineInterchange.ReadOtio(
            parsed.RootElement, ProjectId, Guid.NewGuid(), "r", url => TimelineInterchange.TryParseAssetUrl(url));
        restored.Succeeded.Should().BeTrue(string.Join("; ", restored.Errors.Concat(restored.UnresolvedUrls)));
        MediaClip clip = restored.Document!.Tracks[0].Items.OfType<MediaClip>().Single();
        clip.RegionEffects.Should().HaveCount(2);
        clip.RegionEffects![0].Effect.Should().Be("outline");
        clip.RegionEffects[0].Strength.Should().Be(1.5);
        clip.RegionEffects[0].Mask.Invert.Should().BeTrue();
        clip.RegionEffects[0].Mask.FeatherPx.Should().Be(2);
        clip.RegionEffects[0].Mask.AnalysisStepResultId.Should().Be(Analysis);
        clip.RegionEffects[0].Mask.TrackIds.Should().Equal("g1");
        clip.RegionEffects[1].Enabled.Should().BeFalse();

        // EDL and FCPXML carry cuts only: the effect is simply not exported, never an error.
        TimelineInterchange.WriteEdl(doc).Should().NotBeNullOrEmpty();
        FcpxmlWriter.Write(doc).Should().NotContain("regionEffects");
    }
}
