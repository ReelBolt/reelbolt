using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.PlatformDocs.RetrievalEval;

/// <summary>
/// The metrics of docs/design/workflow-apps.md 7.5, over SECTIONS (<c>docPath#section</c>), not chunks: a ranked list is
/// collapsed to the first occurrence of each section, so a section split into two chunks counts once. Gold is any-of for
/// recall and MRR (the first gold section found decides) and graded binary for nDCG (every gold section is relevant).
/// </summary>
public static class RetrievalEvalMetrics
{
    public static IReadOnlyList<string> Collapse(IEnumerable<string> sections)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        return [.. sections.Where(seen.Add)];
    }

    public static double RecallAt(IReadOnlyList<string> ranked, IReadOnlyCollection<string> gold, int k) =>
        ranked.Take(k).Any(gold.Contains) ? 1d : 0d;

    public static double ReciprocalRank(IReadOnlyList<string> ranked, IReadOnlyCollection<string> gold, int k)
    {
        for (int i = 0; i < Math.Min(k, ranked.Count); i++)
            if (gold.Contains(ranked[i])) return 1d / (i + 1);
        return 0d;
    }

    public static double Ndcg(IReadOnlyList<string> ranked, IReadOnlyCollection<string> gold, int k)
    {
        double dcg = 0;
        for (int i = 0; i < Math.Min(k, ranked.Count); i++)
            if (gold.Contains(ranked[i])) dcg += 1d / Math.Log2(i + 2);
        double ideal = 0;
        for (int i = 0; i < Math.Min(k, gold.Count); i++) ideal += 1d / Math.Log2(i + 2);
        return ideal == 0 ? 0 : dcg / ideal;
    }

    /// <summary>Nearest-rank percentile (p in 0..100) of the values.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        double[] sorted = [.. values.OrderBy(v => v)];
        int rank = (int)Math.Ceiling(p / 100d * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public static SliceMetrics Summarize(IReadOnlyList<QueryOutcome> outcomes)
    {
        if (outcomes.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0);
        double Mean(Func<QueryOutcome, double> f) => outcomes.Average(f);
        List<double> latency = [.. outcomes.Select(o => o.RankingMs)];
        return new(
            outcomes.Count,
            Mean(o => RecallAt(o.Ranked, o.Gold, 5)),
            Mean(o => ReciprocalRank(o.Ranked, o.Gold, 10)),
            Mean(o => Ndcg(o.Ranked, o.Gold, 10)),
            Mean(o => o.Injected.Any(o.Gold.Contains) ? 1d : 0d),
            Mean(o => o.Injected.Count > 0 ? 1d : 0d),
            Percentile(latency, 50),
            Percentile(latency, 95));
    }

    public static IReadOnlyDictionary<string, SliceMetrics> BySlice(IReadOnlyList<QueryOutcome> outcomes) =>
        new Dictionary<string, SliceMetrics>
        {
            ["all"] = Summarize(outcomes),
            ["english"] = Summarize([.. outcomes.Where(o => o.Locale == "en")]),
            ["non-english"] = Summarize([.. outcomes.Where(o => o.Locale != "en")]),
            ["step-question"] = Summarize([.. outcomes.Where(o => o.Type == "step")]),
            ["goal-question"] = Summarize([.. outcomes.Where(o => o.Type == "goal")])
        };
}
