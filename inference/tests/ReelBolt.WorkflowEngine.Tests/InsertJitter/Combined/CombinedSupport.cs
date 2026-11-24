#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Combined;

/// <summary>Paths, scene list, tables and report assembly for the combined (T-006) measurements.</summary>
internal static class CombinedSupport
{
    public static string OutDir => Environment.GetEnvironmentVariable("REELBOLT_COMBINED_DIR") is { Length: > 0 } d ? d : "/tmp/insert-tracking/combined";

    public static string CsvDir => Path.Combine(OutDir, "csv");

    public static string SectionsDir => Path.Combine(OutDir, "sections");

    /// <summary>The ten catalogue scenes plus the two extra EdgeSignal scenes (slow-push on Film A's plate colour, and iso-luma). The clips are T-003's, read-only.</summary>
    public static IReadOnlyList<JitterScene> Scenes => EdgeSupport.Scenes;

    public static IEnumerable<JitterScene> Selected()
    {
        string[] pick = (Environment.GetEnvironmentVariable("REELBOLT_COMBINED_SCENES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (JitterScene s in Scenes)
        {
            if (pick.Length == 0 || pick.Contains(s.Name))
                yield return s;
        }
    }

    public static string CsvPath(string scene, string config) => Path.Combine(CsvDir, $"{scene}-{config}.csv");

    public static string F(double v, string f = "0.000") => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);

    public static string F(double? v, string f = "0.000") => v is null ? "-" : F(v.Value, f);

    public sealed record Metric(string Name, Func<JitterStats, double?> Get, string Format = "0.000");

    public static readonly Metric[] Metrics =
    [
        new("SlipRms", s => s.SlipRms),
        new("SlipP95", s => s.SlipP95),
        new("HfRms", s => s.HfRms),
        new("ContentSlip", s => s.ContentSlipRms),
        new("ErrMean", s => s.ErrMean),
        new("ErrMax", s => s.ErrMax, "0.000"),
        new("Overshoot", s => s.Overshoot, "0.00"),
        new("LagLS", s => s.LagLsFrames, "0.00")
    ];

    /// <summary>Configs x scenes of one metric.</summary>
    public static string MatrixTable(IReadOnlyList<CombinedResult> results, IReadOnlyList<string> configIds, Metric m)
    {
        string[] scenes = Scenes.Select(s => s.Name).Where(n => results.Any(r => r.Scene == n)).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine($"| {m.Name} | " + string.Join(" | ", scenes) + " |");
        sb.AppendLine("|---|" + string.Join("|", scenes.Select(_ => "---:")) + "|");
        foreach (string id in configIds)
        {
            sb.Append($"| {id} |");
            foreach (string s in scenes)
            {
                CombinedResult? r = results.FirstOrDefault(x => x.Scene == s && x.Config.Id == id);
                sb.Append(' ').Append(r is null ? "." : F(m.Get(r.Stats), m.Format)).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Scene rows x the ticket's metrics for one config.</summary>
    public static string FullTable(IReadOnlyList<CombinedResult> results, string configId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Scene | N | " + string.Join(" | ", Metrics.Select(m => m.Name)) + " |");
        sb.AppendLine("|---|---:|" + string.Concat(Enumerable.Repeat("---:|", Metrics.Length)));
        foreach (JitterScene sc in Scenes)
        {
            CombinedResult? r = results.FirstOrDefault(x => x.Scene == sc.Name && x.Config.Id == configId);
            if (r is null)
                continue;
            sb.AppendLine($"| {sc.Name} | {r.Stats.N} | " + string.Join(" | ", Metrics.Select(m => F(m.Get(r.Stats), m.Format))) + " |");
        }

        return sb.ToString();
    }

    public static void WriteSection(string name, string markdown)
    {
        Directory.CreateDirectory(SectionsDir);
        File.WriteAllText(Path.Combine(SectionsDir, name + ".md"), markdown);
        var sb = new StringBuilder("# T-006 combined measurement: the recommended fix stack, end to end\n\n");
        foreach (string f in Directory.GetFiles(SectionsDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
            sb.AppendLine(File.ReadAllText(f)).AppendLine();
        File.WriteAllText(Path.Combine(OutDir, "report.md"), sb.ToString());
    }

    public static double Median(IEnumerable<double> v)
    {
        double[] a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    public static string LoadAvg()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Split(' ')[0] + " (1 min)";
        }
        catch (IOException)
        {
            return "n/a";
        }
    }
}
