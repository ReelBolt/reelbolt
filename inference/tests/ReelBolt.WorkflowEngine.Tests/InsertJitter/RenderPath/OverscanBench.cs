#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.BenchSupport;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 item 3. <c>BuildInsertKeyframes</c> expands every keyframe about ITS OWN centroid
/// (<c>c + (q - c) * (1 + overscan)</c>). Candidates below are TEST-ONLY re-implementations of that one step on the same inputs (the shipped
/// tracker's S4 quads read from the baseline bench csv), scored against the truth expanded the same way. The expansion is pure geometry on
/// the quad, so no ffmpeg is involved: the comparison is exact, in source px.
/// <list type="bullet">
/// <item><b>P</b> production: per-keyframe centroid.</item>
/// <item><b>F</b> PROTOTYPE one fixed expansion vector per corner for the whole track (the mean of the per-keyframe vectors).</item>
/// <item><b>M</b> PROTOTYPE additive expansion vector <c>o * (S - centroid(S))</c> computed from a temporally smoothed copy S of the quad (centred 15-frame mean), added to the raw quad.</item>
/// <item><b>H</b> PROTOTYPE extrapolation in the quad's own projective frame (<c>u,v</c> from <c>-o/2</c> to <c>1+o/2</c>), as the mesh path does for its cells.</item>
/// </list>
/// </summary>
public class OverscanBench
{
    private readonly ITestOutputHelper _output;

    public OverscanBench(ITestOutputHelper output) => _output = output;

    private static (double X, double Y) Centroid(Quad q) => ((q.X0 + q.X1 + q.X2 + q.X3) / 4, (q.Y0 + q.Y1 + q.Y2 + q.Y3) / 4);

    internal static Quad Expand(Quad q, double o)
    {
        (double cx, double cy) = Centroid(q);
        (double X, double Y) E(int c) => (cx + (q.Corner(c).X - cx) * (1 + o), cy + (q.Corner(c).Y - cy) * (1 + o));
        return Quad.From(q.T, E(0), E(1), E(2), E(3));
    }

    internal static List<Quad> Fixed(IReadOnlyList<Quad> raw, double o)
    {
        var mean = new (double X, double Y)[4];
        foreach (Quad q in raw)
        {
            (double cx, double cy) = Centroid(q);
            for (int c = 0; c < 4; c++)
                mean[c] = (mean[c].X + o * (q.Corner(c).X - cx) / raw.Count, mean[c].Y + o * (q.Corner(c).Y - cy) / raw.Count);
        }

        return raw.Select(q => Quad.From(q.T,
            (q.X0 + mean[0].X, q.Y0 + mean[0].Y), (q.X1 + mean[1].X, q.Y1 + mean[1].Y), (q.X2 + mean[2].X, q.Y2 + mean[2].Y), (q.X3 + mean[3].X, q.Y3 + mean[3].Y))).ToList();
    }

    internal static List<Quad> Smoothed(IReadOnlyList<Quad> raw, double o, int window)
    {
        int half = window / 2;
        var result = new List<Quad>(raw.Count);
        for (int k = 0; k < raw.Count; k++)
        {
            int a = Math.Max(0, k - half), b = Math.Min(raw.Count - 1, k + half);
            var s = new double[8];
            for (int m = a; m <= b; m++)
            {
                for (int c = 0; c < 4; c++)
                {
                    s[2 * c] += raw[m].Corner(c).X / (b - a + 1);
                    s[2 * c + 1] += raw[m].Corner(c).Y / (b - a + 1);
                }
            }

            double cx = (s[0] + s[2] + s[4] + s[6]) / 4, cy = (s[1] + s[3] + s[5] + s[7]) / 4;
            Quad q = raw[k];
            (double X, double Y) A(int c) => (q.Corner(c).X + o * (s[2 * c] - cx), q.Corner(c).Y + o * (s[2 * c + 1] - cy));
            result.Add(Quad.From(q.T, A(0), A(1), A(2), A(3)));
        }

        return result;
    }

