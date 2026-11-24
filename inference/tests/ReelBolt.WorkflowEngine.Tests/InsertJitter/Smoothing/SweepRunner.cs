#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>Scores candidates on the cached scenes and formats the tables.</summary>
internal static class SweepRunner
{
    public static readonly string[] Scenes =
    [
        "static-1344", "static-1080p", "static-small", "static-flicker", "microshake", "slow-push", "hold-cadence", "turn", "occlusion", "whip"
    ];

    public sealed record SceneResult(JitterStats Stats, double Ms, long AllocBytes);

    public sealed record CandidateResult(Candidate Candidate, string Name, Dictionary<string, SceneResult> ByScene);

    public static Dictionary<string, SceneData> LoadAll(string cacheDir, RejectPrototype.Mode? reject = null) =>
        Scenes.ToDictionary(s => s, s =>
        {
            SceneData d = SceneData.Load(cacheDir, s);
            return reject is null ? d : d.WithReject(reject.Value, out _);
        });

    public static CandidateResult Evaluate(Candidate c, IReadOnlyDictionary<string, SceneData> data, bool timing = true)
    {
        var by = new Dictionary<string, SceneResult>();
        string name = c.Id;
        foreach (string scene in Scenes)
        {
            SceneData d = data[scene];
            ITrackSmoother sm = c.Make(d);
            name = sm.Name;
            IReadOnlyList<Quad> input = d.Quads(d.S3Frames());
            IReadOnlyList<Quad> truth = d.TruthQuads();
            IReadOnlyList<Quad> output = sm.Smooth(input, d.Fps, d.NoiseSrc);
            JitterStats st = JitterMetrics.Compute(output, truth, d.Fps);
            double ms = 0;
            long alloc = 0;
            if (timing)
            {
                var times = new List<double>();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                sm.Smooth(input, d.Fps, d.NoiseSrc);
                alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
                for (int r = 0; r < 5; r++)
                {
                    var sw = Stopwatch.StartNew();
                    sm.Smooth(input, d.Fps, d.NoiseSrc);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                ms = TrackSeries.Median(times);
                // Normalize to ms per 10 s of video.
                ms *= 10.0 / (d.S3.Count / d.Fps);
            }

            by[scene] = new SceneResult(st, ms, alloc);
        }

        return new CandidateResult(c, name, by);
    }

    private static string F(double v, string f = "0.000") => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);

