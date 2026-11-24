using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Events;

// Runs today's VideoAnalyze anchor detectors (real engine classes) on local clips and writes
// <out>/<clip>.pred.json for score.py. See README.md.
string? videos = null, outDir = null, config = "default", only = null;
bool withBeats = args.Contains("--beats");
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--videos": videos = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--config": config = args[++i]; break;
        case "--clip": only = args[++i]; break;
    }
}
if (videos is null || outDir is null || config is not ("default" or "best" or "events"))
{
    Console.Error.WriteLine("usage: --videos <dir> --out <dir> --config default|best|events [--clip name] [--beats]");
    return 2;
}
Directory.CreateDirectory(outDir);

var options = new VideoEditingOptions();
IOptions<VideoEditingOptions> opt = Options.Create(options);
var runner = new FfmpegVideoToolRunner(opt, NullLogger<FfmpegVideoToolRunner>.Instance);
var probe = new FfprobeMediaProbe(runner, opt, NullLogger<FfprobeMediaProbe>.Instance);
var shotDetector = new FfmpegShotDetector(runner, opt, NullLogger<FfmpegShotDetector>.Instance);
var silenceDetector = new FfmpegSilenceDetector(runner, opt, NullLogger<FfmpegSilenceDetector>.Instance);
var gridSampler = new FfmpegFrameGridSampler(runner, opt, NullLogger<FfmpegFrameGridSampler>.Instance);
var sampler = new FrameSignalSampler(opt, NullLogger<FrameSignalSampler>.Instance);
var eventDetector = new VideoEventDetector(sampler, silenceDetector, opt, NullLogger<VideoEventDetector>.Instance);
var cfg = new Defaults(); // VideoAnalyzeStepConfig positional defaults (record needs required ctor args, so mirrored)
CancellationToken ct = CancellationToken.None;

