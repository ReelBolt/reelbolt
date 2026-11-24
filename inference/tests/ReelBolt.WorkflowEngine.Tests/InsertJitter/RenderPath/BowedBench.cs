#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 item 4. A gently BOWED plate (the baseline scenes have straight edges): an axis-aligned plate whose four edges follow the mesh
/// path's own profile <c>dev(t) = 4t(1-t)(Bow + Skew(2t-1))</c> (fractions of the across-plate vector, positive = inward), moving like the
/// baseline <c>static-1344</c> / <c>slow-push</c> plates, rendered with the same noise and encode as the baseline scenes and tracked by the REAL
/// tracker with curvature measurement on. The mesh path builds each keyframe's control grid from that keyframe's OWN measured bows
/// (<c>BuildInsertMeshPlan</c>, <c>InsertSurfaceMesh.BuildControlPoints</c>) and uses the median bow only to choose the cell count. This bench
/// scores the 5x5 content-point positions that grid implies (the metric the baseline calls ContentSlip) for bows used as measured (production),
/// median over the track, a temporal mean (PROTOTYPES), zero (what the flat path assumes) and the truth (oracle). Geometry only; the cells' ffmpeg
/// render is the flat path's perspective per cell, covered by the linearity microbench.
/// </summary>
public class BowedBench
{
    private readonly ITestOutputHelper _output;

    public BowedBench(ITestOutputHelper output) => _output = output;

    private sealed record BowedScene(string Name, string Base, Func<int, InsertPlateBows> Bows, string Note);

    private static InsertPlateBows ConstantBows { get; } = new(
        new InsertEdgeBow(0.060, 0.010), new InsertEdgeBow(0.030, 0.0), new InsertEdgeBow(0.050, -0.010), new InsertEdgeBow(0.025, 0.005));

    private static double Smooth(double t) => t * t * (3 - 2 * t);

    private static readonly BowedScene[] Scenes =
    [
        new("bowed-static", "static-1344", _ => ConstantBows, "locked-off plate, constant truth bows (top 0.060, right 0.030, bottom 0.050, left 0.025 of the across vector, i.e. a barrel-curved device: the tracker treats a line-fit bow under 0.025 as flat and measures nothing, so gentler bows never reach the mesh path; 25 px sagitta on the top edge)"),
        new("bowed-push", "slow-push", _ => ConstantBows, "slow-push plate path (400 px of travel, x1.25 growth), constant truth bows"),
        new("bowed-grow", "slow-push", f =>
        {
            double s = Smooth(Math.Clamp(f / 239.0, 0, 1));
            double Lerp(double a, double b) => a + (b - a) * s;
            return new InsertPlateBows(
                new InsertEdgeBow(Lerp(0.040, 0.080), 0.010), new InsertEdgeBow(Lerp(0.020, 0.040), 0), new InsertEdgeBow(Lerp(0.035, 0.065), -0.010), new InsertEdgeBow(Lerp(0.015, 0.035), 0.005));
        }, "slow-push path with truth bows that GROW (top 0.040 to 0.080), i.e. the bow genuinely changes over the insert")
    ];

    private static Quad RectAt(JitterScene baseScene, int f)
    {
        Quad q = baseScene.TruthAt(f);
        double x0 = (q.X0 + q.X2) / 2, x1 = (q.X1 + q.X3) / 2, y0 = (q.Y0 + q.Y1) / 2, y1 = (q.Y2 + q.Y3) / 2;
        return Quad.From(f / baseScene.Fps, (x0, y0), (x1, y0), (x0, y1), (x1, y1));
    }