    private static string F(double? v, string f = "0.000") => v is null || double.IsNaN(v.Value) ? "-" : v.Value.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>The full scene matrix of one candidate.</summary>
    public static string Matrix(CandidateResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#### {r.Candidate.Id}: {r.Name}");
        sb.AppendLine();
        sb.AppendLine("| Scene | SlipRms | HfRms | ContentSlip | ErrMean | ErrMax | Overshoot | LagLS | ms/10s | alloc KB |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (string s in Scenes)
        {
            SceneResult x = r.ByScene[s];
            JitterStats j = x.Stats;
            sb.AppendLine($"| {s} | {F(j.SlipRms)} | {F(j.HfRms)} | {F(j.ContentSlipRms)} | {F(j.ErrMean)} | {F(j.ErrMax)} | {F(j.Overshoot, "0.00")} | {F(j.LagLsFrames, "0.00")} | {F(x.Ms, "0.00")} | {F(x.AllocBytes / 1024.0, "0")} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static double Static(CandidateResult r, Func<JitterStats, double> sel) =>
        new[] { "static-1344", "static-1080p", "static-small" }.Max(s => sel(r.ByScene[s].Stats));

    /// <summary>
    /// Count of (scene, metric) cells where the candidate is worse than the baseline by more than 15 % AND more than the absolute slack
    /// (metrics: SlipRms, ErrMean, ErrMax; slack 0.05 px for slip/err-mean, 0.25 px for ErrMax). Zero = no regression anywhere.
    /// </summary>
    public static int Regressions(CandidateResult r, CandidateResult baseline, out string detail)
    {
        int n = 0;
        var parts = new List<string>();
        foreach (string s in Scenes)
        {
            JitterStats a = r.ByScene[s].Stats, b = baseline.ByScene[s].Stats;
            void Check(string label, double x, double y, double slack)
            {
                if (!double.IsNaN(x) && !double.IsNaN(y) && x > y * 1.15 + slack)
                {
                    n++;
                    parts.Add($"{s}:{label} {y:0.00}->{x:0.00}");
                }
            }

            Check("slip", a.SlipRms, b.SlipRms, 0.05);
            Check("errMean", a.ErrMean, b.ErrMean, 0.05);
            Check("errMax", a.ErrMax, b.ErrMax, 0.25);
        }

        detail = string.Join("; ", parts);
        return n;
    }

    public static string SummaryHeader() =>
        "| Id | slow-push slip | slow-push errMean | micro slip | hold slip | flicker slip | static slip (max of 3) | turn slip | turn errMax | turn lag | whip slip | whip errMax | whip lag | occl errMax | regress cells | ms/10s (slow-push) |\n" +
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|";

    public static string SummaryRow(CandidateResult r, CandidateResult baseline)
    {
        JitterStats Sx(string s) => r.ByScene[s].Stats;
        int reg = Regressions(r, baseline, out _);
        return $"| {r.Candidate.Id} | {F(Sx("slow-push").SlipRms)} | {F(Sx("slow-push").ErrMean)} | {F(Sx("microshake").SlipRms)} | {F(Sx("hold-cadence").SlipRms)} | {F(Sx("static-flicker").SlipRms)} | " +
               $"{F(Static(r, j => j.SlipRms))} | {F(Sx("turn").SlipRms)} | {F(Sx("turn").ErrMax, "0.00")} | {F(Sx("turn").LagLsFrames, "0.00")} | {F(Sx("whip").SlipRms)} | {F(Sx("whip").ErrMax, "0.00")} | {F(Sx("whip").LagLsFrames, "0.00")} | {F(Sx("occlusion").ErrMax, "0.00")} | {reg} | {F(r.ByScene["slow-push"].Ms, "0.00")} |";
    }

    /// <summary>The nine objectives (all lower-is-better) the Pareto filter compares.</summary>
    public static double[] Objectives(CandidateResult r)
    {
        JitterStats S(string s) => r.ByScene[s].Stats;
        return
        [
            S("slow-push").SlipRms, S("microshake").SlipRms, S("hold-cadence").SlipRms,
            Static(r, j => j.SlipRms) + S("static-flicker").SlipRms,
            S("turn").ErrMax, S("turn").SlipRms,
            S("whip").ErrMax, S("whip").SlipRms,
            S("occlusion").ErrMax
        ];
    }

    public static readonly string[] ObjectiveNames =
        ["slow-push slip", "microshake slip", "hold-cadence slip", "static+flicker slip", "turn errMax", "turn slip", "whip errMax", "whip slip", "occlusion errMax"];

    /// <summary>Candidates no other candidate beats on every objective (a 2 % + 0.02 slack makes ties not dominate).</summary>
    public static List<CandidateResult> ParetoFront(IReadOnlyList<CandidateResult> all)
    {
        var obj = all.Select(Objectives).ToList();
        var front = new List<CandidateResult>();
        for (int i = 0; i < all.Count; i++)
        {
            bool dominated = false;
            for (int j = 0; j < all.Count && !dominated; j++)
            {
                if (i == j)
                    continue;
                bool allBetterOrEqual = true, oneBetter = false;
                for (int k = 0; k < obj[i].Length; k++)
                {
                    if (obj[j][k] > obj[i][k])
                    {
                        allBetterOrEqual = false;
                        break;
                    }

                    if (obj[j][k] < obj[i][k] * 0.98 - 0.02)
                        oneBetter = true;
                }

                dominated = allBetterOrEqual && oneBetter;
            }

            if (!dominated)
                front.Add(all[i]);
        }

        return front;
    }
}
