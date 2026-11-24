#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-015: a segmentation mask is drawn by alphamerging the verified FFV1 gray mask onto the clip's own
/// leg (source time, source geometry) before its crop/transform. A timeline with no segmentation mask
/// must produce byte-identical argv (the golden strings below were captured from the pre-change builder).
/// </summary>
public class TimelineRenderSegmentationMaskTests
{
    /// <summary>The two per-input whitelists every file input carries (security ENG-3), as they read in a joined argv.</summary>
    private const string InputGuard = "-protocol_whitelist file -format_whitelist " + UserMediaInputFormats.Whitelist + " ";

    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static MediaClip Clip(ShapeMask? mask = null, double rate = 1.0, bool reverse = false, ClipTransform? transform = null, GradeStack? grade = null) =>
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "clip", 0, 30, AssetA, 30, rate, true, null,
            null, transform, null, reverse, false, 0, 0, grade, mask);

    private static EditTimeline Doc(params Item[] items) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings,
            [new Track(Guid.Parse("22222222-0000-4000-8000-000000000000"), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items)],
            [], null, null, null);

    private static ResolvedRenderTarget Target() =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static RegionFilterGraphBuilder.RegionArgv Build(EditTimeline doc, IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? masks = null) =>
        RegionFilterGraphBuilder.Build(SegmentPlanner.Plan(doc, null).Regions[0], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf",
            n => $"/scratch/t-{n}.txt", 2, lutFilePathFor: n => $"/scratch/lut-{n}.cube", stabilizePathFor: n => $"/scratch/stab-{n}.trf",
            segmentationMasks: masks);

    private static string Joined(RegionFilterGraphBuilder.RegionArgv argv) => string.Join(" ", argv.Args);

    private const string GoldenPlain =
        "-y -nostdin -hide_banner -loglevel error -threads 2 " + InputGuard + "-ss 1 -t 1.5 -i /tmp/a.mp4 -filter_complex color=c=black:s=320x180:r=30/1:d=1[base];[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[l0];[base][l0]overlay=eof_action=pass[v0];[v0]format=yuv420p[vout] -map [vout] -an -c:v libx264 -preset veryfast -crf 10 -g 1 -keyint_min 1 -pix_fmt yuv420p -x264-params scenecut=0:open_gop=0 -r 30/1 -frames:v 30 -movflags +faststart /scratch/r.mp4";

    private const string GoldenDepth =
        "-y -nostdin -hide_banner -loglevel error -threads 2 " + InputGuard + "-ss 1 -t 2.5 -i /tmp/a.mp4 -filter_complex color=c=black:s=320x180:r=30/1:d=1[base];[0:v]trim=duration=2,setpts=PTS-STARTPTS,reverse,setpts=PTS-STARTPTS,setpts=PTS/2,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,format=rgba,rotate=a='(10)*PI/180':ow='rotw((10)*PI/180)':oh='roth((10)*PI/180)':c=0x00000000,setsar=1[l0];[base][l0]overlay=x='(W-w)/2':y='(H-h)/2':eval=frame:format=auto:eof_action=pass[v0];[v0]format=yuv420p[vout] -map [vout] -an -c:v libx264 -preset veryfast -crf 10 -g 1 -keyint_min 1 -pix_fmt yuv420p -x264-params scenecut=0:open_gop=0 -r 30/1 -frames:v 30 -movflags +faststart /scratch/r.mp4";

    private const string GoldenShape =
        "-y -nostdin -hide_banner -loglevel error -threads 2 " + InputGuard + "-ss 1 -t 1.5 -i /tmp/a.mp4 -filter_complex color=c=black:s=320x180:r=30/1:d=1[base];[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,format=rgba,pad=320:180:(ow-iw)/2:(oh-ih)/2:color=0x00000000,setsar=1,format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*(1-clip((1-sqrt(pow((X/W-0.5)/0.2,2)+pow((Y/H-0.5)/0.2,2)))/0.1+0.5,0,1))',format=rgba,format=rgba[l0];[base][l0]overlay=eof_action=pass[v0];[v0]format=yuv420p[vout] -map [vout] -an -c:v libx264 -preset veryfast -crf 10 -g 1 -keyint_min 1 -pix_fmt yuv420p -x264-params scenecut=0:open_gop=0 -r 30/1 -frames:v 30 -movflags +faststart /scratch/r.mp4";

    [Fact]
    public void Golden_argv_without_a_segmentation_mask_is_byte_identical()
    {
        Joined(Build(Doc(Clip()))).Should().Be(GoldenPlain, "a plain P4 clip");
        Joined(Build(Doc(Clip(rate: 2.0, reverse: true, transform: new ClipTransform(Rotation: new Keyframed<double>(10)))))).Should().Be(GoldenDepth, "a P5/P7 depth clip");
        Joined(Build(Doc(Clip(mask: new ShapeMask("ellipse", 0.5, 0.5, 0.4, 0.4, 0.1, true))))).Should().Be(GoldenShape, "a shape mask still goes through geq");
    }

    // ── The matte ────────────────────────────────────────────────────────────

    private static readonly Guid Analysis = Guid.Parse("33333333-3333-4333-8333-333333333333");

    private static Dictionary<SegmentationMaskRef, LocalSegmentationMask> Masks(string hash1 = "aa", string hash2 = "bb") => new()
    {
        [new SegmentationMaskRef(Analysis, "g1")] = new LocalSegmentationMask("/tmp/g1.mkv", hash1),
        [new SegmentationMaskRef(Analysis, "g2")] = new LocalSegmentationMask("/tmp/g2.mkv", hash2),
    };

    private static ShapeMask Seg(bool invert = false, double feather = 0, params string[] tracks) =>
        new(ShapeMask.SegmentationKind, Invert: invert, AnalysisStepResultId: Analysis, TrackIds: tracks.Length == 0 ? ["g1"] : tracks, FeatherPx: feather);

    private static string GraphOf(RegionFilterGraphBuilder.RegionArgv argv) => argv.Args[argv.Args.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void One_track_is_read_on_the_clips_source_clock_and_alphamerged_before_the_clips_own_chain()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(Clip(mask: Seg(), rate: 2.0, reverse: true)), Masks());
        string line = Joined(argv);
        // The mask video takes the SAME seek as the picture (source time, speed, reverse), as input 1.
        line.Should().Contain(InputGuard + "-ss 1 -t 2.5 -i /tmp/a.mp4 " + InputGuard + "-ss 1 -t 2.5 -i /tmp/g1.mkv -filter_complex");
        string leg = "trim=duration=2,setpts=PTS-STARTPTS,reverse,setpts=PTS-STARTPTS,setpts=PTS/2,fps=30/1";
        GraphOf(argv).Should().Contain(
            $"[1:v]{leg},format=gray[l0mk0];[l0mk0]null[l0mh];[0:v]{leg},format=rgba[l0pic];[l0pic][l0mh]alphamerge[l0sm];[l0sm]null,")
            .And.NotContain("geq", "a segmentation mask is not drawn as a shape");
    }

    [Fact]
    public void Several_tracks_are_unioned_then_inverted_then_feathered_on_the_mask_not_the_picture()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(Clip(mask: Seg(invert: true, feather: 4, "g1", "g2"))), Masks());
        string graph = GraphOf(argv);
        string leg = "trim=duration=1,setpts=PTS-STARTPTS,fps=30/1";
        graph.Should().Contain(
            $"[1:v]{leg},format=gray[l0mk0];[2:v]{leg},format=gray[l0mk1];[l0mk0][l0mk1]blend=all_mode=lighten[l0mu1];" +
            "[l0mu1]negate,gblur=sigma=4[l0mh];");
        Joined(argv).Should().Contain("-i /tmp/g1.mkv " + InputGuard + "-ss 1 -t 1.5 -i /tmp/g2.mkv");
        graph.Should().Contain("alphamerge").And.NotContain("maskedmerge");
    }

    [Fact]
    public void A_clip_crop_and_transform_come_after_the_matte_so_the_matte_follows_the_picture()
    {
        string graph = GraphOf(Build(Doc(Clip(mask: Seg(), transform: new ClipTransform(Rotation: new Keyframed<double>(10)))), Masks()));
        graph.IndexOf("alphamerge", StringComparison.Ordinal).Should().BeLessThan(graph.IndexOf("rotate=", StringComparison.Ordinal));
        graph.Should().Contain("rotate=").And.Contain("[l0];[base][l0]overlay=");
    }

    [Fact]
    public void An_unstaged_mask_track_throws_rather_than_rendering_unmasked()
    {
        Action act = () => Build(Doc(Clip(mask: Seg())), new Dictionary<SegmentationMaskRef, LocalSegmentationMask>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_region_hash_carries_the_mask_content_hash_invert_and_feather_and_the_renderer_version_moved()
    {
        RegionRecipeHasher.RendererVersion.Should().Be("timeline-render-region-v7");
        string Hash(ShapeMask? mask, IReadOnlyDictionary<SegmentationMaskRef, LocalSegmentationMask>? masks)
        {
            RenderRegion region = SegmentPlanner.Plan(Doc(Clip(mask: mask)), null).Regions[0];
            return RegionRecipeHasher.Hash(region, Target(), Assets, "font", null, masks);
        }

        string baseline = Hash(Seg(), Masks());
        Hash(Seg(), Masks()).Should().Be(baseline, "deterministic");
        Hash(Seg(), Masks(hash1: "cc")).Should().NotBe(baseline, "a re-cut mask re-renders the region");
        Hash(Seg(invert: true), Masks()).Should().NotBe(baseline);
        Hash(Seg(feather: 3), Masks()).Should().NotBe(baseline);
        Hash(Seg(tracks: ["g1", "g2"]), Masks()).Should().NotBe(baseline);
        Hash(null, null).Should().NotBe(baseline);
    }

    // ── Binding to the clip (T-103) ──────────────────────────────────────────

    private static SegmentationTrackFacts Facts(AssetRef? asset = null, bool trimmed = false, double coverage = 10, string? geometry = "upright-1") =>
        new("g1", asset ?? AssetA, trimmed, coverage, geometry);

    [Fact]
    public void Binding_accepts_the_clips_own_source_untrimmed_and_covering_its_range()
    {
        // The clip plays source 1.0 s .. 2.0 s.
        SegmentationMaskBinding.Check(Clip(), 30, Facts()).Should().BeNull();
        SegmentationMaskBinding.Check(Clip(), 30, Facts(coverage: 2.0)).Should().BeNull("exactly covering is fine");
    }

    [Fact]
    public void Binding_refuses_another_source_a_trimmed_analysis_and_a_mask_that_ends_before_the_clip()
    {
        AssetRef other = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        SegmentationMaskBinding.Check(Clip(), 30, Facts(asset: other)).Should().Contain("different source");
        SegmentationMaskBinding.Check(Clip(), 30, new SegmentationTrackFacts("g1", null, false, 10, "upright-1")).Should().Contain("different source");
        SegmentationMaskBinding.Check(Clip(), 30, Facts(trimmed: true)).Should().Contain("only part");
        SegmentationMaskBinding.Check(Clip(), 30, Facts(coverage: 1.5)).Should().Contain("ends before");
        SegmentationMaskBinding.Check(Clip(rate: 2.0), 30, Facts(coverage: 2.5)).Should().Contain("ends before", "a 2x clip plays 2 s of source");
    }

    [Fact]
    public void Binding_applies_the_776_upright_rule_only_when_the_source_alters_pixels()
    {
        SegmentationMaskBinding.Check(Clip(), 30, Facts(geometry: null), sourceAltersPixelsOnDecode: false).Should().BeNull();
        SegmentationMaskBinding.Check(Clip(), 30, Facts(geometry: null), sourceAltersPixelsOnDecode: true).Should().Contain("rotated or mirrored");
        SegmentationMaskBinding.Check(Clip(), 30, Facts(), sourceAltersPixelsOnDecode: true).Should().BeNull();
    }

    private static EditTimelineValidationContext Context(SegmentationTrackFacts facts, Guid project) => new(
        project, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [AssetA.ProjectFileId!.Value] = project }, new Dictionary<Guid, Guid>(),
        FramesPerSecond: 30,
        SegmentationTrackIds: new Dictionary<Guid, IReadOnlySet<string>> { [Analysis] = new HashSet<string> { "g1" } },
        SegmentationTrackFacts: new Dictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>> { [Analysis] = new Dictionary<string, SegmentationTrackFacts> { ["g1"] = facts } });

    [Fact]
    public void The_validator_gives_the_same_answers_as_one_400_violation()
    {
        AssetRef other = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        EditTimeline doc = Doc(Clip(mask: Seg()));
        EditTimelineValidator.Validate(doc, Context(Facts(), ProjectId)).Violations.Should().BeEmpty();
        foreach (SegmentationTrackFacts bad in new[] { Facts(asset: other), Facts(trimmed: true), Facts(coverage: 1.0) })
        {
            ValidationResult result = EditTimelineValidator.Validate(doc, Context(bad, ProjectId));
            result.Violations.Should().ContainSingle(v => v.Path.EndsWith("mask.trackIds", StringComparison.Ordinal) && v.Message.Contains("does not fit this clip"));
        }
    }

    [Fact]
    public void A_stabilised_clip_cannot_carry_a_segmentation_mask_at_save()
    {
        MediaClip stabilised = new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "clip", 0, 30, AssetA, 30, 1.0, true, null,
            null, null, null, false, false, 0, 0, null, Seg(), null, null, true);
        EditTimelineValidator.Validate(Doc(stabilised), Context(Facts(), ProjectId)).Violations
            .Should().Contain(v => v.Path.EndsWith(".stabilize", StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task An_oversized_analysis_artifact_is_refused_by_the_byte_cap()
    {
        using EndlessStream stream = new();
        Func<System.Threading.Tasks.Task> act = () => SegmentationTrackIdLookup.ParseAsync(stream, default);
        await act.Should().ThrowAsync<InvalidDataException>();
        stream.Served.Should().BeLessThan(SegmentationTrackIdLookup.MaxArtifactBytes + 200_000);
    }

    private sealed class EndlessStream : Stream
    {
        public long Served { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Served += count; Array.Fill(buffer, (byte)' ', offset, count); return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── The resolver's refusals (hash, size, source) ─────────────────────────

    private static async System.Threading.Tasks.Task<TimelineSegmentationMasks.Resolution> ResolveAsync(
        string maskContentHash, int maskWidth, bool sameSource, string maskBytes = "mask-bytes")
    {
        string dir = Path.Combine(Path.GetTempPath(), "seg-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Guid project = ProjectId;
        string key = $"projects/{project:D}/agentFiles/segmentation/m.mkv";
        string artifact = "{\"sources\":[{\"sourceIndex\":0,\"storageKey\":\"x\",\"projectFileId\":\"" +
            (sameSource ? AssetA.ProjectFileId : Guid.NewGuid()) + "\"}],\"segmentationTracks\":[{\"id\":\"g1\",\"sourceIndex\":0,\"frameCount\":300,\"fps\":30,\"maskStorageKey\":\"" +
            key + "\",\"maskContentHash\":\"" + maskContentHash + "\"}]}";
        try
        {
            using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(artifact));
            VideoAnalysisArtifact? parsed = await SegmentationTrackIdLookup.ParseAsync(stream, default);
            return await TimelineSegmentationMasks.ResolveAsync(
                Doc(Clip(mask: Seg())), project, Assets,
                (ids, _) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<(Guid, string?)>>([(Analysis, $"projects/{project:D}/agentFiles/video-analysis/a.json")]),
                (_, _) => System.Threading.Tasks.Task.FromResult(parsed),
                async (_, local, ct) => await File.WriteAllTextAsync(local, maskBytes, ct),
                (_, _) => System.Threading.Tasks.Task.FromResult(new MediaProbeResult(1, 30, 1, maskWidth, 180, "ffv1", null, null)),
                n => Path.Combine(dir, $"m{n}.mkv"),
                default);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task The_resolver_accepts_a_verified_mask_and_refuses_hash_size_and_source_mismatches()
    {
        string good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("mask-bytes"u8.ToArray()));
        (await ResolveAsync(good, 320, true)).Refusal.Should().BeNull();
        (await ResolveAsync(good, 320, true)).Masks.Should().ContainKey(new SegmentationMaskRef(Analysis, "g1"));
        (await ResolveAsync(new string('0', 64), 320, true)).Refusal.Should().Contain("hash");
        (await ResolveAsync(good, 640, true)).Refusal.Should().Contain("640x180");
        (await ResolveAsync(good, 320, false)).Refusal.Should().Contain("different source").And.NotContain("agentFiles");
    }

    // ── Real ffmpeg on the committed mask replay ─────────────────────────────

    private const int ReplayFrames = 120;

    /// <summary>The replayed R1-tabby masks as FFV1 gray plus every decoded frame (index = source frame).</summary>
    private sealed class Replay : IDisposable
    {
        public readonly string Work = Path.Combine(Path.GetTempPath(), "seg-matte-" + Guid.NewGuid().ToString("N"));
        public readonly PromptedRegionReplayFixture Fixture = PromptedRegionReplay.ReadDigests().First(f => f.Name == "R1-tabby");
        public readonly List<byte[]> Masks = [];
        public readonly string MaskPath;
        public int Width => Fixture.Width;
        public int Height => Fixture.Height;

        public Replay()
        {
            Directory.CreateDirectory(Work);
            MaskPath = Path.Combine(Work, "mask.mkv");
            using JsonDocument response = JsonDocument.Parse(File.ReadAllText(Fixture.ResponsePath));
            using Process encoder = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList = { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{Width}x{Height}", "-r", "24", "-i", "-", "-c:v", "ffv1", "-pix_fmt", "gray", MaskPath },
                RedirectStandardInput = true,
            })!;
            foreach (JsonElement frame in response.RootElement.GetProperty("rle").EnumerateArray().Take(ReplayFrames))
            {
                byte[] mask = new byte[Width * Height];
                PromptedRegionRleReference.DecodeInto(frame.GetString(), mask, Width, Height);
                Masks.Add(mask);
                encoder.StandardInput.BaseStream.Write(mask, 0, mask.Length);
            }

            encoder.StandardInput.Close();
            encoder.WaitForExit();
            encoder.ExitCode.Should().Be(0);
        }

        public void Dispose()
        {
            try { Directory.Delete(Work, recursive: true); } catch (IOException) { }
        }
    }

    private const int ClipFrames = 24;

    private static MediaClip ReplayClip(AssetRef source, long sourceIn, double rate, bool reverse, bool invert, long recordIn = 0) =>
        new(Guid.NewGuid(), "cat", recordIn, recordIn + ClipFrames, source, sourceIn, rate, true, null, null, null, null, reverse, false, 0, 0, null,
            new ShapeMask(ShapeMask.SegmentationKind, Invert: invert, AnalysisStepResultId: Analysis, TrackIds: ["g1"]));

    private static byte[] Render(Replay replay, string name, EditTimeline doc, int regionIndex, Dictionary<AssetRef, LocalAsset> assets, EditTimelineSettings settings, int frame)
    {
        string output = Path.Combine(replay.Work, name + ".mp4");
        ResolvedRenderTarget target = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), settings);
        Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks = new() { [new SegmentationMaskRef(Analysis, "g1")] = new LocalSegmentationMask(replay.MaskPath, "x") };
        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            SegmentPlanner.Plan(doc, null).Regions[regionIndex], target, assets, output, "/nonexistent.ttf", n => Path.Combine(replay.Work, $"{name}-t{n}.txt"), 2, segmentationMasks: masks);
        if (argv.GraphFile is { } graphFile) File.WriteAllText(graphFile.Path, graphFile.Contents);
        RunOk("ffmpeg", argv.Args);
        return Frame(output, frame, replay.Width, replay.Height);
    }

    private static (Replay Replay, AssetRef Source, EditTimelineSettings Settings, Dictionary<AssetRef, LocalAsset> Assets) Setup()
    {
        Replay replay = new();
        AssetRef source = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        EditTimelineSettings settings = new(replay.Width, replay.Height, new Rational(24, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
        Dictionary<AssetRef, LocalAsset> assets = new() { [source] = new LocalAsset(replay.Fixture.ClipPath, "cat|1", new MediaProbeResult(5, 24, 1, replay.Width, replay.Height, "h264", null, null)) };
        return (replay, source, settings, assets);
    }

    private static EditTimeline DocOf(EditTimelineSettings settings, params Item[] items) =>
        new(Guid.NewGuid(), ProjectId, "doc", settings, [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items)], [], null, null, null);

    /// <summary>
    /// Proves the matte sits on the RIGHT source frame, not just that there is alpha: finds pixels where the expected
    /// source frames' masks (+-1 frame) are all <paramref name="expectedValue"/> and the wrong frame's mask is the opposite,
    /// then checks the rendered output there (kept = the source's own pixel, dropped = black).
    /// </summary>
    private static void AssertMatteOnSourceFrame(Replay replay, byte[] rendered, int sourceFrame, int wrongFrame, bool invert)
    {
        int w = replay.Width, h = replay.Height;
        byte[] original = Frame(replay.Fixture.ClipPath, sourceFrame, w, h);
        bool Stable(int x, int y, byte value) =>
            new[] { sourceFrame - 1, sourceFrame, sourceFrame + 1 }.All(f => replay.Masks[f][(y * w) + x] == value) && replay.Masks[wrongFrame][(y * w) + x] == (byte)(255 - value);
        (int, int)? kept = null, dropped = null;
        for (int y = 4; y < h - 4 && (kept is null || dropped is null); y += 3)
        {
            for (int x = 4; x < w - 4; x += 3)
            {
                if (kept is null && Stable(x, y, invert ? (byte)0 : (byte)255)) kept = (x, y);
                if (dropped is null && Stable(x, y, invert ? (byte)255 : (byte)0)) dropped = (x, y);
            }
        }

        kept.Should().NotBeNull("the replay must contain pixels that tell the right frame from the wrong one");
        dropped.Should().NotBeNull();
        int Sum(byte[] rgb, (int X, int Y) p) => rgb[((p.Y * w) + p.X) * 3] + rgb[(((p.Y * w) + p.X) * 3) + 1] + rgb[(((p.Y * w) + p.X) * 3) + 2];
        Math.Abs(Sum(rendered, kept!.Value) - Sum(original, kept.Value)).Should().BeLessThan(72, "the matte keeps the source pixel where the RIGHT frame's mask says so");
        Sum(rendered, dropped!.Value).Should().BeLessThan(24, "and drops it where the right frame's mask says so");
        if (!invert) Sum(original, dropped.Value).Should().BeGreaterThan(24);
    }

    [FfmpegFact]
    public void Real_ffmpeg_a_clip_with_a_non_zero_in_point_reads_the_mask_at_the_same_source_time()
    {
        (Replay replay, AssetRef source, EditTimelineSettings settings, Dictionary<AssetRef, LocalAsset> assets) = Setup();
        using (replay)
        {
            byte[] frame = Render(replay, "inpoint", DocOf(settings, ReplayClip(source, 40, 1.0, false, false)), 0, assets, settings, 10);
            AssertMatteOnSourceFrame(replay, frame, 50, 10, invert: false);
        }
    }

    [FfmpegFact]
    public void Real_ffmpeg_a_sped_up_clip_reads_the_mask_at_the_sped_up_source_time()
    {
        (Replay replay, AssetRef source, EditTimelineSettings settings, Dictionary<AssetRef, LocalAsset> assets) = Setup();
        using (replay)
        {
            byte[] frame = Render(replay, "speed", DocOf(settings, ReplayClip(source, 20, 2.0, false, false)), 0, assets, settings, 10);
            AssertMatteOnSourceFrame(replay, frame, 40, 30, invert: false);
        }
    }

    [FfmpegFact]
    public void Real_ffmpeg_a_reversed_clip_reads_the_mask_backwards_with_its_picture()
    {
        (Replay replay, AssetRef source, EditTimelineSettings settings, Dictionary<AssetRef, LocalAsset> assets) = Setup();
        using (replay)
        {
            // Output frame 4 of a reversed 24-frame clip from source frame 30 shows source frame 30 + 23 - 4 = 49.
            byte[] frame = Render(replay, "reverse", DocOf(settings, ReplayClip(source, 30, 1.0, true, false)), 0, assets, settings, 4);
            AssertMatteOnSourceFrame(replay, frame, 49, 34, invert: false);
        }
    }

    [FfmpegFact]
    public void Real_ffmpeg_invert_swaps_which_pixels_are_kept()
    {
        (Replay replay, AssetRef source, EditTimelineSettings settings, Dictionary<AssetRef, LocalAsset> assets) = Setup();
        using (replay)
        {
            byte[] frame = Render(replay, "invert", DocOf(settings, ReplayClip(source, 40, 1.0, false, true)), 0, assets, settings, 10);
            AssertMatteOnSourceFrame(replay, frame, 50, 10, invert: true);
        }
    }

    [Fact]
    public void A_masked_clip_inside_a_transition_carries_the_matte_on_its_leg()
    {
        MediaClip from = new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "a", 0, 60, AssetA, 0, 1.0, true, null, null, null, null, false, false, 0, 0, null, Seg());
        Transition transition = new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), "x", 60, 90, "fade");
        MediaClip to = new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "b", 90, 150, AssetA, 0, 1.0, true, null);
        RenderPlan plan = SegmentPlanner.Plan(Doc(from, transition, to), null);
        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            plan.Regions[1], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2, segmentationMasks: Masks());
        string line = Joined(argv);
        // Inputs: from picture, to picture, then the from-leg's mask video on the SAME span as its picture.
        line.Should().Contain("-i /tmp/a.mp4 " + InputGuard + "-ss 0 -t 1.5 -i /tmp/a.mp4 " + InputGuard + "-ss 2 -t 1.5 -i /tmp/g1.mkv");
        string graph = GraphOf(argv);
        graph.Should().Contain("[2:v]").And.Contain("alphamerge[x0asm]").And.Contain("[x0asm]null,");
        graph.Should().Contain("[x0a][x0b]xfade=transition=fade");
    }

    [FfmpegFact]
    public void Real_ffmpeg_a_masked_clip_inside_a_transition_still_renders_with_its_matte()
    {
        (Replay replay, AssetRef source, EditTimelineSettings settings, Dictionary<AssetRef, LocalAsset> assets) = Setup();
        using (replay)
        {
            MediaClip from = ReplayClip(source, 0, 1.0, false, false);
            Transition transition = new(Guid.NewGuid(), "x", ClipFrames, ClipFrames + 12, "fade");
            MediaClip to = new(Guid.NewGuid(), "b", ClipFrames + 12, ClipFrames + 36, source, 60, 1.0, true, null);
            EditTimeline doc = DocOf(settings, from, transition, to);
            RenderPlan plan = SegmentPlanner.Plan(doc, null);
            int region = plan.Regions.ToList().FindIndex(r => r.Layers.Any(l => l.Item is Transition));
            region.Should().BeGreaterThanOrEqualTo(0);
            // First frame of the span: the outgoing leg is (almost) fully visible, with its matte.
            byte[] frame = Render(replay, "transition", doc, region, assets, settings, 0);
            frame.Length.Should().Be(replay.Width * replay.Height * 3);
            int w = replay.Width;
            byte[] original = Frame(replay.Fixture.ClipPath, ClipFrames, w, replay.Height);
            (int X, int Y)? dropped = null;
            for (int y = 4; y < replay.Height - 4 && dropped is null; y += 3)
                for (int x = 4; x < w - 4 && dropped is null; x += 3)
                    if (new[] { ClipFrames - 1, ClipFrames, ClipFrames + 1 }.All(f => replay.Masks[f][(y * w) + x] == 0)) dropped = (x, y);
            dropped.Should().NotBeNull();
            int i = ((dropped!.Value.Y * w) + dropped.Value.X) * 3;
            (frame[i] + frame[i + 1] + frame[i + 2]).Should().BeLessThan((original[i] + original[i + 1] + original[i + 2]) / 2, "the matte removed the outgoing picture where the mask says so");
        }
    }

    /// <summary>A pixel whose 9x9 neighbourhood all holds <paramref name="wanted"/> (away from any edge).</summary>
    private static (int X, int Y) Find(byte[] mask, int width, int height, byte wanted)
    {
        for (int y = 8; y < height - 8; y += 7)
        {
            for (int x = 8; x < width - 8; x += 7)
            {
                bool all = true;
                for (int dy = -4; dy <= 4 && all; dy++)
                    for (int dx = -4; dx <= 4 && all; dx++)
                        all = mask[((y + dy) * width) + x + dx] == wanted;
                if (all) return (x, y);
            }
        }

        throw new InvalidOperationException("no interior pixel found in the replayed mask");
    }

    private static byte[] Frame(string video, int index, int width, int height)
    {
        using Process p = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-i", video, "-vf", $"select=eq(n\\,{index})", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" },
            RedirectStandardOutput = true,
        })!;
        using MemoryStream ms = new();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        ms.Length.Should().Be(width * height * 3);
        return ms.ToArray();
    }

    private static void RunOk(string exe, IReadOnlyList<string> args)
    {
        ProcessStartInfo psi = new(exe) { RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, error);
    }
}
