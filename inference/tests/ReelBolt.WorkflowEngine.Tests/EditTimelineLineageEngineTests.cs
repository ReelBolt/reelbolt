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
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-105: a MULTI-CUT chain whose references come from the REAL <see cref="VideoCompileStepExecutor"/> with real ffmpeg:
/// compile 1 keeps two of three shots of a tiny source (a real cut list, a real transcript track in its manifest),
/// compile 2 fits that output to a 9:16 canvas with Pad + canvasFill Blur. The seeder is fed the manifests the engine
/// itself wrote, and the seeded document is rendered by TimelineRender and compared with compile 2's real output.
/// The engine has no retime/speed (cuts are 1:1 trims), so no speed-changed chain can be produced by it; a retimed cut
/// is covered by LineageSeederTests (it falls back).
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class EditTimelineLineageEngineTests(ITestOutputHelper output)
{
    private static readonly Guid ProjectId = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Result1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Result2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string SourceKey = "projects/p/userFiles/shoot.mp4";
    private const string Render1Key = "projects/p/outputFiles/e/render1.mp4";

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Compiled(string OutputPath, TimelineManifest Manifest, VideoCompileStepConfig Config);

    [FfmpegFact]
    public async Task The_seeded_multi_cut_chain_renders_like_the_real_engines_output()
    {
        string root = Path.Combine(Path.GetTempPath(), "lineage-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            string source = Path.Combine(root, "shoot.mp4");
            Ff("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", "-c:a", "aac", "-shortest", source);

            // Compile 1: keep shots s0 (0-2 s) and s2 (4-6 s) of the three; a transcript segment per shot.
            VideoAnalysisArtifact analysis1 = new(
                Version: 1, Media: new VideoAnalysisMedia(6.0, 30, 1, 320, 180),
                Shots: [new VideoAnalysisShot("s0", 0, 2), new VideoAnalysisShot("s1", 2, 4), new VideoAnalysisShot("s2", 4, 6)],
                SilenceSpans: [],
                Segments: [new VideoAnalysisSegment("t0", "s0", 0, 2, "first words"), new VideoAnalysisSegment("t1", "s1", 2, 4, "dropped words"), new VideoAnalysisSegment("t2", "s2", 4, 6, "last words")],
                Words: [], OfferedIds: ["s0", "s1", "s2"],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Placements: null, OfferedPlacementIds: null);
            string decision1 = JsonSerializer.Serialize(new
            {
                keep = new[] { new { fromId = "s0", toId = "s0", reason = "intro" }, new { fromId = "s2", toId = "s2", reason = "outro" } },
                editRationale = "two shots",
                suggestedTitle = "T"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            VideoCompileStepConfig config1 = new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1)
            { RegisterProjectFile = false, LoudnessTarget = ProgramLoudness.Off };
            Compiled one = await CompileAsync(root, "c1", config1, analysis1, decision1, source, SourceKey);

            // Compile 2: the step-1 output, whole, to a vertical 9:16 canvas, Pad + canvasFill Blur.
            double len1 = one.Manifest.OutputVideo.DurationSec;
            VideoAnalysisArtifact analysis2 = new(
                Version: 1, Media: new VideoAnalysisMedia(len1, 30, 1, 320, 180),
                Shots: [new VideoAnalysisShot("s0", 0, len1)], SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Placements: null, OfferedPlacementIds: null);
            VideoCompileStepConfig config2 = new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)
            {
                RegisterProjectFile = false,
                LoudnessTarget = ProgramLoudness.Off,
                KeepWholeSources = true,
                OutputFormat = VideoOutputFormat.Vertical,
                OutputFit = VideoOutputFit.Pad,
                CanvasFill = VideoCanvasFill.Blur
            };
            Compiled two = await CompileAsync(root, "c2", config2, analysis2, "{}", one.OutputPath, Render1Key);

            // Both manifests are what the engine wrote. Name the sources by identity as the resolver does.
            TimelineManifest m1 = one.Manifest with { Sources = one.Manifest.Sources.Select(s => s with { ProjectFileId = FileId }).ToList() };
            TimelineManifest m2 = two.Manifest with { Sources = two.Manifest.Sources.Select(s => s with { StepResultId = Result1 }).ToList() };
            m1.Tracks.Should().Contain(t => t.Type == "dialogue", "the engine really writes the transcript track for compile 1");
            m1.Tracks.First(t => t.Type == "video").Items.Should().HaveCount(2);

            List<LineageLevel> levels = [new(Result2, m2, two.Config), new(Result1, m1, one.Config)];
            SeededTimeline seeded = LineageSeeder.TrySeed(levels, Guid.NewGuid(), s => s.ProjectFileId is { } id ? new AssetRef(AssetRefKind.ProjectFile, id) : null, _ => false)!;
            EditTimeline doc = seeded.Document;
            seeded.Unplaced.Should().BeEmpty("the whole chain is representable: the inner transcript is metadata");
            doc.Markers.Should().BeEmpty();
            doc.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().Should().HaveCount(2).And.OnlyContain(c => c.AssetRef.ProjectFileId == FileId);

            string full = RenderDoc(doc, "full.mp4", source, root);
            string shifted = RenderDoc(ShiftMain(doc, 8), "shifted.mp4", source, root);
            double total = Ssim(full, two.OutputPath, null, root);
            double first = Ssim(full, two.OutputPath, "trim=start_frame=3:end_frame=55,setpts=PTS-STARTPTS", root);
            double second = Ssim(full, two.OutputPath, "trim=start_frame=65:end_frame=115,setpts=PTS-STARTPTS", root);
            double shiftedTotal = Ssim(shifted, two.OutputPath, null, root);
            string line = string.Create(CultureInfo.InvariantCulture, $"engine chain SSIM total={total:F4} firstCut={first:F4} secondCut={second:F4} (8-frame time shift: {shiftedTotal:F4})");
            output.WriteLine(line);

            // Thresholds are derived, not observed: measured total 0.9296, cut 1 0.9295, cut 2 0.9294, 8-frame shift 0.8568 (gap 0.073).
            // Each floor is the measured value minus a 0.02 margin; the shift gap floor is 0.05. The score is not higher because the
            // reference went through TWO h264 generations (compile 1, then compile 2) over a high-frequency testsrc2 picture, while
            // the seeded render goes through one, and its sigma-40 fill is fixed against the engine's.
            total.Should().BeGreaterThanOrEqualTo(0.91, line);
            first.Should().BeGreaterThanOrEqualTo(0.91, line);
            second.Should().BeGreaterThanOrEqualTo(0.91, line);
            total.Should().BeGreaterThan(shiftedTotal + 0.05, "an 8-frame misplacement of the cuts must score visibly worse: " + line);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static EditTimeline ShiftMain(EditTimeline doc, long frames) => new(doc.Id, doc.ProjectId, doc.Name, doc.Settings,
        doc.Tracks.Select(t => t.Name != "Video" ? t : t with
        {
            Items = t.Items.OfType<MediaClip>().Select(c => new MediaClip(c.Id, c.Name, c.RecordIn, c.RecordOut, c.AssetRef, c.SourceIn + frames, c.PlaybackRate, c.Enabled, c.Provenance, null, c.Transform, c.Crop)).Cast<Item>().ToList()
        }).ToList(), doc.Markers, null, null, null);

    private async Task<Compiled> CompileAsync(string root, string name, VideoCompileStepConfig config, VideoAnalysisArtifact analysis, string decisionJson, string input, string inputKey)
    {
        string analysisKey = $"projects/p/agentFiles/video-analysis/e/{name}-analysis.json";
        string work = Path.Combine(root, name);
        Directory.CreateDirectory(work);
        var options = Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(work, "scratch"), MaxConcurrentJobs = 1, AnalyzeTimeoutSeconds = 120, CompileTimeoutSeconds = 300 });
        var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);
        string manifestCopy = Path.Combine(work, "manifest.json");
        var files = new Mock<IProjectFileWorkspace>();
        files.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
            {
                if (key == analysisKey) File.WriteAllText(destination, JsonSerializer.Serialize(analysis, JsonOptions()));
                else File.Copy(input, destination, overwrite: true);
                return Task.CompletedTask;
            });
        files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid pid, string path, string fileName, string _, CancellationToken _, string category) =>
            {
                if (fileName.EndsWith("-timeline.json", StringComparison.Ordinal) && File.Exists(path)) File.Copy(path, manifestCopy, overwrite: true);
                return "projects/" + pid + "/" + category + "/" + fileName;
            });
        var recorder = new VideoCompileRealFfmpegWorkspaceTests.RecordingRunner(real, Path.Combine(work, "preserved"));
        var analyzeStep = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), JsonOptions()) };
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions()) };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: inputKey, ArtifactStorageKey: null),
                new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: analysisKey),
                new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
            ],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
        var executor = new VideoCompileStepExecutor(recorder, probe, files.Object, new Mock<IServiceScopeFactory>().Object, options, NullLogger<VideoCompileStepExecutor>.Instance);
        StepExecutionResult result = await executor.ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string produced = Path.Combine(work, "preserved", "edited.mp4");
        File.Exists(produced).Should().BeTrue();
        File.Exists(manifestCopy).Should().BeTrue("the engine wrote its timeline manifest");
        TimelineManifest manifest = JsonSerializer.Deserialize<TimelineManifest>(File.ReadAllText(manifestCopy), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return new Compiled(produced, manifest, config);
    }

    private string RenderDoc(EditTimeline doc, string name, string sourcePath, string root)
    {
        AssetRef asset = new(AssetRefKind.ProjectFile, FileId, null);
        Dictionary<AssetRef, LocalAsset> assets = new() { [asset] = new LocalAsset(sourcePath, "src|1", new MediaProbeResult(6, 30, 1, 320, 180, "h264", "aac", 48000)) };
        ResolvedRenderTarget target = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), doc.Settings);
        var plan = SegmentPlanner.Plan(doc, null);
        string outPath = Path.Combine(root, name);
        List<string> parts = [];
        for (int r = 0; r < plan.Regions.Count; r++)
        {
            string part = Path.Combine(root, $"{name}.r{r}.mp4");
            RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(plan.Regions[r], target, assets, part, "/nonexistent.ttf", n => Path.Combine(root, $"t{r}-{n}.txt"), 2);
            if (argv.GraphFile is { } g) File.WriteAllText(g.Path, g.Contents);
            Run(argv.Args.ToArray());
            parts.Add(part);
        }

        string list = Path.Combine(root, name + ".txt");
        File.WriteAllLines(list, parts.Select(p => $"file '{p}'"));
        Ff("-f", "concat", "-safe", "0", "-i", list, "-c", "copy", outPath);
        return outPath;
    }

    private static double Ssim(string a, string b, string? trim, string root)
    {
        string t = trim is null ? "null" : trim;
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-i", a, "-i", b, "-lavfi", $"[0:v]{t},scale=360:640,format=yuv420p[a];[1:v]{t},scale=360:640,format=yuv420p[b];[a][b]ssim", "-f", "null", "-" }) psi.ArgumentList.Add(arg);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        System.Text.RegularExpressions.Match m = Regex.Match(err, @"SSIM .*All:([0-9.]+)");
        m.Success.Should().BeTrue(err);
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static void Ff(params string[] args) => Run(["-hide_banner", "-loglevel", "error", "-y", .. args]);

    private static void Run(params string[] args)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, error);
    }
}