string scratch = Path.Combine(Path.GetTempPath(), "baseline-runner-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var workspace = LocalMediaWorkspace.CreateAt(scratch, runner);

foreach (string file in Directory.GetFiles(videos, "*.mp4").OrderBy(f => f, StringComparer.Ordinal))
{
    string clip = Path.GetFileNameWithoutExtension(file);
    if (only is not null && clip != only) continue;
    string vpath = clip + ".mp4";
    string link = Path.Combine(scratch, vpath);
    File.CreateSymbolicLink(link, Path.GetFullPath(file));
    try
    {
        MediaProbeResult p = await probe.ProbeAsync(workspace, vpath, ct);
        double fps = p.FpsDen > 0 ? p.FpsNum / (double)p.FpsDen : 30.0;
        if (config == "events")
        {
            // The real layer-1 detector. Beat events are left out unless --beats: the labelled vocabulary has no beat.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            VideoEventSet set = await eventDetector.DetectAsync(workspace, vpath, p,
                new VideoEventOptions { DetectBeats = withBeats }, ct: ct);
            clock.Stop();
            var typed = set.Events
                .Select((e, i) => new { id = "e" + (i + 1), frame = e.Frame, type = SnakeCase(e.Type.ToString()), strength = e.Strength, origin = e.Origin.ToString() })
                .ToList();
            File.WriteAllText(Path.Combine(outDir, clip + ".pred.json"),
                JsonSerializer.Serialize(new
                {
                    clip,
                    config,
                    fps = Math.Round(fps, 4),
                    anchors = typed,
                    suppressed = set.Suppressed,
                    seconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
                    videoSeconds = Math.Round(p.DurationSec, 2)
                },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{clip}: {typed.Count} events in {clock.Elapsed.TotalSeconds:F1}s ({p.DurationSec:F0}s of video)");
            continue;
        }

        var anchors = new List<(double T, string Kind)>();

        IReadOnlyList<(double StartSec, double EndSec)> silences = [];
        if (p.AudioCodec is not null)
            silences = await silenceDetector.DetectAsync(workspace, vpath, cfg.SilenceThresholdDb, cfg.MinSilenceMs / 1000.0, p.DurationSec, ct);

        bool best = config == "best";
        double minShot = Math.Max(0, cfg.MinShotMs) / 1000.0;
        IReadOnlyList<(double StartSec, double EndSec)> shots = best
            ? await SceneCutFinder.DetectAsync(shotDetector, workspace, vpath, SceneDetectionMode.Sensitive,
                cfg.SceneThreshold, p.DurationSec, minShot, options, ct)
            : await shotDetector.DetectShotsAsync(workspace, vpath, cfg.SceneThreshold, p.DurationSec, ct);
        shots = ShotSpanConsolidator.AbsorbTransients(shots, cfg.MinShotMs / 1000.0);

        HashSet<long> contentCuts = [];
        if (best)
        {
            SubShotBounds bounds = SubShotBounds.Clamp(cfg.MinSubShotSec, cfg.MaxSubShotSec, cfg.MaxSubShotsPerShot);
            if (shots.Any(sp => sp.EndSec - sp.StartSec >= 2 * bounds.MinSec))
            {
                int gw = Math.Clamp(cfg.VisualGridWidth, 8, 256), gh = Math.Clamp(cfg.VisualGridHeight, 8, 256);
                var original = shots.Select(s => Key(s.StartSec)).ToHashSet();
                SubShotSignal signal = SubShotSignal.Empty;
                try
                {
                    FrameGridResult grid = await gridSampler.SampleAsync(workspace, vpath, SubShotPlanner.SampleFps, gw, gh,
                        p.DurationSec, cfg.MaxVisualSampleFrames, ct);
                    signal = SubShotSignal.FromGrid(grid, shots, fps);
                }
                catch (Exception ex) { Console.Error.WriteLine($"{clip}: grid sampling failed ({ex.Message}); empty signal"); }
                (int, int)? rate = p.FpsNum > 0 && p.FpsDen > 0 ? (p.FpsNum, p.FpsDen) : null;
                var split = ShotSplitter.SplitByContent(shots, signal, silences, bounds, rate);
                shots = split.Spans;
                foreach (var s in shots) if (!original.Contains(Key(s.StartSec))) contentCuts.Add(Key(s.StartSec));
            }
        }

        // Interior shot boundaries: each boundary once, as the start of the later span.
        foreach (var s in shots.OrderBy(s => s.StartSec))
            if (s.StartSec > 0.0005 && s.StartSec < p.DurationSec - 0.0005)
                anchors.Add((s.StartSec, contentCuts.Contains(Key(s.StartSec)) ? "subShotCut" : "shotStart"));
        foreach (var g in silences)
        {
            if (g.StartSec > 0.0005) anchors.Add((g.StartSec, "silenceStart"));
            if (g.EndSec < p.DurationSec - 0.0005) anchors.Add((g.EndSec, "silenceEnd"));
        }

        if (best)
        {
            VideoToolResult r = await runner.RunFfmpegAsync(
                FfmpegArgvBuilder.BuildMotionArgs(file, options.FfmpegThreads), SourceTimeoutPolicy.Analyze(options), ct);
            if (r.Succeeded)
            {
                var w = MotionSignalParser.BuildWindows(MotionSignalParser.ParseSamples(r.StdOut));
                for (int i = 1; i < w.Count; i++)
                    if (w[i].MotionClass != w[i - 1].MotionClass)
                        anchors.Add((w[i].StartSec, "motionEdge"));
            }
            else Console.Error.WriteLine($"{clip}: motion pass failed");
        }

        long lastFrame = (long)Math.Round(p.DurationSec * fps) - 1;
        var outAnchors = anchors.OrderBy(a => a.T)
            .Select(a => (Frame: (long)Math.Round(a.T * fps), a.T, a.Kind))
            .Where(a => a.Frame > 0 && a.Frame < lastFrame)
            .Select((a, i) => new { id = "s" + (i + 1), frame = a.Frame, kind = a.Kind, timeSec = Math.Round(a.T, 3) })
            .ToList();
        string json = JsonSerializer.Serialize(new { clip, config, fps = Math.Round(fps, 4), anchors = outAnchors },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outDir, clip + ".pred.json"), json);
        Console.WriteLine($"{clip}: {outAnchors.Count} anchors");
    }
    catch (Exception ex) { Console.Error.WriteLine($"{clip}: FAILED ({ex.Message.Split((char)10)[0]}); no prediction written"); }
    finally { File.Delete(link); }
}
try { Directory.Delete(scratch, true); } catch { }
return 0;

static string SnakeCase(string name) =>
    string.Concat(name.Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

static long Key(double sec) => (long)Math.Round(sec * 1000);

// Mirrors VideoAnalyzeStepConfig defaults (inference/src/ReelBolt.Shared/Workflows/VideoAnalyzeStepConfig.cs).
sealed class Defaults
{
    public double SilenceThresholdDb = -34.0;
    public int MinSilenceMs = 350;
    public double SceneThreshold = 0.30;
    public int MinShotMs = 200;
    public double MinSubShotSec = 1.2, MaxSubShotSec = 4.0;
    public int MaxSubShotsPerShot = 8;
    public int VisualGridWidth = 32, VisualGridHeight = 18, MaxVisualSampleFrames = 4000;
}
