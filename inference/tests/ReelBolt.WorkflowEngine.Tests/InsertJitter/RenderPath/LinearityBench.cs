#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;

/// <summary>
/// T-005 item 1: how linearly does ffmpeg <c>perspective</c> (the production expression form) turn a commanded sub-pixel
/// corner offset into a rendered edge? A white insert on a flat black base, ONE quad swept through sub-pixel offsets
/// (translation in x and y at 0.05 px steps, a slow rotation, a slow scale), every frame an independent sample; the
/// composite stays 4:4:4 (overlay <c>format=yuv444</c>) and is piped as its luma plane, so output chroma subsampling
/// cannot enter. The edge is measured with the integrating <see cref="EdgeMeter"/> at native resolution.
/// </summary>
public class LinearityBench
{
    private readonly ITestOutputHelper _output;

    public LinearityBench(ITestOutputHelper output) => _output = output;

    private static readonly double[] Ratios = [0.33, 0.52, 0.97];

    private sealed record Sweep(string Kind, int Steps, double StepUnit, string UnitLabel);

    private static readonly Sweep[] Sweeps =
    [
        new("x", 41, 0.05, "px"),
        new("y", 41, 0.05, "px"),
        new("rot", 41, 0.0125, "deg"),
        new("scale", 41, 0.0005, "frac")
    ];

    private static Quad BaseQuad(double ratio)
    {
        double w = 1344 * ratio, h = 768 * ratio;
        return Quad.From(0, (672 - w / 2, 384 - h / 2), (672 + w / 2, 384 - h / 2), (672 - w / 2, 384 + h / 2), (672 + w / 2, 384 + h / 2));
    }

    private static List<Quad> BuildSweep(Sweep s, double ratio)
    {
        Quad b = BaseQuad(ratio);
        var quads = new List<Quad>();
        for (int i = 0; i < s.Steps; i++)
        {
            double d = i * s.StepUnit;
            (double X, double Y) P(int c)
            {
                (double x, double y) = b.Corner(c);
                switch (s.Kind)
                {
                    case "x": return (x + d, y);
                    case "y": return (x, y + d);
                    case "rot":
                        double a = d * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
                        return (672 + (x - 672) * ca - (y - 384) * sa, 384 + (x - 672) * sa + (y - 384) * ca);
                    default: return (672 + (x - 672) * (1 + d), 384 + (y - 384) * (1 + d));
                }
            }

            quads.Add(Quad.From(i / 24.0, P(0), P(1), P(2), P(3)));
        }

        return quads;
    }

    /// <summary>Residual of measured vs commanded after removing the best affine function of the step index per coordinate.</summary>
    internal static (double Rms, double Max, double[] MeanSeries) Residual(IReadOnlyList<Quad> measured, IReadOnlyList<Quad> commanded)
    {
        int n = Math.Min(measured.Count, commanded.Count);
        var all = new List<double>();
        var meanSeries = new double[n];
        for (int c = 0; c < 4; c++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                var r = new double[n];
                for (int i = 0; i < n; i++)
                {
                    (double mx, double my) = measured[i].Corner(c);
                    (double cx, double cy) = commanded[i].Corner(c);
                    r[i] = axis == 0 ? mx - cx : my - cy;
                }

                double mi = (n - 1) / 2.0, mr = r.Average();
                double sxx = 0, sxy = 0;
                for (int i = 0; i < n; i++)
                {
                    sxx += (i - mi) * (i - mi);
                    sxy += (i - mi) * (r[i] - mr);
                }

                double slope = sxx > 0 ? sxy / sxx : 0;
                for (int i = 0; i < n; i++)
                {
                    double res = r[i] - mr - slope * (i - mi);
                    all.Add(res);
                    meanSeries[i] += res / 8.0;
                }
            }
        }

