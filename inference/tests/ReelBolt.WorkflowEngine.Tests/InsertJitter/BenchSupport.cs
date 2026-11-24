#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter;

/// <summary>Skips unless <c>REELBOLT_JITTER_BENCH=1</c> and ffmpeg/ffprobe are on PATH. Never runs in CI.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class JitterBenchFactAttribute : Xunit.FactAttribute
{
    public const string Variable = "REELBOLT_JITTER_BENCH";

    public JitterBenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Jitter benchmark: set {Variable}=1 (renders ~10 clips, takes minutes).";
        else if (FfmpegToolsOnPath.MissingBinary() is { } missing)
            Skip = $"'{missing}' is not on PATH; the jitter benchmark runs the real binary.";
    }
}

/// <summary>Paths, probing, grid sampling and the production tracker options, shared by the bench parts.</summary>
internal static class BenchSupport
{
    public static string OutDir => Environment.GetEnvironmentVariable("REELBOLT_JITTER_DIR") is { Length: > 0 } d ? d : "/tmp/insert-tracking/bench";
    public static string ClipsDir => Path.Combine(OutDir, "clips");
    public static string CsvDir => Path.Combine(OutDir, "csv");
    public static string SectionsDir => Path.Combine(OutDir, "sections");

    /// <summary>Comma list in <c>REELBOLT_JITTER_SCENES</c> restricts the run to those scene names.</summary>
    public static IEnumerable<JitterScene> SelectedScenes(IEnumerable<string>? onlyThese = null)
    {
        string[] pick = (Environment.GetEnvironmentVariable("REELBOLT_JITTER_SCENES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (JitterScene s in JitterScenes.All)
        {
            if (onlyThese is not null && !onlyThese.Contains(s.Name))
                continue;
            if (pick.Length > 0 && !pick.Contains(s.Name))
                continue;
            yield return s;
        }
    }

    public static readonly VideoAnalyzeStepConfig DefaultConfig = new(1, new VideoSourceRef(VideoSourceKind.ProjectFile));

    /// <summary>The options <c>VideoAnalyzeStepExecutor</c> passes <c>Track</c>, with the ticket's curvature/key switches on.</summary>
    public static ChromaQuadTracker.Options ProductionOptions(VideoAnalyzeStepConfig cfg) => new(
        ColorName: cfg.InsertRegionColor,
        MinAreaRatio: Math.Clamp(cfg.MinInsertRegionAreaRatio, 0.0001, 0.9),
        MinTrackSeconds: Math.Max(0.1, cfg.MinInsertRegionSeconds),
        MaxRegions: Math.Clamp(cfg.MaxInsertRegions, 0, 32),
        MaxPlatesPerFrame: Math.Clamp(cfg.MaxInsertPlatesPerFrame, 1, 16),
        MeasureCurvature: true,
        SolveChromaKey: true);

    public sealed record GridPlan(int GridW, int GridH, double SampleFps, int FrameCap);

    /// <summary>Grid size and sample rate exactly as the analyze step derives them (no probe: the scene's own dimensions).</summary>
    public static GridPlan Plan(JitterScene scene, VideoAnalyzeStepConfig cfg)
    {
        (int gw, int gh) = ChromaQuadTracker.DeriveGridSize(scene.Width, scene.Height, cfg.InsertGridWidth, cfg.InsertGridHeight);
        int cap = ChromaQuadTracker.SampleFrameCap(gw, gh, Math.Clamp(cfg.MaxInsertSampleFrames, 10, 20_000));
        double fps = ChromaQuadTracker.ResolveSampleFps(cfg.InsertSampleFps, scene.Fps, scene.Frames / scene.Fps, gw, gh, cap);
        return new GridPlan(gw, gh, fps, cap);
    }

    private static readonly object GridLock = new();
    private static readonly Dictionary<string, byte[]> GridCache = [];

    /// <summary>Raw RGB24 grid through the production ffmpeg argv (<c>fps,scale=area,rgb24</c>), cached per (clip, size, rate).</summary>
    public static byte[] SampleGrid(string clipPath, double fps, int gw, int gh)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"{clipPath}|{fps}|{gw}x{gh}|{File.GetLastWriteTimeUtc(clipPath).Ticks}");
        lock (GridLock)
        {
            if (GridCache.TryGetValue(key, out byte[]? hit))
                return hit;
        }

        string tmp = Path.Combine(Path.GetTempPath(), "insert-jitter-grid-" + Guid.NewGuid().ToString("N") + ".rgb");
        try
        {
            string[] args = FfmpegArgvBuilder.BuildGridSampleArgs(clipPath, tmp, fps, gw, gh, 0);
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(err.Result);
            byte[] data = File.ReadAllBytes(tmp);
            lock (GridLock)
            {
                // Keep the cache small: a handful of grids is ~100 MB each.
                if (GridCache.Count > 6)
                    GridCache.Clear();
                GridCache[key] = data;
            }

            return data;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public static VideoInsertRegionTrack? Longest(IReadOnlyList<VideoInsertRegionTrack> tracks) =>
        tracks.OrderByDescending(t => t.EndSec - t.StartSec).FirstOrDefault();

    // -------------------------------------------------------------------------------------------
    // Output: tables and csv
    // -------------------------------------------------------------------------------------------

    public sealed record Row(string Label, JitterStats Stats);

    public static string Table(IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Row | N | ErrMean | ErrP95 | ErrMax | SlipRms | SlipP95 | HfRms | ContentSlip | LagXc | LagLS | Overshoot | Drift px/s | P2P |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (Row r in rows)
        {
            JitterStats s = r.Stats;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Label} | {s.N} | {Cell(s.ErrMean)} | {Cell(s.ErrP95)} | {Cell(s.ErrMax)} | {Cell(s.SlipRms)} | {Cell(s.SlipP95)} | {Cell(s.HfRms)} | {Cell(s.ContentSlipRms)} | {Cell(s.LagFrames, "0.00")} | {Cell(s.LagLsFrames, "0.00")} | {Cell(s.Overshoot, "0.00")} | {Cell(s.DriftPxPerSec)} | {Cell(s.PeakToPeak, "0.00")} |"));
        }

        return sb.ToString();
    }

    public static string NoTruthTable(IEnumerable<(string Label, JitterStats Stats)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Track | N | ResidualRms | ResidualP95 | SecondDiffRms | P2P |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach ((string label, JitterStats s) in rows)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {label} | {s.N} | {Cell(s.ResidualRms)} | {Cell(s.ResidualP95)} | {Cell(s.SecondDiffRms)} | {Cell(s.PeakToPeak, "0.00")} |"));
        return sb.ToString();
    }

    private static string Cell(double v, string f = "0.000") => JitterMetrics.Cell(v, f);

    private static string Cell(double? v, string f = "0.000") => JitterMetrics.Cell(v, f);

    /// <summary>Per-frame track, truth and error, one row per track sample (source px unless the caller scaled).</summary>
    public static void WriteCsv(string path, IReadOnlyList<Quad> track, IReadOnlyList<Quad> truth, double fps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Quad[] gt = truth.OrderBy(q => q.T).ToArray();
        var sb = new StringBuilder("frame,t");
        for (int c = 0; c < 4; c++)
            sb.Append(CultureInfo.InvariantCulture, $",c{c}_tx,c{c}_ty,c{c}_gx,c{c}_gy,c{c}_ex,c{c}_ey");
        sb.AppendLine();
        foreach (Quad q in track.OrderBy(q => q.T))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{JitterMetrics.Index(q.T, fps)},{q.T:0.######}");
            int i = Array.BinarySearch(gt.Select(g => g.T).ToArray(), q.T);
            if (i < 0)
                i = Math.Clamp(~i, 0, gt.Length - 1);
            if (i > 0 && Math.Abs(gt[i - 1].T - q.T) < Math.Abs(gt[i].T - q.T))
                i--;
            bool have = gt.Length > 0 && Math.Abs(gt[i].T - q.T) <= 0.25 / fps;
            for (int c = 0; c < 4; c++)
            {
                (double tx, double ty) = q.Corner(c);
                if (have)
                {
                    (double gx, double gy) = gt[i].Corner(c);
                    sb.Append(CultureInfo.InvariantCulture, $",{tx:0.####},{ty:0.####},{gx:0.####},{gy:0.####},{tx - gx:0.####},{ty - gy:0.####}");
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $",{tx:0.####},{ty:0.####},,,,");
                }
            }

            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Reads the track columns of a csv written by <see cref="WriteCsv"/> (fps = the rate used to index frames).</summary>
    public static List<Quad> ReadCsvTrack(string path)
    {
        var quads = new List<Quad>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] p = line.Split(',');
            double D(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
            quads.Add(new Quad(D(1), D(2), D(3), D(8), D(9), D(14), D(15), D(20), D(21)));
        }

        return quads;
    }

    public static void WriteSection(string name, string markdown)
    {
        Directory.CreateDirectory(SectionsDir);
        File.WriteAllText(Path.Combine(SectionsDir, name + ".md"), markdown);
        AssembleReport();
    }

    /// <summary>Concatenates every section on disk, in name order, into <c>report.md</c>.</summary>
    public static void AssembleReport()
    {
        lock (GridLock)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Insert tracking jitter benchmark");
            sb.AppendLine();
            sb.AppendLine("Units: source pixels unless a row says otherwise. Slip = frame-to-frame change of the corner error vector (RMS over corners and frames);");
            sb.AppendLine("Hf = error minus its centred 1 s moving average; ContentSlip = slip of a 5x5 grid of content points. See `JitterMetrics.cs` for every definition.");
            sb.AppendLine("Regenerate: `REELBOLT_JITTER_BENCH=1 dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter \"FullyQualifiedName~InsertJitter\"` (from `inference/`).");
            sb.AppendLine();
            sb.AppendLine("### Deviations from the ticket (and why)");
            sb.AppendLine();
            sb.AppendLine("- Scene geometry that cannot fit the 1344x768 frame as written was adjusted: `slow-push` starts 197 px left of the real plate (400 px of travel + 1.25x growth would otherwise leave the frame); `turn` uses the plate at 0.85 scale centred on the frame (a 35 degree turn of the full plate leaves the frame); `whip` starts 250 px left of the real plate (360 px of travel); `static-small` is a portrait (0.46) plate of 0.13 frame area; `microshake` is 1.5 px RMS per axis.");
            sb.AppendLine("- `S0-coarse` is recovered by re-running `DetectQuads` with component retention and applying `ChromaQuadOrientation.DominantAngle/ExtremeCorners` to the grown component (the same call `FitQuad` makes before the line fit). Stages S1-S4 are replicated stage by stage in `Track()`'s order; every stage body is the production method. The max difference between the replica's S4 and `Track()` is printed per scene.");
            sb.AppendLine("- Stages are scored on the corners the compile step pins to (`surfaceCorners` when present, else `x0..y3`). `S2a` (assembly + trim only) is an extra row so the edge refiner can be attributed separately.");
            sb.AppendLine("- `LagXc` is the ticket's velocity cross-correlation (normalized; `-` when the peak sits on the search boundary, which is what slow smooth motion gives, and for a static truth). `LagLS` is an added position-domain delay estimate that stays well conditioned on slow motion.");
            sb.AppendLine("- `S5a` scores the keyframe expressions against the truth pushed through the same overscan (so overscan is not counted as error); `S5b` is the staging setup: 1920x1080 Crop (`SourceCanvasFit.For(..., cover: true)`) from 1344x768, matted overscan 0.10; rows labelled `output px` are in 1920x1080 pixels (x1.4286). Frame indices follow `ScreenInsertFilterBuilder` (`in` is 1-based); the corner expressions are evaluated by parsing the production filter string, and checked against an independent piecewise-linear reading.");
            sb.AppendLine("- `S6` runs the real unmatted flat-path chain (`ScreenInsertFilterBuilder.BuildFilterChain`: scale, 1 px border pad, `perspective ... sense=destination:eval=frame`, mask warp, `alphamerge`, `overlay`) over the encoded plate at overscan 0.02 for `static-1344` and `slow-push`. No plate matte, reflection transfer or guard fill. The re-detected edge is measured twice: a magenta insert (chroma-only contrast, which the yuv420p output resolves at half resolution) and a white insert (luma contrast). Controls: a PERFECT tracker (truth as keyframes) through the same chain, and the same re-detection on an analytic plate with no ffmpeg. The 1 px black border makes the visible quad about 1-2 px inside the corners: a constant offset (ErrMean), not wobble.");
            sb.AppendLine("- Sweeps (`o1`-`o5`) call the real `Track()` per variant. `o1` is split into `o1` (no noise, lossless RGB: the ticket's oracle), `o1b` (no noise, standard lossy encode: codec only), `o1c` (noise, lossless: noise only) and `o1d` (no noise, lossless 4:2:0: chroma subsampling only). `o2` 640 is 630x360 (the 360 px height cap at the plate's aspect).");
            sb.AppendLine();
            foreach (string f in Directory.GetFiles(SectionsDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            {
                sb.AppendLine(File.ReadAllText(f));
                sb.AppendLine();
            }

            File.WriteAllText(Path.Combine(OutDir, "report.md"), sb.ToString());
        }
    }

    // -------------------------------------------------------------------------------------------
    // Real artifacts
    // -------------------------------------------------------------------------------------------

    /// <summary>The first insert region's keyframes of a stored analysis artifact, in source pixels.</summary>
    public static (List<Quad> Surface, List<Quad> Corners, int W, int H) LoadArtifact(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement media = doc.RootElement.GetProperty("media");
        int w = media.GetProperty("width").GetInt32(), h = media.GetProperty("height").GetInt32();
        JsonElement kfs = doc.RootElement.GetProperty("insertRegions")[0].GetProperty("keyframes");
        var surface = new List<Quad>();
        var corners = new List<Quad>();
        foreach (JsonElement k in kfs.EnumerateArray())
        {
            double t = k.GetProperty("timeSec").GetDouble();
            double[] n = new[] { "x0", "y0", "x1", "y1", "x2", "y2", "x3", "y3" }.Select(p => k.GetProperty(p).GetDouble()).ToArray();
            corners.Add(new Quad(t, n[0] * w, n[1] * h, n[2] * w, n[3] * h, n[4] * w, n[5] * h, n[6] * w, n[7] * h));
            if (k.TryGetProperty("surfaceCorners", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
            {
                double[] m = new[] { "x0", "y0", "x1", "y1", "x2", "y2", "x3", "y3" }.Select(p => s.GetProperty(p).GetDouble()).ToArray();
                surface.Add(new Quad(t, m[0] * w, m[1] * h, m[2] * w, m[3] * h, m[4] * w, m[5] * h, m[6] * w, m[7] * h));
            }
            else
            {
                surface.Add(corners[^1]);
            }
        }

        return (surface, corners, w, h);
    }
}