    /// <summary>Renders (or reuses) the bowed scene clip; returns the clip path.</summary>
    private static string Render(BowedScene s, string dir)
    {
        JitterScene b = JitterScenes.Get(s.Base);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, s.Name + ".mp4");
        string stamp = path + ".stamp";
        string want = "v2|" + s.Name;
        if (File.Exists(path) && File.Exists(stamp) && File.ReadAllText(stamp) == want)
            return path;
        int w = b.Width, h = b.Height;
        byte[] tex = b.Texture();
        int tw = w + 2 * 24;
        string tmp = path + ".part.mp4";
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-r", "24", "-i", "pipe:0", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-f", "mp4", tmp })
            psi.ArgumentList.Add(a);
        using Process ff = Process.Start(psi)!;
        var err = ff.StandardError.ReadToEndAsync();
        _ = ff.StandardOutput.ReadToEndAsync();
        var frame = new byte[w * h * 3];
        ulong seed = JitterScene.Fnv1a(s.Name);
        Stream stdin = ff.StandardInput.BaseStream;
        for (int f = 0; f < b.Frames; f++)
        {
            Parallel.For(0, h, y => Buffer.BlockCopy(tex, ((y + 24) * tw + 24) * 3, frame, y * w * 3, w * 3));
            PaintBowedPlate(frame, w, h, RectAt(b, f), s.Bows(f), (0, 236, 1));
            Parallel.For(0, h, y =>
            {
                ulong state = JitterScene.SplitMix(seed ^ ((ulong)f << 20) ^ (ulong)y * 0x9E3779B97F4A7C15UL ^ 0x5EED);
                int row = y * w * 3;
                for (int i = 0; i < w * 3; i += 2)
                {
                    state = JitterScene.SplitMix(state);
                    double u1 = 1 - (state >> 11) * (1.0 / (1UL << 53));
                    state = JitterScene.SplitMix(state);
                    double u2 = (state >> 11) * (1.0 / (1UL << 53));
                    double mag = 2.0 * Math.Sqrt(-2 * Math.Log(u1));
                    frame[row + i] = (byte)Math.Clamp((int)Math.Round(frame[row + i] + mag * Math.Cos(2 * Math.PI * u2)), 0, 255);
                    if (i + 1 < w * 3)
                        frame[row + i + 1] = (byte)Math.Clamp((int)Math.Round(frame[row + i + 1] + mag * Math.Sin(2 * Math.PI * u2)), 0, 255);
                }
            });
            stdin.Write(frame, 0, frame.Length);
        }

        stdin.Close();
        ff.WaitForExit();
        if (ff.ExitCode != 0)
            throw new InvalidOperationException("bowed render failed: " + err.Result);
        File.Move(tmp, path, overwrite: true);
        File.WriteAllText(stamp, want);
        return path;
    }

    private static bool Inside(Quad r, InsertPlateBows bows, double qx, double qy)
    {
        double x0 = r.X0, x1 = r.X1, y0 = r.Y0, y1 = r.Y2, ww = x1 - x0, hh = y1 - y0;
        double t = (qx - x0) / ww, sv = (qy - y0) / hh;
        if (t < 0 || t > 1 || sv < 0 || sv > 1)
            return false;
        double top = y0 + hh * bows.Top.Evaluate(t), bottom = y1 - hh * bows.Bottom.Evaluate(t);
        double left = x0 + ww * bows.Left.Evaluate(sv), right = x1 - ww * bows.Right.Evaluate(sv);
        return qy >= top && qy <= bottom && qx >= left && qx <= right;
    }

    private static void PaintBowedPlate(byte[] dst, int w, int h, Quad r, InsertPlateBows bows, (int R, int G, int B) colour)
    {
        double x0 = r.X0, x1 = r.X1, y0 = r.Y0, y1 = r.Y2, ww = x1 - x0, hh = y1 - y0;
        int px0 = Math.Max(0, (int)x0 - 2), px1 = Math.Min(w - 1, (int)x1 + 2), py0 = Math.Max(0, (int)y0 - 2), py1 = Math.Min(h - 1, (int)y1 + 2);
        Parallel.For(py0, py1 + 1, y =>
        {
            for (int x = px0; x <= px1; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double t = Math.Clamp((px - x0) / ww, 0, 1), sv = Math.Clamp((py - y0) / hh, 0, 1);
                double top = y0 + hh * bows.Top.Evaluate(t), bottom = y1 - hh * bows.Bottom.Evaluate(t);
                double left = x0 + ww * bows.Left.Evaluate(sv), right = x1 - ww * bows.Right.Evaluate(sv);
                double dmin = Math.Min(Math.Min(py - top, bottom - py), Math.Min(px - left, right - px));
                double cov;
                if (dmin >= 1.5)
                {
                    cov = 1;
                }
                else if (dmin <= -1.5)
                {
                    continue;
                }
                else
                {
                    int inside = 0;
                    for (int sy = 0; sy < 8; sy++)
                    {
                        for (int sx = 0; sx < 8; sx++)
                        {
                            if (Inside(r, bows, x + (sx + 0.5) / 8, y + (sy + 0.5) / 8))
                                inside++;
                        }
                    }

                    cov = inside / 64.0;
                    if (cov == 0)
                        continue;
                }

                int o = (y * w + x) * 3;
                dst[o] = (byte)Math.Clamp((int)Math.Round(dst[o] * (1 - cov) + colour.R * cov), 0, 255);
                dst[o + 1] = (byte)Math.Clamp((int)Math.Round(dst[o + 1] * (1 - cov) + colour.G * cov), 0, 255);
                dst[o + 2] = (byte)Math.Clamp((int)Math.Round(dst[o + 2] * (1 - cov) + colour.B * cov), 0, 255);
            }
        });
    }

    private static readonly double[] GridUv = [0.1, 0.3, 0.5, 0.7, 0.9];

    private static (double X, double Y)[] ContentPoints(Quad corners, InsertPlateBows bows)
    {
        InsertSurfaceMesh.UnitSquareHomography? h = InsertSurfaceMesh.UnitSquareHomography.Solve(corners.Corner(0), corners.Corner(1), corners.Corner(2), corners.Corner(3));
        var pts = new (double, double)[GridUv.Length * GridUv.Length];
        if (h is null)
            return pts;
        int n = 0;
        foreach (double v in GridUv)
        {
            foreach (double u in GridUv)
                pts[n++] = InsertSurfaceMesh.MapPoint(h, bows, u, v);
        }

        return pts;
    }

    private static InsertPlateBows Median(IReadOnlyList<InsertPlateBows> all) => VideoCompileStepExecutor.MedianBows(all);

    private static InsertPlateBows Mean(IReadOnlyList<InsertPlateBows> all, int k, int window)
    {
        int a = Math.Max(0, k - window / 2), b = Math.Min(all.Count - 1, k + window / 2);
        InsertEdgeBow E(Func<InsertPlateBows, InsertEdgeBow> p) =>
            new(Enumerable.Range(a, b - a + 1).Average(i => p(all[i]).Bow), Enumerable.Range(a, b - a + 1).Average(i => p(all[i]).Skew));
        return new InsertPlateBows(E(x => x.Top), E(x => x.Right), E(x => x.Bottom), E(x => x.Left));
    }

    private static Quad MeanCorners(IReadOnlyList<Quad> all, int k, int window)
    {
        int a = Math.Max(0, k - window / 2), b = Math.Min(all.Count - 1, k + window / 2);
        (double X, double Y) M(int c) => (Enumerable.Range(a, b - a + 1).Average(i => all[i].Corner(c).X), Enumerable.Range(a, b - a + 1).Average(i => all[i].Corner(c).Y));
        return Quad.From(all[k].T, M(0), M(1), M(2), M(3));
    }

    private sealed record Candidate(string Label, Func<int, InsertPlateBows> Bows, Func<int, Quad>? Corners = null);

    [JitterBenchFact]
    public void Bowed_plate_per_keyframe_vs_median_bows()
    {
        string clips = Path.Combine(OutDir, "clips");
        var sb = new StringBuilder();
        sb.AppendLine("## Bowed plate: per-keyframe bows vs median (item 4)");
        sb.AppendLine();
        sb.AppendLine("Synthetic bowed plates (`BowedBench`), rendered with the baseline scenes' background, 4x4+ anti-aliasing, sensor noise sigma 2 and crf 20 4:2:0 encode, tracked by the real `ChromaQuadTracker.Track` with the production options (curvature and chroma-key solving on). Corners = what the compile step pins to (the tracker's `surfaceCorners` when present); bows as listed. Error = position of a 5x5 grid of content points (u,v in 0.1..0.9) through `InsertSurfaceMesh.MapPoint` vs the same points through the truth plate (truth corners = the plate's rectangle, truth bows). **ContentSlip** = RMS frame-to-frame change of that error vector (px, source px); **Err** = position error; **bow RMS** = RMS error of the 8 measured bow parameters vs truth (dimensionless; x420 px for a feel of mid-edge pixels). Rows marked PROTOTYPE replace the per-keyframe bows by a centred mean over 9 / 25 frames or by the median over the track (and, in the last two rows, smooth the corners the same way); `measured` is production (each keyframe builds its own grid). The oracle row of an earlier draft (truth bows on the tracker's corners) was dropped: the tracker's corners and bows are fitted TOGETHER (bows are relative to its fitted chord), so truth bows on its corners is an inconsistent pair and scores worse than either side.");
        sb.AppendLine();
        foreach (BowedScene s in Scenes)
        {
            string clip = Render(s, clips);
            JitterScene b = JitterScenes.Get(s.Base);
            VideoAnalyzeStepConfig cfg = DefaultConfig;
            (int gw, int gh) = ChromaQuadTracker.DeriveGridSize(b.Width, b.Height, cfg.InsertGridWidth, cfg.InsertGridHeight);
            int cap = ChromaQuadTracker.SampleFrameCap(gw, gh, Math.Clamp(cfg.MaxInsertSampleFrames, 10, 20_000));
            double fps = ChromaQuadTracker.ResolveSampleFps(cfg.InsertSampleFps, b.Fps, b.Frames / b.Fps, gw, gh, cap);
            byte[] grid = SampleGrid(clip, fps, gw, gh);
            int frames = grid.Length / (gw * gh * 3);
            IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(grid, frames, gw, gh, fps, ProductionOptions(cfg), b.Width, b.Height);
            VideoInsertRegionTrack? track = Longest(tracks);
            sb.AppendLine($"### {s.Name}");
            sb.AppendLine();
            sb.AppendLine($"{s.Note}. Tracker: {tracks.Count} track(s); " + (track is null ? "none." : $"{track.Keyframes.Count} keyframes, {track.Keyframes.Count(k => k.Bows is not null)} with bows, {track.Keyframes.Count(k => k.SurfaceCorners is not null)} with surface corners."));
            sb.AppendLine();
            if (track is null)
                continue;

            var measuredCorners = new List<Quad>();
            var truthCorners = new List<Quad>();
            var measuredBows = new List<InsertPlateBows>();
            var truthBows = new List<InsertPlateBows>();
            foreach (VideoInsertQuadKeyframe k in track.Keyframes)
            {
                int g = (int)Math.Round(k.TimeSec * b.Fps);
                measuredCorners.Add(Quad.FromKeyframe(k, b.Width, b.Height));
                truthCorners.Add(RectAt(b, g));
                measuredBows.Add(VideoCompileStepExecutor.FromDto(k.Bows));
                truthBows.Add(s.Bows(g));
            }

            InsertPlateBows median = Median(measuredBows);
            var (rows, cols) = InsertSurfaceMesh.ChooseDensity(median, truthCorners.Average(q => q.X1 - q.X0), truthCorners.Average(q => q.Y2 - q.Y0), 0.6, 24);
            sb.AppendLine($"Median measured bows: top {median.Top.Bow:0.0000}/{median.Top.Skew:0.0000}, right {median.Right.Bow:0.0000}/{median.Right.Skew:0.0000}, bottom {median.Bottom.Bow:0.0000}/{median.Bottom.Skew:0.0000}, left {median.Left.Bow:0.0000}/{median.Left.Skew:0.0000} (bow/skew); mesh gate: {(median.IsFlat ? "flat" : "curved")}, density {rows}x{cols} cells (tolerance 0.6 px, max 24).");
            sb.AppendLine();

            var cands = new List<Candidate>
            {
                new("flat (zero bows: what the planar path assumes)", _ => InsertPlateBows.Flat),
                new("measured per keyframe (PRODUCTION)", k => measuredBows[k]),
                new("PROTOTYPE smoothed, centred 9-frame mean", k => Mean(measuredBows, k, 9)),
                new("PROTOTYPE smoothed, centred 25-frame mean", k => Mean(measuredBows, k, 25)),
                new("PROTOTYPE median over the track", _ => median),
                new("PROTOTYPE corners AND bows both smoothed, centred 9-frame mean", k => Mean(measuredBows, k, 9), k => MeanCorners(measuredCorners, k, 9)),
                new("PROTOTYPE corners AND bows both smoothed, centred 25-frame mean", k => Mean(measuredBows, k, 25), k => MeanCorners(measuredCorners, k, 25))
            };

            sb.AppendLine("| bows used | Err mean px | Err max px | ContentSlip px | Hf px | bow RMS (frac) | bow RMS (px, x420) |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach (Candidate c in cands)
            {
                var errs = new List<double[]>();
                var errAll = new List<double>();
                for (int k = 0; k < measuredCorners.Count; k++)
                {
                    (double X, double Y)[] cand = ContentPoints(c.Corners?.Invoke(k) ?? measuredCorners[k], c.Bows(k));
                    (double X, double Y)[] truth = ContentPoints(truthCorners[k], truthBows[k]);
                    var e = new double[cand.Length * 2];
                    for (int p = 0; p < cand.Length; p++)
                    {
                        e[2 * p] = cand[p].X - truth[p].X;
                        e[2 * p + 1] = cand[p].Y - truth[p].Y;
                        errAll.Add(Math.Sqrt(e[2 * p] * e[2 * p] + e[2 * p + 1] * e[2 * p + 1]));
                    }

                    errs.Add(e);
                }

                var slip = new List<double>();
                for (int k = 1; k < errs.Count; k++)
                {
                    for (int p = 0; p < errs[k].Length; p += 2)
                    {
                        double dx = errs[k][p] - errs[k - 1][p], dy = errs[k][p + 1] - errs[k - 1][p + 1];
                        slip.Add(dx * dx + dy * dy);
                    }
                }

                // High-frequency: error minus centred 1 s mean.
                var hf = new List<double>();
                int half = (int)Math.Round(b.Fps / 2);
                for (int k = half; k < errs.Count - half; k++)
                {
                    for (int p = 0; p < errs[k].Length; p += 2)
                    {
                        double mx = 0, my = 0;
                        for (int m = k - half; m <= k + half; m++)
                        {
                            mx += errs[m][p];
                            my += errs[m][p + 1];
                        }

                        mx /= 2 * half + 1;
                        my /= 2 * half + 1;
                        hf.Add((errs[k][p] - mx) * (errs[k][p] - mx) + (errs[k][p + 1] - my) * (errs[k][p + 1] - my));
                    }
                }

                var bowErr = new List<double>();
                for (int k = 0; k < measuredBows.Count; k++)
                {
                    InsertPlateBows cb = c.Bows(k), tb = truthBows[k];
                    foreach ((InsertEdgeBow a, InsertEdgeBow t) in new[] { (cb.Top, tb.Top), (cb.Right, tb.Right), (cb.Bottom, tb.Bottom), (cb.Left, tb.Left) })
                    {
                        bowErr.Add((a.Bow - t.Bow) * (a.Bow - t.Bow));
                        bowErr.Add((a.Skew - t.Skew) * (a.Skew - t.Skew));
                    }
                }

                double bowRms = Math.Sqrt(bowErr.Average());
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {c.Label} | {errAll.Average():0.000} | {errAll.Max():0.000} | {Math.Sqrt(slip.Average()):0.000} | {(hf.Count == 0 ? double.NaN : Math.Sqrt(hf.Average())):0.000} | {bowRms:0.0000} | {bowRms * 420:0.00} |"));
                _output.WriteLine($"{s.Name} {c.Label}: slip {Math.Sqrt(slip.Average()):0.000}");
            }

            sb.AppendLine();
        }

        WriteSection("54-bowed", sb.ToString());
    }
}
