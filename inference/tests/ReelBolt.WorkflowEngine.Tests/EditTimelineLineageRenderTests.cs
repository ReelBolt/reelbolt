#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-102: the lineage-seeded document of the prod-shaped chain (04ef3b0a configs), rendered by TimelineRender with real
/// ffmpeg, against the chain's compile output built with the engine's own filter graphs on the same tiny source:
/// step 2 = the engine's RegionEffect Blur stage (mask feather 8), step 4 = the engine's Pad + blurred-fill graph
/// (scale/crop/gblur 40/eq -0.08 under the contain-fit foreground).
/// Step 2's censor (box tracker) cannot be reproduced without the tracker, so the render fixture drops censorLabels
/// from the config; its numbers are pinned in LineageSeederTests instead.
/// </summary>
public class EditTimelineLineageRenderTests(ITestOutputHelper output) : IDisposable
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Render2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Render4 = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Analysis = Guid.Parse("11111111-1111-4111-8111-111111111111");

    // Tiny fixture: 640x360 source, 9:16 canvas 360x640. Mask box (source px).
    private const int SrcW = 640, SrcH = 360, CanvasW = 360, CanvasH = 640, Frames = 90;
    private const int MaskX = 200, MaskY = 120, MaskW = 120, MaskH = 80;

    private readonly string _work = Path.Combine(Path.GetTempPath(), "lineage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private static List<LineageLevel> Chain(bool dropCensor)
    {
        string step2 = ProdRun04ef3b0aConfigs.Step2Compile;
        if (dropCensor)
        {
            step2 = Regex.Replace(step2, "\"censorLabels\": \\[[^\\]]*\\]", "\"censorLabels\": []");
        }

        TimelineManifest m2 = new(1, Guid.NewGuid(), Guid.NewGuid(), new TimelineOutputVideo(SrcW, SrcH, 30, 3, "render2.mp4"),
            [new TimelineSource(0, "src.mp4", 3, SrcW, SrcH, 30, ProjectFileId: FileId)],
            [new TimelineTrack("v", "video", "Video", 0, [new TimelineItem(Id: "c1", TimelineStartSec: 0, TimelineEndSec: 3, SourceIndex: 0, SourceStartSec: 0, SourceEndSec: 3, Label: "Whole")])]);
        TimelineManifest m4 = new(1, m2.ProjectId, m2.ExecutionId, new TimelineOutputVideo(CanvasW, CanvasH, 30, 3, "out4.mp4"),
            [new TimelineSource(0, "render2.mp4", 3, SrcW, SrcH, 30, StepResultId: Render2)],
            [new TimelineTrack("v", "video", "Video", 0, [new TimelineItem(Id: "c1", TimelineStartSec: 0, TimelineEndSec: 3, SourceIndex: 0, SourceStartSec: 0, SourceEndSec: 3, Label: "Whole")])]);
        return
        [
            new LineageLevel(Render4, m4, JsonSerializer.Deserialize<VideoCompileStepConfig>(ProdRun04ef3b0aConfigs.Step4Compile, ConfigJson)),
            new LineageLevel(Render2, m2, JsonSerializer.Deserialize<VideoCompileStepConfig>(step2, ConfigJson), Analysis,
                [new LineageSegmentationTrack("g1", 0, "digital speedometer", new SegmentationTrackFacts("g1", new AssetRef(AssetRefKind.ProjectFile, FileId), false, 3, null))])
        ];
    }

    private static EditTimeline Seed(bool dropCensor = true) =>
        LineageSeeder.TrySeed(Chain(dropCensor), Guid.NewGuid(), s => new AssetRef(AssetRefKind.ProjectFile, s.ProjectFileId), _ => false)!.Document;

    [FfmpegFact]
    public void The_seeded_document_renders_like_the_chains_compile_output()
    {
        Directory.CreateDirectory(_work);
        string src = P("src.mp4"), mask = P("mask.mkv"), r2 = P("render2.mkv"), ref4 = P("ref4.mkv"), bgOnly = P("bg.mkv");

        Ff("-f", "lavfi", "-i", $"testsrc2=s={SrcW}x{SrcH}:r=30:d=3", "-c:v", "libx264", "-crf", "10", "-pix_fmt", "yuv420p", src);
        Ff("-f", "lavfi", "-i", $"color=c=black:s={SrcW}x{SrcH}:r=30:d=3,drawbox=x={MaskX}:y={MaskY}:w={MaskW}:h={MaskH}:color=white:t=fill,format=gray",
            "-c:v", "ffv1", "-pix_fmt", "gray", mask);

        // Reference, step 2: the engine's own RegionEffect Blur stage (strength 1, feather 8).
        string helper = RegionEffectFilterBuilder.BuildMaskHelperChain("[1:v]", "[m]", false, 8, 1.0);
        string effect = RegionEffectFilterBuilder.BuildEffectChain("[0:v]", "[m]", "[v2]", 0, RegionEffect.Blur, SrcW, SrcH, 1.0, "30", 3);
        Ff("-i", src, "-i", mask, "-filter_complex", helper + ";" + effect, "-map", "[v2]", "-c:v", "ffv1", "-pix_fmt", "yuv420p", r2);

        // Reference, step 4: the engine's Pad + blurred-fill graph.
        string graph = $"[0:v]split[fg][bg];[bg]scale={CanvasW}:{CanvasH}:force_original_aspect_ratio=increase,crop={CanvasW}:{CanvasH},gblur=sigma=40,eq=brightness=-0.08[bgb];" +
                       $"[fg]scale={CanvasW}:{CanvasH}:force_original_aspect_ratio=decrease[fgs];[bgb][fgs]overlay=(W-w)/2:(H-h)/2,setsar=1[v]";
        Ff("-i", r2, "-filter_complex", graph, "-map", "[v]", "-c:v", "ffv1", "-pix_fmt", "yuv420p", ref4);
        Ff("-i", r2, "-filter_complex", $"[0:v]scale={CanvasW}:{CanvasH}:force_original_aspect_ratio=increase,crop={CanvasW}:{CanvasH},gblur=sigma=40,eq=brightness=-0.08[bgb]",
            "-map", "[bgb]", "-c:v", "ffv1", "-pix_fmt", "yuv420p", bgOnly);

        EditTimeline doc = Seed();
        EditTimelineValidator.Validate(doc, new EditTimelineValidationContext(
            doc.ProjectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [FileId] = doc.ProjectId }, new Dictionary<Guid, Guid>(),
            FramesPerSecond: 30,
            SegmentationTrackIds: new Dictionary<Guid, IReadOnlySet<string>> { [Analysis] = new HashSet<string> { "g1" } },
            SegmentationTrackFacts: new Dictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>> { [Analysis] = new Dictionary<string, SegmentationTrackFacts> { ["g1"] = new SegmentationTrackFacts("g1", new AssetRef(AssetRefKind.ProjectFile, FileId), false, 3, null) } })).IsValid.Should().BeTrue();

        string full = RenderDoc(doc, "full.mp4", mask, src);
        string fillOnly = RenderDoc(WithTracks(doc, t => t.Name.StartsWith("Canvas fill", StringComparison.Ordinal)), "fill.mp4", mask, src);
        string noRegion = RenderDoc(WithTracks(doc, t => !t.Name.StartsWith("Blur:", StringComparison.Ordinal)), "noregion.mp4", mask, src);

        // The mask box in canvas px: contain-fit scale 0.5625, centred vertically.
        double s = CanvasW / (double)SrcW;
        double oy = (CanvasH - (SrcH * s)) / 2.0;
        string regionCrop = Crop(Math.Max(0, MaskX * s - 16), Math.Max(0, oy + (MaskY * s) - 16), (MaskW * s) + 32, (MaskH * s) + 32);
        string fgCrop = Crop(0, oy, CanvasW, SrcH * s);

        double total = Ssim(full, ref4, null);
        double fill = Ssim(fillOnly, bgOnly, null);
        double fg = Ssim(full, ref4, fgCrop);
        double region = Ssim(full, ref4, regionCrop);
        double regionWithout = Ssim(noRegion, ref4, regionCrop);
        string line = string.Create(CultureInfo.InvariantCulture,
            $"SSIM total={total:F4} fillLayer(vs engine bg)={fill:F4} foreground={fg:F4} regionBox={region:F4} regionBoxWithoutRegionLayer={regionWithout:F4}");
        output.WriteLine(line);

        // Thresholds (measured 2026-10-28 on this fixture: total 0.955, fill 0.948, foreground 0.972, region box 0.965,
        // region box without the region layer 0.904), each set about 0.015 under the measurement. Why not higher: the master
        // encode is lossy h264 against an FFV1 reference, and the editor has no luma-only brightness, so the fill's dim
        // (eq=brightness -0.08 in the engine) goes through the grade's RGB lut; measured in isolation that path caps the
        // fill at about 0.968 whatever the offset, while crop-then-scale geometry + one blur of amount 2 (sigma 40) equal the
        // engine's sigma 40 (a single sigma-20 blur, the old maximum, scored 0.933). The region layer is judged by the gain it adds.
        total.Should().BeGreaterThanOrEqualTo(0.94, line);
        fill.Should().BeGreaterThanOrEqualTo(0.93, line);
        fg.Should().BeGreaterThanOrEqualTo(0.955, line);
        region.Should().BeGreaterThanOrEqualTo(0.95, line);
        region.Should().BeGreaterThan(regionWithout + 0.03, "the segmentation-masked blur layer must move the render toward the compile output: " + line);
    }

    private static EditTimeline WithTracks(EditTimeline doc, Func<Track, bool> keep) =>
        new(doc.Id, doc.ProjectId, doc.Name, doc.Settings, doc.Tracks.Where(keep).ToList(), doc.Markers, null, null, null);

    private string RenderDoc(EditTimeline doc, string name, string maskPath, string srcPath)
    {
        AssetRef asset = new(AssetRefKind.ProjectFile, FileId, null);
        Dictionary<AssetRef, LocalAsset> assets = new() { [asset] = new LocalAsset(srcPath, "src|1", new MediaProbeResult(3, 30, 1, SrcW, SrcH, "h264", null, null)) };
        Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks = new() { [new SegmentationMaskRef(Analysis, "g1")] = new LocalSegmentationMask(maskPath, "x") };
        ResolvedRenderTarget target = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), doc.Settings);
        var plan = SegmentPlanner.Plan(doc, null);
        plan.Regions.Should().ContainSingle("no transition, so one region");
        string outPath = P(name);
        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            plan.Regions[0], target, assets, outPath, "/nonexistent.ttf", n => P($"t{n}.txt"), 2, segmentationMasks: masks);
        if (argv.GraphFile is { } graphFile) File.WriteAllText(graphFile.Path, graphFile.Contents);
        Run("ffmpeg", argv.Args);
        return outPath;
    }

    private static string Crop(double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"crop={(int)Math.Round(w) / 2 * 2}:{(int)Math.Round(h) / 2 * 2}:{(int)Math.Round(x) / 2 * 2}:{(int)Math.Round(y) / 2 * 2}");

    private double Ssim(string a, string b, string? crop)
    {
        string c = crop is null ? "null" : crop;
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-i", a, "-i", b, "-lavfi", $"[0:v]{c},format=yuv420p[a];[1:v]{c},format=yuv420p[b];[a][b]ssim", "-f", "null", "-" }) psi.ArgumentList.Add(arg);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Match m = Regex.Match(err, @"SSIM .*All:([0-9.]+)");
        m.Success.Should().BeTrue(err);
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private string P(string name) => Path.Combine(_work, name);

    private static void Ff(params string[] args) => Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", .. args]);

    private static void Run(string exe, IReadOnlyList<string> args)
    {
        ProcessStartInfo psi = new(exe) { RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, error);
    }
}
