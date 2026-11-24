using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Real-footage measurement harness for tracked screen inserts (docs/video-editing.md "Tracking
/// quality on the customer clips"). Not a regression test — it asserts nothing about the numbers —
/// but the instrument every tracker change in this area is judged by. It runs the analyze step's
/// own tracking pass over each clip, then composites a coordinate card into every track with the
/// compile step's own filter builder and REAL ffmpeg, and reports:
///
/// <list type="bullet">
/// <item><b>jitter</b> — per-corner high-frequency residual of the emitted track: each corner's
/// distance from a 9-sample local quadratic through its neighbours, RMS and 95th percentile, in
/// source pixels. A quadratic follows real acceleration, so what is left is shake.</item>
/// <item><b>reference error and slip</b> — every tracked frame's edges re-fitted on the SOURCE
/// frame at native resolution, seeded with the track's own corners: the corner error against that
/// reference, and the slip, its frame-to-frame change — content sliding on the glass, which is what
/// the eye reads as jitter, and which also exposes smoothing lag on fast moves.</item>
/// <item><b>residual green</b> — per output frame, the strict-plate-green pixels left in the
/// composite as a fraction of those in the source frame: mean, 95th percentile, and how many
/// frames show more than 2% (a visible flash) or more than 50% (the plate essentially raw).</item>
/// <item><b>content extent</b> — the card encodes its own x in red and y in blue, so the visible
/// content's range of red/blue says how much of the content's frame survives on the plate: the
/// median percentage lost off each side.</item>
/// </list>
///
/// Runs only when <c>REELBOLT_INSERT_CLIPS</c> names a directory of clips (and ffmpeg is on
/// PATH); <c>REELBOLT_INSERT_CLIP_LIST</c> picks clips (comma-separated, default every .mp4 not
/// starting with <c>composite</c>/<c>ui-</c>) and <c>REELBOLT_INSERT_LABEL</c> names the report,
/// written to <c>report-{label}.txt</c> in the same directory.
/// </summary>
public class InsertTrackingQualityHarness
{
    private readonly ITestOutputHelper _output;

    public InsertTrackingQualityHarness(ITestOutputHelper output) => _output = output;

    [InsertClipsFact]
    public void Measure_tracking_and_composite_quality_on_real_clips()
    {
        string dir = Environment.GetEnvironmentVariable(InsertClipsFactAttribute.DirVariable)!;
        string label = Environment.GetEnvironmentVariable("REELBOLT_INSERT_LABEL") ?? "run";
        string[] clips = (Environment.GetEnvironmentVariable("REELBOLT_INSERT_CLIP_LIST") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (clips.Length == 0)
        {
            clips = Directory.GetFiles(dir, "*.mp4")
                .Select(Path.GetFileName)
                .Where(n => !n!.StartsWith("composite", StringComparison.Ordinal) && !n.StartsWith("ui-", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray()!;
        }

        var report = new StringBuilder();
        report.AppendLine($"# insert tracking quality — {label}");
        foreach (string clip in clips)
        {
            string text = InsertQualityMeasurement.Measure(Path.Combine(dir, clip));
            report.AppendLine(text);
            _output.WriteLine(text);
        }

        File.WriteAllText(Path.Combine(dir, $"report-{label}.txt"), report.ToString());
    }
}

/// <summary>The measurement itself — see <see cref="InsertTrackingQualityHarness"/>.</summary>
internal static class InsertQualityMeasurement
{
    /// <summary>Card values live in 16..239 so the pad's black (and a clipped 0/255) is never read as content.</summary>
    private const int CardLow = 16, CardSpan = 223;

    /// <summary><c>REELBOLT_INSERT_BASELINE=1</c> switches every improvement made with this harness off, reproducing the shipped behaviour it was measured against.</summary>
    private static bool Baseline => Environment.GetEnvironmentVariable("REELBOLT_INSERT_BASELINE") == "1";

    /// <summary><c>REELBOLT_INSERT_CONTENT</c>: composite a real video instead of the coordinate card (for visual evidence; the extent metric then means nothing).</summary>
    private static (string Path, int W, int H)? RealContent =>
        Environment.GetEnvironmentVariable("REELBOLT_INSERT_CONTENT") is { Length: > 0 } p ? (p, Probe(p).W, Probe(p).H) : null;

    /// <summary><c>REELBOLT_INSERT_FIT</c>: how real content is fitted to the plate (default Auto).</summary>
    private static InsertContentFit RealFit =>
        Enum.TryParse(Environment.GetEnvironmentVariable("REELBOLT_INSERT_FIT"), out InsertContentFit f) ? f : InsertContentFit.Auto;

    public static string Measure(string path)
    {
        (int w, int h, double fps, int fpsNum, int fpsDen, double duration) = Probe(path);
        var cfg = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.ProjectFile));
        (int gw, int gh) = ChromaQuadTracker.DeriveGridSize(w, h, cfg.InsertGridWidth, cfg.InsertGridHeight);
        int cap = ChromaQuadTracker.SampleFrameCap(gw, gh, cfg.MaxInsertSampleFrames);
        double sampleFps = ChromaQuadTracker.ResolveSampleFps(cfg.InsertSampleFps, fps, duration, gw, gh, cap);
        byte[] grid = SampleGrid(path, sampleFps, gw, gh);
        int frames = grid.Length / (gw * gh * 3);

        var options = new ChromaQuadTracker.Options(
            ColorName: cfg.InsertRegionColor,
            MinAreaRatio: cfg.MinInsertRegionAreaRatio,
            MinTrackSeconds: cfg.MinInsertRegionSeconds,
            MaxRegions: cfg.MaxInsertRegions,
            MaxPlatesPerFrame: cfg.MaxInsertPlatesPerFrame,
            MeasureCurvature: cfg.MeasureInsertCurvature,
            SolveChromaKey: cfg.SolveInsertChromaKey);
        if (Baseline)
            options = options with { RefineEdgesFromNeighbours = false };

        // Experiments: REELBOLT_INSERT_OPTS="Name=value,Name=value" overrides tracker options.
        foreach (string kv in (Environment.GetEnvironmentVariable("REELBOLT_INSERT_OPTS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = kv.Split('=', 2);
            System.Reflection.PropertyInfo prop = typeof(ChromaQuadTracker.Options).GetProperty(parts[0].Trim())!;
            options = options with { };
            prop.SetValue(options, Convert.ChangeType(parts[1].Trim(), prop.PropertyType, CultureInfo.InvariantCulture));
        }

        // REELBOLT_INSERT_RIGID=0 switches the rigid-pose repair off (the behaviour before it existed).
        if (Baseline || Environment.GetEnvironmentVariable("REELBOLT_INSERT_RIGID") == "0")
            options = options with { SolveRigidPose = false };
        var poseNotes = new List<string>();
        // REELBOLT_POSE_TRACE_FILE: also append each note to this file as it is made (for a run that does not finish).
        string? traceFile = Environment.GetEnvironmentVariable("REELBOLT_POSE_TRACE_FILE");
        // REELBOLT_POSE_TRACE=1: one note per solved frame as well.
        ChromaQuadTracker.PoseLogEveryFrame = Environment.GetEnvironmentVariable("REELBOLT_POSE_TRACE") == "1";
        ChromaQuadTracker.PoseLog = note =>
        {
            poseNotes.Add(note);
            if (!string.IsNullOrEmpty(traceFile))
                File.AppendAllText(traceFile, $"{DateTime.UtcNow:HH:mm:ss.fff} {note}\n");
        };
        var sw = Stopwatch.StartNew();
        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(grid, frames, gw, gh, sampleFps, options, w, h);
        double trackMs = sw.Elapsed.TotalMilliseconds;
        ChromaQuadTracker.PoseLog = null;
        ChromaQuadTracker.PoseLogEveryFrame = false;
        tracks = OverrideFirstTrack(path, tracks, w, h, sampleFps);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"## {Path.GetFileName(path)}  {w}x{h} @ {fps:0.###} fps, {duration:0.##} s; grid {gw}x{gh} @ {sampleFps:0.###} fps, {frames} frames, tracked in {trackMs:0} ms");

        foreach (string note in poseNotes)
            sb.AppendLine("  pose: " + note);

        // Raw per-frame detections at the tuned growth bar, for the lag measure and the
        // "plate visible but untracked" count at grid level.
        double? bar = tracks.FirstOrDefault()?.Tuning?.Parameters.GetValueOrDefault(ChromaTrackerTuner.GrowExcessFractionKey);
        var raw = new ChromaQuadTracker.FrameQuad?[frames];
        var rawOptions = options with { GrowExcessFraction = bar ?? ChromaQuadTracker.DefaultGrowExcessFraction, MeasureCurvature = false, SolveChromaKey = false };
        for (int f = 0; f < frames; f++)
            raw[f] = ChromaQuadTracker.DetectQuads(grid, f * gw * gh * 3, gw, gh, f, rawOptions).FirstOrDefault();

        foreach (VideoInsertRegionTrack t in tracks)
        {
            (double rms, double p95) = Jitter(t, w, h);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  track {t.Id}: {t.StartSec:0.000}-{t.EndSec:0.000} s, conf {t.Confidence:0.000}, aspect {t.MeanAspectRatio:0.000}, " +
                $"key {(t.ChromaKey is null ? "none" : $"#{t.ChromaKey.R:X2}{t.ChromaKey.G:X2}{t.ChromaKey.B:X2}")}, " +
                $"jitter (vs local quadratic) RMS {rms:0.00} px / p95 {p95:0.00} px");
        }

        if (Environment.GetEnvironmentVariable("REELBOLT_INSERT_DEBUG") == "1")
        {
            // Which detections the outlier pass replaces, with their photometric edge evidence.
            var tuned = options with { GrowExcessFraction = bar ?? ChromaQuadTracker.DefaultGrowExcessFraction };
            var perFrame = new List<ChromaQuadTracker.FrameQuad>[frames];
            for (int f = 0; f < frames; f++)
                perFrame[f] = ChromaQuadTracker.DetectQuads(grid, f * gw * gh * 3, gw, gh, f, tuned);
            double scale = Math.Max(1.0, sampleFps / ChromaQuadTracker.ReferenceSampleFps);
            var scaled = tuned with { MaxGapFrames = ChromaQuadTracker.ScaleFrames(tuned.MaxGapFrames, scale), MaxOccludedFrames = ChromaQuadTracker.ScaleFrames(tuned.MaxOccludedFrames, scale) };
            foreach (List<ChromaQuadTracker.FrameQuad> run0 in ChromaQuadTracker.AssembleTracks(perFrame, scaled, scale))
            {
                List<ChromaQuadTracker.FrameQuad> run = ChromaQuadTracker.TrimWeakEnds(run0);
                if (run.Count < 4)
                    continue;
                string[] range = (Environment.GetEnvironmentVariable("REELBOLT_INSERT_DEBUG_FRAMES") ?? "-1..-1").Split("..");
                int lo = int.Parse(range[0], CultureInfo.InvariantCulture), hi = int.Parse(range[^1], CultureInfo.InvariantCulture);
                run = ChromaEdgeTemporalRefiner.Refine(run, grid, gw, gh, "green", m =>
                {
                    int f = int.Parse(m.Split(' ')[1][1..], CultureInfo.InvariantCulture);
                    if (f >= lo && f <= hi)
                        sb.AppendLine("    " + m);
                });
                foreach (ChromaQuadTracker.FrameQuad q in run.Where(q => q.FrameIndex >= lo && q.FrameIndex <= hi))
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    refined f{q.FrameIndex}: TR {q.X1 * w:0.0},{q.Y1 * h:0.0} BR {q.X3 * w:0.0},{q.Y3 * h:0.0} TL {q.X0 * w:0.0},{q.Y0 * h:0.0} BL {q.X2 * w:0.0},{q.Y2 * h:0.0} interp {q.Interpolated}");
                (List<ChromaQuadTracker.FrameQuad> cleaned, double noise) = ChromaQuadTracker.RejectImplausibleSamples(run, sampleFps, gw, gh, scaled);
                var after = cleaned.ToDictionary(q => q.FrameIndex);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  debug run {run[0].FrameIndex}-{run[^1].FrameIndex}: noise {noise:0.000} grid px, median edge contrast {ChromaQuadTracker.Median(run.Where(q => !q.Interpolated).Select(q => q.EdgeContrast).ToList()):0.000}");
                foreach (ChromaQuadTracker.FrameQuad q in run.Where(q => !q.Interpolated))
                {
                    bool rejected = !after.TryGetValue(q.FrameIndex, out var a2) || a2.Interpolated;
                    if (rejected)
                        sb.AppendLine(CultureInfo.InvariantCulture, $"    rejected frame {q.FrameIndex}: edge contrast {q.EdgeContrast:0.000}, fill {q.FillScore:0.000}, bows flat {q.Bows.IsFlat}");
                }
            }
        }

        // Per grid frame: the first track's emitted corners and the raw detection, source pixels.
        var trackDump = new StringBuilder("frame,t_x0,t_y0,t_x1,t_y1,t_x2,t_y2,t_x3,t_y3,r_x0,r_y0,r_x1,r_y1,r_x2,r_y2,r_x3,r_y3,core_area\n");
        VideoInsertRegionTrack? first = tracks.FirstOrDefault();

        // Where the first track's surface model puts a 5x5 lattice of plate coordinates (u, v in
        // 0, 0.25 .. 1, row-major from the top-left), in source pixels: the corner pin's homography
        // for a flat track, plus the measured edge bows for one the compile step would mesh. Scored
        // against a synthetic clip's known surface, this is the content's registration error.
        var gridDump = new StringBuilder("frame");
        for (int p = 0; p < 25; p++)
            gridDump.Append(CultureInfo.InvariantCulture, $",x{p},y{p}");
        gridDump.Append('\n');
        bool meshed = first is not null && first.MeanCurvature >= ChromaEdgeTemporalRefiner.FlatTrackCurvature;
        for (int f = 0; f < frames; f++)
        {
            trackDump.Append(CultureInfo.InvariantCulture, $"{f}");
            VideoInsertQuadKeyframe? kf = first?.Keyframes.FirstOrDefault(k => Math.Abs(k.TimeSec * sampleFps - f) < 0.01);
            if (kf is not null)
            {
                (double X, double Y) P(int c) => (WarpCorner(kf, c).X * w, WarpCorner(kf, c).Y * h);
                var hom = InsertSurfaceMesh.UnitSquareHomography.Solve(P(0), P(1), P(2), P(3));
                if (hom is not null)
                {
                    InsertPlateBows bows = meshed ? VideoCompileStepExecutor.FromDto(kf.Bows) : InsertPlateBows.Flat;
                    gridDump.Append(CultureInfo.InvariantCulture, $"{f}");
                    for (int p = 0; p < 25; p++)
                    {
                        (double X, double Y) m = InsertSurfaceMesh.MapPoint(hom, bows, (p % 5) / 4.0, (p / 5) / 4.0);
                        gridDump.Append(CultureInfo.InvariantCulture, $",{m.X:0.00},{m.Y:0.00}");
                    }

                    gridDump.Append('\n');
                }
            }

            // The corners the compile step warps to: the silhouette-refined ones where curvature was
            // measured (a curved plate's mesh is built on them), else the fitted quad.
            for (int c = 0; c < 4; c++)
                trackDump.Append(kf is null ? ",," : string.Create(CultureInfo.InvariantCulture, $",{WarpCorner(kf, c).X * w:0.00},{WarpCorner(kf, c).Y * h:0.00}"));
            ChromaQuadTracker.FrameQuad? d = raw[f];
            if (d is not null && kf is not null)
                d = ChromaQuadTracker.AlignCornerLabels(d, new ChromaQuadTracker.FrameQuad(f, kf.X0, kf.Y0, kf.X1, kf.Y1, kf.X2, kf.Y2, kf.X3, kf.Y3, 0, 1, 1));
            for (int c = 0; c < 4; c++)
            {
                (double X, double Y) rc = d is null ? default : c switch { 0 => (d.X0, d.Y0), 1 => (d.X1, d.Y1), 2 => (d.X2, d.Y2), _ => (d.X3, d.Y3) };
                trackDump.Append(d is null ? ",," : string.Create(CultureInfo.InvariantCulture, $",{rc.X * w:0.00},{rc.Y * h:0.00}"));
            }

            trackDump.Append(CultureInfo.InvariantCulture, $",{(d is null ? 0 : d.CoreAreaRatio):0.00000}\n");
        }

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,
            $"track-{Environment.GetEnvironmentVariable("REELBOLT_INSERT_LABEL") ?? "run"}-{Path.GetFileNameWithoutExtension(path)}.csv"), trackDump.ToString());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,
            $"grid-{Environment.GetEnvironmentVariable("REELBOLT_INSERT_LABEL") ?? "run"}-{Path.GetFileNameWithoutExtension(path)}.csv"), gridDump.ToString());

        int plateFramesUntracked = 0, plateFrames = 0;
        for (int f = 0; f < frames; f++)
        {
            if (CorePlateFraction(grid, f * gw * gh * 3, gw * gh) < cfg.MinInsertRegionAreaRatio)
                continue;
            plateFrames++;
            double t = f / sampleFps;
            if (!tracks.Any(tr => t >= tr.StartSec - 1e-6 && t < tr.EndSec - 1e-6))
                plateFramesUntracked++;
        }

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"  grid frames with a visible plate: {plateFrames}, outside every track's span: {plateFramesUntracked}");

        // REELBOLT_INSERT_NO_COMPOSITE=1: geometry only (the track and lattice dumps), no ffmpeg composite.
        if (Environment.GetEnvironmentVariable("REELBOLT_INSERT_NO_COMPOSITE") != "1")
            sb.Append(Composite(path, w, h, fpsNum, fpsDen, duration, tracks));
        return sb.ToString();
    }

    /// <summary>
    /// <c>REELBOLT_INSERT_TRACK_FROM=label</c>: replace the first track's geometry with the corners
    /// (and, when present, edge bows) in <c>track-{label}-{clip}.csv</c> beside the clip — one row per
    /// grid frame, source pixels, the columns this harness itself writes. Lets a geometry prototype
    /// be composited and scored by the real compile path before it is ported. The track's key,
    /// tuning and identity stay the tracker's own.
    /// </summary>
    private static IReadOnlyList<VideoInsertRegionTrack> OverrideFirstTrack(
        string path, IReadOnlyList<VideoInsertRegionTrack> tracks, int w, int h, double sampleFps)
    {
        string? from = Environment.GetEnvironmentVariable("REELBOLT_INSERT_TRACK_FROM");
        if (string.IsNullOrEmpty(from) || tracks.Count == 0)
            return tracks;
        string csv = Path.Combine(Path.GetDirectoryName(path)!, $"track-{from}-{Path.GetFileNameWithoutExtension(path)}.csv");
        if (!File.Exists(csv))
            return tracks;

        string[] lines = File.ReadAllLines(csv);
        var header = lines[0].Split(',').Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        bool hasBows = header.ContainsKey("b_top");
        var keys = new List<VideoInsertQuadKeyframe>();
        var curvature = new List<double>();
        foreach (string line in lines.Skip(1))
        {
            string[] c = line.Split(',');
            if (c.Length <= header["t_y3"] || c[header["t_x0"]].Length == 0)
                continue;
            double V(string name) => double.Parse(c[header[name]], CultureInfo.InvariantCulture);
            var q = new VideoInsertSurfaceQuad(
                V("t_x0") / w, V("t_y0") / h, V("t_x1") / w, V("t_y1") / h, V("t_x2") / w, V("t_y2") / h, V("t_x3") / w, V("t_y3") / h);
            VideoInsertEdgeBows? bows = hasBows
                ? new VideoInsertEdgeBows(V("b_top"), V("s_top_skew"), V("b_right"), V("s_right_skew"), V("b_bottom"), V("s_bottom_skew"), V("b_left"), V("s_left_skew"))
                : null;
            if (bows is not null)
                curvature.Add(new[] { bows.TopBow, bows.RightBow, bows.BottomBow, bows.LeftBow }.Max(Math.Abs));
            keys.Add(new VideoInsertQuadKeyframe(
                int.Parse(c[0], CultureInfo.InvariantCulture) / sampleFps, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3, bows, q));
        }

        if (keys.Count < 2)
            return tracks;
        var list = tracks.ToList();
        list[0] = list[0] with
        {
            Keyframes = keys,
            StartSec = keys[0].TimeSec,
            EndSec = keys[^1].TimeSec + 1 / sampleFps,
            Confidence = Math.Max(list[0].Confidence, 0.9),
            MeanCurvature = curvature.Count == 0 ? 0 : curvature.OrderBy(v => v).ElementAt(curvature.Count / 2)
        };
        return list;
    }

    /// <summary>Per corner, distance from a quadratic through the ±4 neighbouring keyframes, in source pixels.</summary>
    private static (double Rms, double P95) Jitter(VideoInsertRegionTrack t, int w, int h)
    {
        var k = t.Keyframes;
        var res = new List<double>();
        const int half = 4;
        for (int i = half; i + half < k.Count; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                double ex = Fit(j => Corner(k[j], c).X * w, i, half) - Corner(k[i], c).X * w;
                double ey = Fit(j => Corner(k[j], c).Y * h, i, half) - Corner(k[i], c).Y * h;
                res.Add(Math.Sqrt(ex * ex + ey * ey));
            }
        }

        if (res.Count == 0)
            return (0, 0);
        res.Sort();
        return (Math.Sqrt(res.Average(r => r * r)), res[(int)(0.95 * (res.Count - 1))]);

        // Least-squares quadratic through the neighbours (excluding i), evaluated at i.
        static double Fit(Func<int, double> v, int i, int half)
        {
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
            for (int j = i - half; j <= i + half; j++)
            {
                if (j == i)
                    continue;
                double x = j - i, y = v(j);
                s0 += 1; s1 += x; s2 += x * x; s3 += x * x * x; s4 += x * x * x * x;
                y0 += y; y1 += x * y; y2 += x * x * y;
            }

            // Solve [s0 s1 s2; s1 s2 s3; s2 s3 s4] [a b c] = [y0 y1 y2]; prediction at x=0 is a.
            double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
            double da = y0 * (s2 * s4 - s3 * s3) - s1 * (y1 * s4 - s3 * y2) + s2 * (y1 * s3 - s2 * y2);
            return da / det;
        }
    }

    private static (double X, double Y) Corner(VideoInsertQuadKeyframe k, int c) => c switch
    {
        0 => (k.X0, k.Y0),
        1 => (k.X1, k.Y1),
        2 => (k.X2, k.Y2),
        _ => (k.X3, k.Y3)
    };

    private static (double X, double Y) WarpCorner(VideoInsertQuadKeyframe k, int c) => k.SurfaceCorners is { } s
        ? c switch { 0 => (s.X0, s.Y0), 1 => (s.X1, s.Y1), 2 => (s.X2, s.Y2), _ => (s.X3, s.Y3) }
        : Corner(k, c);

    private static bool StrictPlate(byte r, byte g, byte b) => g >= 60 && g * 10 > r * 13 + 100 && g * 10 > b * 13 + 100;

    /// <summary>The tracker's permissive grow test — reaches the plate's dim edges, where the strict test stops short.</summary>
    private static bool LoosePlate(byte r, byte g, byte b) => g >= 24 && g * 10 > r * 11 + 20 && g * 10 > b * 11 + 20;

    /// <summary>Green that reads as plate on screen: bright enough and pure enough — dull foliage (purity 0.1-0.3) is not.</summary>
    private static bool VisibleGreen(byte r, byte g, byte b) => g >= 40 && (g - Math.Max(r, b)) >= 0.35 * g;

    private static double CorePlateFraction(byte[] data, int offset, int pixels)
    {
        int n = 0;
        for (int p = 0; p < pixels; p++)
        {
            int i = offset + p * 3;
            if (StrictPlate(data[i], data[i + 1], data[i + 2]))
                n++;
        }

        return n / (double)pixels;
    }

    /// <summary>
    /// Composites a coordinate card into every track that clears the compile step's default
    /// confidence floor, exactly as the compile step would for configured content, and measures
    /// the output frame by frame against the source.
    /// </summary>
    private static string Composite(string path, int w, int h, int fpsNum, int fpsDen, double duration, IReadOnlyList<VideoInsertRegionTrack> tracks)
    {
        var compile = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1);
        var media = new VideoAnalysisMedia(duration, fpsNum, fpsDen, w, h);
        long totalFrames = (long)Math.Round(duration * fpsNum / Math.Max(1, fpsDen));
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, duration, 0, duration, 0, totalFrames) };
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));

        var inserts = new List<ResolvedScreenInsert>();
        var sbGuards = new StringBuilder();
        foreach (VideoInsertRegionTrack t in tracks.Where(t => t.Confidence >= compile.MinInsertConfidence))
        {
            (double Start, double End)? window = timeline.MapWindowToOutput(t.StartSec, t.EndSec, t.SourceIndex);
            if (window is null)
                continue;
            VideoInsertChromaKey? matte = compile.EnableInsertMatte ? t.ChromaKey : null;
            double overscan = InsertCompositeGeometry.EffectiveOverscan(Math.Clamp(compile.InsertOverscan, 0, 0.1), matte is not null);
            if (double.TryParse(Environment.GetEnvironmentVariable("REELBOLT_INSERT_OVERSCAN"), NumberStyles.Float, CultureInfo.InvariantCulture, out double forced))
                overscan = forced;
            IReadOnlyList<InsertQuadFrame> keyframes = VideoCompileStepExecutor.BuildInsertKeyframes(
                t, timeline, media, window.Value, compile.MaxInsertExprKeyframes, overscan);
            if (keyframes.Count == 0)
                continue;
            // Same decision the compile step makes for a flat insert: smear the overscan band
            // (content pinned to the plate itself) — or, in baseline mode, the old black pad.
            bool smear = !Baseline && matte is not null && overscan > 0;
            string? prefilter = RealContent is { } rc
                ? InsertContentFitFilter.Build(RealFit, rc.W, rc.H, t.MeanAspectRatio)
                : null;
            if (matte is not null && overscan > 0 && !smear)
                prefilter = InsertContentFitFilter.Append(prefilter, InsertContentFitFilter.OverscanPad(overscan));
            IReadOnlyList<InsertQuadFrame>? plate = smear
                ? VideoCompileStepExecutor.BuildInsertKeyframes(t, timeline, media, window.Value, compile.MaxInsertExprKeyframes, 0)
                : null;
            // Same surface decision the compile step makes under InsertSurface = Auto: a track whose
            // measured curvature clears MinInsertCurvature gets the mesh warp, any other a corner pin.
            InsertMeshPlan? mesh = !Baseline && t.MeanCurvature >= compile.MinInsertCurvature
                ? VideoCompileStepExecutor.BuildInsertMeshPlan(
                    VideoCompileStepExecutor.SampleInsertKeyframes(t, timeline, media, window.Value, compile.MaxInsertExprKeyframes),
                    media, overscan, compile.InsertMeshTolerancePx, compile.MaxInsertMeshCells)
                : null;
            if (mesh is not null)
                sbGuards.AppendLine(CultureInfo.InvariantCulture, $"    {t.Id}: curvature {t.MeanCurvature:0.0000}, {mesh.Rows}x{mesh.Cols} mesh warp");
            inserts.Add(new ResolvedScreenInsert(
                t.Id, "card", window.Value.Start, window.Value.End, keyframes, fpsNum, fpsDen,
                Mesh: mesh, Matte: matte, ContentPrefilter: prefilter,
                // REELBOLT_INSERT_REFLECTIONS=0 leaves the reflection layer off, so the card's own
                // colours survive exactly (ground-truth registration scoring reads them back).
                TransferReflections: matte is not null && compile.InsertReflections
                    && Environment.GetEnvironmentVariable("REELBOLT_INSERT_REFLECTIONS") != "0",
                ContentKeyframes: plate));
        }

        // Where the CONTENT's own frame lands under the old padded-overscan scheme: the content
        // padded by (1 + o), the padded frame warped onto the quad expanded by (1 + o) about its
        // vertex centroid. Exact only for a parallelogram; on a foreshortened plate the content's
        // corners land off the plate's — inside it (a strip of black pad) or outside (content
        // cropped). The smeared scheme pins them to the plate by construction.
        var offsets = new List<double>();
        foreach (VideoInsertRegionTrack t in tracks.Where(t => t.Confidence >= compile.MinInsertConfidence && t.ChromaKey is not null))
        {
            double o = InsertCompositeGeometry.EffectiveOverscan(Math.Clamp(compile.InsertOverscan, 0, 0.1), matted: true);
            double e = o / 2 / (1 + o);
            foreach (VideoInsertQuadKeyframe k in t.Keyframes)
            {
                VideoInsertSurfaceQuad q = k.SurfaceCorners ?? new VideoInsertSurfaceQuad(k.X0, k.Y0, k.X1, k.Y1, k.X2, k.Y2, k.X3, k.Y3);
                (double X, double Y)[] plate = [(q.X0 * w, q.Y0 * h), (q.X1 * w, q.Y1 * h), (q.X2 * w, q.Y2 * h), (q.X3 * w, q.Y3 * h)];
                double cx = plate.Average(p => p.X), cy = plate.Average(p => p.Y);
                (double X, double Y) E((double X, double Y) p) => (cx + (p.X - cx) * (1 + o), cy + (p.Y - cy) * (1 + o));
                var hom = InsertSurfaceMesh.UnitSquareHomography.Solve(E(plate[0]), E(plate[1]), E(plate[2]), E(plate[3]));
                if (hom is null)
                    continue;
                (double U, double V)[] inner = [(e, e), (1 - e, e), (e, 1 - e), (1 - e, 1 - e)];
                for (int c = 0; c < 4; c++)
                {
                    (double X, double Y) m = hom.Map(inner[c].U, inner[c].V);
                    offsets.Add(Math.Sqrt(Math.Pow(m.X - plate[c].X, 2) + Math.Pow(m.Y - plate[c].Y, 2)));
                }
            }
        }

        var sbGeometry = new StringBuilder();
        if (offsets.Count > 0)
        {
            offsets.Sort();
            sbGeometry.AppendLine(CultureInfo.InvariantCulture,
                $"  content-frame corners vs tracked plate under the old padded overscan: mean {offsets.Average():0.00} px, p95 {offsets[(int)(0.95 * (offsets.Count - 1))]:0.00} px, max {offsets[^1]:0.00} px (smeared: 0 by construction)");
        }

        int guardCount = 0;
        if (!Baseline)
        {

            InsertPlateGuardPlanner.Plan plan = InsertPlateGuardPlanner.Build(
                tracks, timeline, media, inserts, _ => VideoCompileStepExecutor.SourceCanvasFit.Identity(media),
                compile.EnableInsertMatte, compile.MinInsertConfidence, compile.MaxInsertExprKeyframes);
            inserts.AddRange(plan.Guards);
            guardCount = plan.Guards.Count;
            foreach (JsonNode? r in plan.Report)
                sbGuards.AppendLine($"    unfilled plate: {r!.ToJsonString()}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  composited tracks: {string.Join(", ", inserts.Where(i => i.GuardFillColor is null).Select(i => i.RegionId))}; plate guards: {guardCount}");
        sb.Append(sbGuards);
        sb.Append(sbGeometry);

        string tmp = Path.Combine(Path.GetTempPath(), "insert-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string card = $"color=c=black:s=1280x720:r={fpsNum}/{fpsDen}:d={(duration + 2).ToString(CultureInfo.InvariantCulture)},format=gbrp," +
                $"geq=r='{CardLow}+X*{CardSpan}/(W-1)':g='0':b='{CardLow}+Y*{CardSpan}/(H-1)'";
            string graph = inserts.Count == 0
                ? "[0:v]format=rgb24[vout]"
                : "[0:v]null[b0];" + ScreenInsertFilterBuilder.BuildFilterChain("[b0]", inserts, w, h, i => i + 1, "[vc]") + ";[vc]format=rgb24[vout]";
            string script = Path.Combine(tmp, "graph.txt");
            File.WriteAllText(script, graph);

            var args = new List<string> { "-v", "error", "-i", path };
            foreach (ResolvedScreenInsert insert in inserts)
            {
                if (insert.GuardFillColor is null && RealContent is { } rc)
                    args.AddRange(["-stream_loop", "-1", "-i", rc.Path]);
                else
                    args.AddRange(["-f", "lavfi", "-i", insert.GuardFillColor is { } fill
                        ? $"color=c={fill}:s=16x16:r={fpsNum}/{fpsDen}"
                        : card]);
            }
            args.AddRange(["-filter_complex_script", script, "-map", "[vout]", "-frames:v", totalFrames.ToString(CultureInfo.InvariantCulture), "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);

            using Process composite = Start(args);
            using Process source = Start(["-v", "error", "-i", path, "-map", "0:v", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
            Task<string> e1 = composite.StandardError.ReadToEndAsync();
            Task<string> e2 = source.StandardError.ReadToEndAsync();

            int frameBytes = w * h * 3;
            var a = new byte[frameBytes];
            var b = new byte[frameBytes];

            // REELBOLT_INSERT_SAVE_COMPOSITE=1 keeps the composite as composite-{label}-{clip}.mp4
            // beside the clip (the "composite" prefix keeps it out of the next run's clip list).
            Process? keep = null;
            if (Environment.GetEnvironmentVariable("REELBOLT_INSERT_SAVE_COMPOSITE") == "1")
            {
                string kept = Path.Combine(Path.GetDirectoryName(path)!,
                    $"composite-{Environment.GetEnvironmentVariable("REELBOLT_INSERT_LABEL") ?? "run"}-{Path.GetFileNameWithoutExtension(path)}.mp4");
                var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true };
                foreach (string arg in new[]
                {
                    "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-r", $"{fpsNum}/{fpsDen}", "-i", "pipe:0",
                    "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "16", "-pix_fmt", "yuv420p", kept
                })
                {
                    psi.ArgumentList.Add(arg);
                }

                keep = Process.Start(psi);
            }

            var residual = new List<double>();
            var vresidual = new List<double>();
            int vflashes = 0;
            var lostL = new List<double>();
            var lostR = new List<double>();
            var lostT = new List<double>();
            var lostB = new List<double>();
            int flashes = 0, raw = 0, plateFrames = 0;
            var rs = new List<int>();
            var bs = new List<int>();
            var mask = new bool[w * h];
            var dump = new StringBuilder("frame,plate_px,residual_frac,visible_residual_frac\n");
            int frameNo = -1;

            // Full-resolution reference: each tracked frame's edges re-fitted on the SOURCE frame
            // at native resolution, seeded with the track's own corners (see the class remarks).
            double outFps = fpsNum / (double)Math.Max(1, fpsDen);
            var keyAt = new Dictionary<int, List<(int Track, VideoInsertQuadKeyframe K)>>();
            for (int ti = 0; ti < tracks.Count; ti++)
            {
                foreach (VideoInsertQuadKeyframe k in tracks[ti].Keyframes)
                {
                    int f = (int)Math.Round(k.TimeSec * outFps);
                    if (!keyAt.TryGetValue(f, out var list))
                        keyAt[f] = list = [];
                    list.Add((ti, k));
                }
            }

            var refErr = new Dictionary<int, List<(int Frame, (double X, double Y)[] Err, double Speed)>>();
            var domField = new float[w * h];
            while (ReadFull(source.StandardOutput.BaseStream, a) && ReadFull(composite.StandardOutput.BaseStream, b))
            {
                frameNo++;
                keep?.StandardInput.BaseStream.Write(b, 0, b.Length);
                if ((Environment.GetEnvironmentVariable("REELBOLT_INSERT_SAVE_FRAME") ?? string.Empty)
                    .Split(',').Contains($"{Path.GetFileNameWithoutExtension(path)}:{frameNo}"))
                {
                    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!,
                        $"frame-{Path.GetFileNameWithoutExtension(path)}-{frameNo}-{w}x{h}.rgb"), b);
                }

                if (keyAt.TryGetValue(frameNo, out var keysHere))
                {
                    for (int p = 0, q = 0; p < frameBytes; p += 3, q++)
                        domField[q] = ChromaEdgeLineFitter.Dominance("green", a[p], a[p + 1], a[p + 2]);
                    foreach ((int ti, VideoInsertQuadKeyframe k) in keysHere)
                    {
                        double level = tracks[ti].ChromaKey?.PlateDominance ?? 128;
                        (double X, double Y)[] tc = [(k.X0 * w - 0.5, k.Y0 * h - 0.5), (k.X1 * w - 0.5, k.Y1 * h - 0.5), (k.X3 * w - 0.5, k.Y3 * h - 0.5), (k.X2 * w - 0.5, k.Y2 * h - 0.5)];
                        ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(domField, w, h, level, tc[0], tc[1], tc[2], tc[3]);
                        if (fit is null)
                            continue;

                        // Only frames where the reference itself sits on a clean plate/bezel
                        // boundary along all four edges count — a finger or a reflection breaks
                        // the reference exactly where it breaks the tracker.
                        (double X, double Y)[] rc = fit.Value.Corners;
                        double rcx = rc.Average(c => c.X), rcy = rc.Average(c => c.Y);
                        bool clean = true;
                        for (int e = 0; e < 4 && clean; e++)
                        {
                            var line = ChromaEdgeLineFitter.LineThrough(rc[e], rc[(e + 1) % 4]);
                            clean = ChromaEdgeLineFitter.EdgeContrastScore(domField, w, h, level, line, rc[e], rc[(e + 1) % 4], rcx, rcy, offset: 4) >= 0.6;
                        }

                        if (!clean)
                            continue;
                        var cornerErr = new (double X, double Y)[4];
                        for (int c = 0; c < 4; c++)
                            cornerErr[c] = (tc[c].X - fit.Value.Corners[c].X, tc[c].Y - fit.Value.Corners[c].Y);
                        if (!refErr.TryGetValue(ti, out var el))
                            refErr[ti] = el = [];
                        el.Add((frameNo, cornerErr, 0));
                    }
                }

                int plate = 0, left = 0;
                rs.Clear();
                bs.Clear();
                int bx0 = w, by0 = h, bx1 = -1, by1 = -1;
                for (int p = 0, q = 0; p < frameBytes; p += 3, q++)
                {
                    mask[q] = LoosePlate(a[p], a[p + 1], a[p + 2]);
                    if (StrictPlate(a[p], a[p + 1], a[p + 2]))
                    {
                        int qx = q % w, qy = q / w;
                        bx0 = Math.Min(bx0, qx);
                        bx1 = Math.Max(bx1, qx);
                        by0 = Math.Min(by0, qy);
                        by1 = Math.Max(by1, qy);
                    }
                }

                // Visible green: anything green and pure enough to read as the plate on screen —
                // including the dim, reflection-covered plate the strict test misses — inside the
                // strict plate's box grown by a tenth of the frame (lawn and foliage are far less
                // pure and stay out).
                int visible = 0, visibleLeft = 0;
                if (bx1 >= 0)
                {
                    int mx = w / 10, my = h / 10;
                    for (int y = Math.Max(0, by0 - my); y <= Math.Min(h - 1, by1 + my); y++)
                    {
                        for (int x = Math.Max(0, bx0 - mx); x <= Math.Min(w - 1, bx1 + mx); x++)
                        {
                            int p = (y * w + x) * 3;
                            if (!VisibleGreen(a[p], a[p + 1], a[p + 2]))
                                continue;
                            visible++;
                            if (VisibleGreen(b[p], b[p + 1], b[p + 2]))
                                visibleLeft++;
                        }
                    }
                }

                for (int q = 0; q < mask.Length; q++)
                {
                    if (!mask[q])
                        continue;
                    int p = q * 3;
                    if (StrictPlate(a[p], a[p + 1], a[p + 2]))
                    {
                        plate++;
                        if (StrictPlate(b[p], b[p + 1], b[p + 2]))
                        {
                            left++;
                            continue;
                        }
                    }

                    // Content extent is read only well inside the plate (3 px from its silhouette),
                    // where the matte is fully on: at the edge the content blends with the bezel,
                    // which darkens both coordinate channels and would bias the reading.
                    int x = q % w, y = q / w;
                    const int r = 3;
                    bool interior = x >= r && y >= r && x < w - r && y < h - r &&
                        mask[q - r] && mask[q + r] && mask[q - r * w] && mask[q + r * w];
                    // The card's green is 0, so whatever green the pixel carries is the reflection
                    // layer screen-blended on top (out = a + g - a*g): invert that exactly.
                    double g = b[p + 1] / 255.0;
                    if (interior && g < 0.5)
                    {
                        int cr = (int)Math.Round((b[p] / 255.0 - g) / (1 - g) * 255);
                        int cb = (int)Math.Round((b[p + 2] / 255.0 - g) / (1 - g) * 255);
                        if (cr >= CardLow - 4 && cb >= CardLow - 4)
                        {
                            rs.Add(cr);
                            bs.Add(cb);
                        }
                    }
                }

                double vfrac = visible == 0 ? 0 : visibleLeft / (double)visible;
                dump.Append(CultureInfo.InvariantCulture, $"{frameNo},{plate},{(plate == 0 ? 0 : left / (double)plate):0.0000},{vfrac:0.0000}\n");
                if (plate < frameBytes / 3 / 500)
                    continue;
                plateFrames++;
                double frac = left / (double)plate;
                residual.Add(frac);
                vresidual.Add(vfrac);
                if (frac > 0.02)
                    flashes++;
                if (frac > 0.5)
                    raw++;
                if (vfrac > 0.02)
                    vflashes++;
                if (rs.Count > plate / 4)
                {
                    rs.Sort();
                    bs.Sort();
                    lostL.Add(Math.Max(0, (rs[rs.Count / 200] - CardLow) / (double)CardSpan));
                    lostR.Add(Math.Max(0, (CardLow + CardSpan - rs[rs.Count - 1 - rs.Count / 200]) / (double)CardSpan));
                    lostT.Add(Math.Max(0, (bs[bs.Count / 200] - CardLow) / (double)CardSpan));
                    lostB.Add(Math.Max(0, (CardLow + CardSpan - bs[bs.Count - 1 - bs.Count / 200]) / (double)CardSpan));
                }
            }

            composite.WaitForExit();
            source.WaitForExit();
            if (keep is not null)
            {
                keep.StandardInput.Close();
                keep.WaitForExit();
                keep.Dispose();
            }

            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!,
                $"dump-{Environment.GetEnvironmentVariable("REELBOLT_INSERT_LABEL") ?? "run"}-{Path.GetFileNameWithoutExtension(path)}.csv"), dump.ToString());
            string err = e1.Result + e2.Result;
            if (composite.ExitCode != 0)
                sb.AppendLine($"  ffmpeg failed: {err.Trim()}");

            foreach ((int ti, var el) in refErr.OrderBy(kv => kv.Key))
            {
                var mags = el.SelectMany(e => e.Err.Select(v => Math.Sqrt(v.X * v.X + v.Y * v.Y))).OrderBy(v => v).ToList();
                var slips = new List<double>();
                for (int i = 1; i < el.Count; i++)
                {
                    if (el[i].Frame - el[i - 1].Frame > 2)
                        continue;
                    for (int c = 0; c < 4; c++)
                    {
                        double dx = el[i].Err[c].X - el[i - 1].Err[c].X, dy = el[i].Err[c].Y - el[i - 1].Err[c].Y;
                        slips.Add(dx * dx + dy * dy);
                    }
                }

                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  vs full-res edge reference ({tracks[ti].Id}, {el.Count} frames): corner error mean {mags.Average():0.00} px, p95 {mags[(int)(0.95 * (mags.Count - 1))]:0.00} px, " +
                    $"max {mags[^1]:0.0} px; slip (frame-to-frame change of the error) RMS {(slips.Count == 0 ? 0 : Math.Sqrt(slips.Average())):0.00} px");
            }

            residual.Sort();
            double mean = residual.Count == 0 ? 0 : residual.Average();
            double p95 = residual.Count == 0 ? 0 : residual[(int)(0.95 * (residual.Count - 1))];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  residual green inside the plate over {plateFrames} frames: mean {mean * 100:0.00}%, p95 {p95 * 100:0.00}%, " +
                $"frames >2%: {flashes}, frames >50% (raw plate): {raw}");
            vresidual.Sort();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  visible-green residual (incl. dim/reflected plate): mean {(vresidual.Count == 0 ? 0 : vresidual.Average()) * 100:0.00}%, " +
                $"p95 {(vresidual.Count == 0 ? 0 : vresidual[(int)(0.95 * (vresidual.Count - 1))]) * 100:0.00}%, frames >2%: {vflashes}");
            if (lostL.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  content lost off each side (median over {lostL.Count} frames): left {Median(lostL) * 100:0.0}%, right {Median(lostR) * 100:0.0}%, " +
                    $"top {Median(lostT) * 100:0.0}%, bottom {Median(lostB) * 100:0.0}%");
            }
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }

        return sb.ToString();
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    private static bool ReadFull(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
                return false;
            read += n;
        }

        return true;
    }

    private static Process Start(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static byte[] SampleGrid(string path, double fps, int gw, int gh)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "insert-grid-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            string[] args = FfmpegArgvBuilder.BuildGridSampleArgs(path, tmp, fps, gw, gh, 0, GridChroma.FullChroma);
            using Process p = Start(args);
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(err);
            return File.ReadAllBytes(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private static (int W, int H, double Fps, int Num, int Den, double Duration) Probe(string path)
    {
        var psi = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate:format=duration", "-of", "default=nw=1" })
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(path);
        using Process p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var kv = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .Where(x => x.Length == 2)
            .GroupBy(x => x[0])
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
        string[] rate = kv["r_frame_rate"].Split('/');
        int num = int.Parse(rate[0], CultureInfo.InvariantCulture), den = int.Parse(rate[1], CultureInfo.InvariantCulture);
        return (int.Parse(kv["width"], CultureInfo.InvariantCulture), int.Parse(kv["height"], CultureInfo.InvariantCulture),
            num / (double)den, num, den, double.Parse(kv["duration"], CultureInfo.InvariantCulture));
    }
}

/// <summary>Skips unless <c>REELBOLT_INSERT_CLIPS</c> names a directory and ffmpeg is on PATH.</summary>
public sealed class InsertClipsFactAttribute : FactAttribute
{
    public const string DirVariable = "REELBOLT_INSERT_CLIPS";

    public InsertClipsFactAttribute()
    {
        string? dir = Environment.GetEnvironmentVariable(DirVariable);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            Skip = $"Real-footage harness: set {DirVariable} to a directory of clips.";
        else if (new FfmpegFactAttribute().Skip is { } why)
            Skip = why;
    }
}
