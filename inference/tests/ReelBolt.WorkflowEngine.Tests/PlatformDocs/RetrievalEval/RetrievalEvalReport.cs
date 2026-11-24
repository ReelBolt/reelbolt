using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs.RetrievalEval;

public sealed record ConfigResult(
    string Name,
    string Description,
    IReadOnlyDictionary<string, SliceMetrics> Slices,
    IReadOnlyList<QueryOutcome> Outcomes);

public sealed record Gate(string Name, bool? Passed, string Detail);

/// <summary>Gate verdicts of docs/design/workflow-apps.md 7.5, computed from the three configuration results.</summary>
public static class RetrievalEvalGates
{
    public const string Baseline = "baseline";
    public const string HybridA = "hybrid-a";
    public const string HybridAb = "hybrid-a+b";

    public static IReadOnlyList<Gate> Evaluate(IReadOnlyList<ConfigResult> results)
    {
        ConfigResult? Find(string name) => results.FirstOrDefault(r => r.Name == name);
        ConfigResult? b = Find(Baseline), a = Find(HybridA), ab = Find(HybridAb);
        List<Gate> gates = [];
        if (b is not null && a is not null)
        {
            double delta = a.Slices["all"].RecallAt5 - b.Slices["all"].RecallAt5;
            gates.Add(new("hybrid: recall@5 up at least 10 points (whole set)", delta >= 0.10 - 1e-9, $"{Pp(delta)} points"));
            SliceMetrics nb = b.Slices["non-english"], na = a.Slices["non-english"];
            double[] deltas = [na.RecallAt5 - nb.RecallAt5, na.MrrAt10 - nb.MrrAt10, na.NdcgAt10 - nb.NdcgAt10, na.InjectionHitRate - nb.InjectionHitRate];
            gates.Add(new("hybrid: non-English not worse on recall@5, MRR@10, nDCG@10 and injection hit (all four)",
                deltas.All(x => x >= -1e-9),
                $"recall@5 {Pp(deltas[0])}, MRR@10 {Pp(deltas[1])}, nDCG@10 {Pp(deltas[2])}, injection hit {Pp(deltas[3])} points"));
            double p95b = b.Slices["all"].LatencyP95Ms, p95a = a.Slices["all"].LatencyP95Ms;
            gates.Add(new("hybrid: injection p95 at most 2x baseline", p95a <= 2 * p95b, $"{F(p95a)} ms vs {F(p95b)} ms baseline"));
        }

        if (a is not null && ab is not null)
        {
            double all = ab.Slices["all"].NdcgAt10 - a.Slices["all"].NdcgAt10;
            gates.Add(new("stage B: nDCG@10 not below stage A (whole set)", all >= -1e-9, $"{all:+0.000;-0.000;0.000}"));
            double ne = ab.Slices["non-english"].NdcgAt10 - a.Slices["non-english"].NdcgAt10;
            gates.Add(new("stage B: nDCG@10 not below stage A (non-English)", ne >= -1e-9, $"{ne:+0.000;-0.000;0.000}"));
        }

        return gates;
    }

    private static string Pp(double d) => (d * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
    private static string F(double d) => d.ToString("0", CultureInfo.InvariantCulture);
}

public static class RetrievalEvalReport
{
    private static readonly string[] SliceOrder = ["all", "english", "non-english", "step-question", "goal-question"];

    public static string ToJson(string machine, string date, IReadOnlyDictionary<string, string> setup, int active, int pending,
        IReadOnlyList<ConfigResult> results, IReadOnlyList<Gate> gates) =>
        JsonSerializer.Serialize(new { date, machine, setup, activeQueries = active, pendingKbQueries = pending, configurations = results, gates },
            new JsonSerializerOptions { WriteIndented = true });

    public static string ToMarkdown(string machine, string date, IReadOnlyDictionary<string, string> setup, int active, int pending,
        IReadOnlyList<ConfigResult> results, IReadOnlyList<Gate> gates)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Retrieval eval report").AppendLine();
        sb.AppendLine($"Date: {date}. Machine: {machine}.").AppendLine();
        sb.AppendLine($"Queries scored: {active}; pending-kb (excluded until their knowledge-base documents exist): {pending}.").AppendLine();
        sb.AppendLine("Setup:").AppendLine();
        foreach ((string k, string v) in setup) sb.AppendLine($"- {k}: {v}");
        sb.AppendLine();
        foreach (string slice in SliceOrder)
        {
            sb.AppendLine($"## Slice: {slice}").AppendLine();
            sb.AppendLine("| Configuration | n | recall@5 | MRR@10 | nDCG@10 | injection hit | injection any | p50 ms | p95 ms |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (ConfigResult r in results)
            {
                SliceMetrics m = r.Slices[slice];
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {r.Name} | {m.Queries} | {m.RecallAt5:0.000} | {m.MrrAt10:0.000} | {m.NdcgAt10:0.000} | {m.InjectionHitRate:0.000} | {m.InjectionAnyRate:0.000} | {m.LatencyP50Ms:0} | {m.LatencyP95Ms:0} |"));
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Gates").AppendLine();
        foreach (Gate g in gates)
            sb.AppendLine($"- {(g.Passed is null ? "NOT MEASURED" : g.Passed.Value ? "PASS" : "FAIL")}: {g.Name} ({g.Detail})");
        sb.AppendLine();
        sb.AppendLine("Definitions: recall@5 = share of queries with a gold section in the top 5 sections; injection hit = share with a gold");
        sb.AppendLine("section among the injected sections (floor 0.35, top 4); injection any = share with at least one section injected;");
        sb.AppendLine("latency = wall clock of the first search per query (limit 10, so the query embedding is a real call, never cached) including, for A+B, the rerank call.");
        return sb.ToString();
    }
}