        return (Math.Sqrt(all.Average(v => v * v)), all.Max(Math.Abs), meanSeries);
    }

    /// <summary>Dominant period (in steps) and amplitude of a short series by direct DFT over periods 3..n steps.</summary>
    internal static (double Period, double Amp) Dominant(double[] series)
    {
        int n = series.Length;
        double mean = series.Average();
        double bestAmp = 0, bestPeriod = 0;
        for (double period = 3; period <= n; period += 0.25)
        {
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double ph = 2 * Math.PI * i / period;
                re += (series[i] - mean) * Math.Cos(ph);
                im += (series[i] - mean) * Math.Sin(ph);
            }

            double amp = 2 * Math.Sqrt(re * re + im * im) / n;
            if (amp > bestAmp)
            {
                bestAmp = amp;
                bestPeriod = period;
            }
        }

        return (bestPeriod, bestAmp);
    }

    private static RigInput InputFor(List<Quad> quads, string content = "0xFFFFFF")
    {
        var keys = quads.Select((q, i) => new InsertQuadFrame(i, q.X0, q.Y0, q.X1, q.Y1, q.X2, q.Y2, q.X3, q.Y3)).ToList();
        return new RigInput(1344, 768, 24, 1, quads.Count, keys, quads, content, null, "0x000000");
    }

    [JitterBenchFact]
    public void Perspective_linearity_microbench()
    {
        string dir = Path.Combine(BenchSupport.OutDir, "linearity");
        Directory.CreateDirectory(dir);
        var variants = new List<RigVariant>
        {
            new("lin", "production (linear, 1 px bordered frame pinned)", OverlayFormat: "yuv444", OutPixFmt: "gray"),
            new("cub", "interpolation=cubic", Interpolation: "cubic", OverlayFormat: "yuv444", OutPixFmt: "gray"),
            new("comp", "PROTOTYPE border-compensated pin (white region = commanded quad)", OverlayFormat: "yuv444", OutPixFmt: "gray", CompensateBorder: true),
            new("pre", "PROTOTYPE content+mask prescaled to the quad (perspective near 1:1)", OverlayFormat: "yuv444", OutPixFmt: "gray", Prescale: true, CompensateBorder: true),
            new("rep", "PROTOTYPE border-compensated + content smeared past the edge (no black rim in the content)", OverlayFormat: "yuv444", OutPixFmt: "gray", CompensateBorder: true, ContentEdgeReplicate: true),
            new("prerep", "PROTOTYPE prescaled + content smeared past the edge", OverlayFormat: "yuv444", OutPixFmt: "gray", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true),
            new("prerepcub", "PROTOTYPE prescaled + smeared content + interpolation=cubic", Interpolation: "cubic", OverlayFormat: "yuv444", OutPixFmt: "gray", Prescale: true, CompensateBorder: true, ContentEdgeReplicate: true),
            new("ss2", "PROTOTYPE mask supersampled 2x then area-averaged", OverlayFormat: "yuv444", OutPixFmt: "gray", MaskSupersample: 2, CompensateBorder: true),
            new("maskonly", "warped mask alone (no alphamerge/overlay)", OverlayFormat: "yuv444", OutPixFmt: "gray", MaskOnly: true, CompensateBorder: true)
        };

        var jobs = (from r in Ratios from s in Sweeps from v in variants select (r, s, v)).ToList();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, (double Rms, double Max, double Slip, double Period, double Amp, int Fail, List<Quad> Measured, List<Quad> Commanded)>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 6 }, job =>
        {
            (double ratio, Sweep sweep, RigVariant v) = job;
            List<Quad> quads = BuildSweep(sweep, ratio);
            RigResult res = RenderRig.Run(InputFor(quads), v, RenderStage.White, runProd: false);
            (double rms, double max, double[] series) = Residual(res.Meter, quads);
            JitterStats st = JitterMetrics.Compute(res.Meter, quads, 24);
            (double period, double amp) = Dominant(series);
            results[Key(ratio, sweep.Kind, v.Id)] = (rms, max, st.SlipRms, period, amp, res.MeterFailed, res.Meter, quads);
            _output.WriteLine($"{Key(ratio, sweep.Kind, v.Id)}: rms {rms:0.000} max {max:0.000} slip {st.SlipRms:0.000}");
        });

        var sb = new StringBuilder();
        sb.AppendLine("## Perspective linearity microbench (item 1)");
        sb.AppendLine();
        sb.AppendLine("White insert, flat black base, 1344x768 canvas, ONE quad (aspect 1344:768) swept through 41 sub-pixel steps, one rendered frame per step; overlay `format=yuv444`, luma plane piped (no output chroma subsampling). Edge = integrating meter (`EdgeMeter`, floor in `RenderPathTests`: analytic anti-aliased control). Quad size is `ratio x` canvas, so `perspective` minifies the 1344x768 content by 1/ratio: 0.33 -> 3.0:1, 0.52 -> 1.9:1 (the staging plates), 0.97 -> 1.03:1.");
        sb.AppendLine("**Resid RMS / max**: measured minus commanded corner, per coordinate, after removing the best affine function of the step (the constant 1 px border inset and any scale-dependent inset), over 4 corners x 2 coordinates, px. **Slip**: the benchmark's SlipRms of measured-vs-commanded, px. **Period / Amp**: dominant period (in steps; x 0.05 px for x/y sweeps) and amplitude (px) of the corner-mean residual series. Sweeps: `x`, `y` +0..2 px in 0.05 px steps; `rot` 0..0.5 deg in 0.0125 deg steps about the canvas centre; `scale` 1..1.02.");
        sb.AppendLine();
        foreach (Sweep sweep in Sweeps)
        {
            sb.AppendLine($"### sweep `{sweep.Kind}`");
            sb.AppendLine();
            sb.AppendLine("| variant | ratio | Resid RMS | Resid max | Slip RMS | Period (steps) | Amp | edge fits failed |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            foreach (RigVariant v in variants)
            {
                foreach (double ratio in Ratios)
                {
                    var r = results[Key(ratio, sweep.Kind, v.Id)];
                    sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                        $"| {v.Label} | {ratio:0.00} | {r.Rms:0.000} | {r.Max:0.000} | {r.Slip:0.000} | {r.Period:0.0} | {r.Amp:0.000} | {r.Fail} |"));
                }
            }

            sb.AppendLine();
        }

        // Per-step csv + svg of the x sweep.
        foreach (double ratio in Ratios)
        {
            foreach (RigVariant v in variants)
            {
                var r = results[Key(ratio, "x", v.Id)];
                var csv = new StringBuilder("step,commandedTL_x,measuredTL_x,measuredTL_y,commandedTL_y\n");
                for (int i = 0; i < r.Measured.Count; i++)
                    csv.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{i},{r.Commanded[i].X0:0.####},{r.Measured[i].X0:0.####},{r.Measured[i].Y0:0.####},{r.Commanded[i].Y0:0.####}"));
                File.WriteAllText(Path.Combine(dir, $"x-{ratio:0.00}-{v.Id}.csv"), csv.ToString());
            }
        }

        File.WriteAllText(Path.Combine(dir, "residual-x.svg"), Svg(results, variants.Where(v => v.Id is "lin" or "pre" or "prerep").ToList()));
        sb.AppendLine("Per-step data: `linearity/x-<ratio>-<variant>.csv`; residual plot of the `x` sweep: `linearity/residual-x.svg` (TL corner x residual vs commanded offset, one panel per ratio, lines = production linear / cubic / prescaled).");
        BenchSupport.WriteSection("50-linearity", sb.ToString());
    }

    private static string Key(double ratio, string kind, string variant) => string.Create(CultureInfo.InvariantCulture, $"{ratio:0.00}|{kind}|{variant}");

    private static string Svg(System.Collections.Concurrent.ConcurrentDictionary<string, (double Rms, double Max, double Slip, double Period, double Amp, int Fail, List<Quad> Measured, List<Quad> Commanded)> results, List<RigVariant> variants)
    {
        string[] colours = ["#c0392b", "#2980b9", "#27ae60"];
        var sb = new StringBuilder();
        int pw = 360, ph = 220, gap = 20;
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<svg xmlns='http://www.w3.org/2000/svg' width='{3 * pw + 4 * gap}' height='{ph + 70}' font-family='sans-serif' font-size='11'>"));
        sb.AppendLine("<rect width='100%' height='100%' fill='white'/>");
        for (int p = 0; p < Ratios.Length; p++)
        {
            double ox = gap + p * (pw + gap), oy = 30;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<text x='{ox}' y='16'>ratio {Ratios[p]:0.00} (minification {1 / Ratios[p]:0.0}:1): TL corner x residual (px) vs commanded offset (px)</text>"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<rect x='{ox}' y='{oy}' width='{pw}' height='{ph}' fill='none' stroke='#999'/>"));
            double yScale = ph / 2.0 / 0.75; // +-0.75 px
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<line x1='{ox}' y1='{oy + ph / 2.0}' x2='{ox + pw}' y2='{oy + ph / 2.0}' stroke='#ccc'/>"));
            for (int vi = 0; vi < variants.Count; vi++)
            {
                var r = results[Key(Ratios[p], "x", variants[vi].Id)];
                // residual of TL x, mean removed
                var res = new double[r.Measured.Count];
                for (int i = 0; i < res.Length; i++)
                    res[i] = r.Measured[i].X0 - r.Commanded[i].X0;
                double mean = res.Average();
                var pts = res.Select((v, i) => string.Create(CultureInfo.InvariantCulture, $"{ox + i * pw / (double)(res.Length - 1):0.##},{oy + ph / 2.0 - (v - mean) * yScale:0.##}"));
                sb.AppendLine($"<polyline fill='none' stroke='{colours[vi]}' stroke-width='1.4' points='{string.Join(' ', pts)}'/>");
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"<text x='{ox + 4}' y='{oy + ph + 14 + vi * 13}' fill='{colours[vi]}'>{System.Net.WebUtility.HtmlEncode(variants[vi].Label)}</text>"));
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