    internal static List<Quad> Projective(IReadOnlyList<Quad> raw, double o) =>
        raw.Select(q =>
        {
            UnitSquareMap m = q.ToMap();
            return Quad.From(q.T, m.Map(-o / 2, -o / 2), m.Map(1 + o / 2, -o / 2), m.Map(-o / 2, 1 + o / 2), m.Map(1 + o / 2, 1 + o / 2));
        }).ToList();

    [JitterBenchFact]
    public void Overscan_expansion_amplification()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Overscan: per-keyframe centroid expansion vs alternatives (item 3)");
        sb.AppendLine();
        sb.AppendLine("Input = the shipped tracker's S4 quads (baseline bench csv, source px); reference = the truth expanded about its own centroid by the same overscan. `raw` = the S4 track vs truth with no expansion. **Amp** = SlipRms(candidate) / SlipRms(raw). For independent per-corner noise the closed forms are: per-keyframe centroid `sqrt(1+1.5o+0.75o^2)` (1.015 at o=0.02, 1.076 at 0.10), a fixed centroid or fixed expansion `1+o`... as measured below. P = production, F/M/H = PROTOTYPES (see class summary).");
        sb.AppendLine();
        var summary = new List<(string Scene, double O, double Raw, double P, double F, double M, double H)>();
        foreach (JitterScene scene in BenchSupport.SelectedScenes(RenderScenes.AllScenes))
        {
            string csv = Path.Combine(RenderScenes.BaselineDir, "csv", scene.Name + "-S4-artifact.csv");
            List<Quad> raw = ReadCsvTrack(csv);
            IReadOnlyList<Quad> truth = scene.Truth();
            JitterStats rawStats = JitterMetrics.Compute(raw, truth, scene.Fps);
            sb.AppendLine($"### {scene.Name}");
            sb.AppendLine();
            foreach (double o in new[] { 0.02, 0.10 })
            {
                var truthExp = truth.Select(q => Expand(q, o)).ToList();
                var rows = new List<Row> { new("raw S4 vs truth (no overscan)", rawStats) };
                List<Quad> p = raw.Select(q => Expand(q, o)).ToList();
                List<Quad> f = Fixed(raw, o);
                List<Quad> m = Smoothed(raw, o, 15);
                List<Quad> h = Projective(raw, o);
                var stats = new[] { p, f, m, h }.Select(c => JitterMetrics.Compute(c, truthExp, scene.Fps)).ToArray();
                rows.Add(new Row($"P per-keyframe centroid (production), o={o:0.00}", stats[0]));
                rows.Add(new Row($"F fixed expansion vector per corner, o={o:0.00}", stats[1]));
                rows.Add(new Row($"M additive expansion from smoothed shape, o={o:0.00}", stats[2]));
                rows.Add(new Row($"H projective-frame extrapolation, o={o:0.00}", stats[3]));
                sb.AppendLine(Table(rows));
                summary.Add((scene.Name, o, rawStats.SlipRms, stats[0].SlipRms, stats[1].SlipRms, stats[2].SlipRms, stats[3].SlipRms));
            }
        }

        sb.AppendLine("### Slip amplification summary (SlipRms px, source px)");
        sb.AppendLine();
        sb.AppendLine("| scene | o | raw | P | F | M | H | Amp P | Amp F | Amp M | Amp H |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in summary)
        {
            string R(double a, double b) => b > 1e-6 ? (a / b).ToString("0.000", CultureInfo.InvariantCulture) : "-";
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {s.Scene} | {s.O:0.00} | {s.Raw:0.000} | {s.P:0.000} | {s.F:0.000} | {s.M:0.000} | {s.H:0.000} | {R(s.P, s.Raw)} | {R(s.F, s.Raw)} | {R(s.M, s.Raw)} | {R(s.H, s.Raw)} |"));
        }

        WriteSection("53-overscan", sb.ToString());
    }
}
