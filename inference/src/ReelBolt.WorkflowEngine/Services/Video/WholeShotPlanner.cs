using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>What <see cref="WholeShotPlanner.Plan"/> chose: indices into the planned spans, ascending.</summary>
/// <param name="Kept">Spans kept whole, in the editor's order.</param>
/// <param name="Dropped">Spans left out.</param>
/// <param name="AchievedSec">Total of the kept spans.</param>
/// <param name="TargetSec">The asked-for length.</param>
/// <param name="TotalSec">Total of every span before the choice.</param>
/// <param name="Reason">Null, or <c>no_span_fits</c> when not even one span fits under the ceiling (Kept and Dropped are then empty: the caller keeps the cut as it was).</param>
public sealed record WholeShotPlan(
    IReadOnlyList<int> Kept,
    IReadOnlyList<int> Dropped,
    double AchievedSec,
    double TargetSec,
    double TotalSec,
    string? Reason)
{
    /// <summary>The compile's <c>length.fitted</c> node: mode, target, achieved and the chosen and dropped spans.</summary>
    public JsonObject ToReport(IReadOnlyList<(double Start, double End, int SourceIndex)> spans)
    {
        JsonObject Describe(int index) => new()
        {
            ["index"] = index,
            ["sourceIndex"] = spans[index].SourceIndex,
            ["startSec"] = Math.Round(spans[index].Start, 3),
            ["endSec"] = Math.Round(spans[index].End, 3),
            ["durationSec"] = Math.Round(spans[index].End - spans[index].Start, 3)
        };

        JsonArray kept = new();
        foreach (int i in Kept)
            kept.Add(Describe(i));
        JsonArray dropped = new();
        foreach (int i in Dropped)
            dropped.Add(Describe(i));

        JsonObject report = new()
        {
            ["mode"] = "WholeShots",
            ["editedSec"] = Math.Round(TotalSec, 3),
            ["targetSec"] = Math.Round(TargetSec, 3),
            ["achievedSec"] = Math.Round(AchievedSec, 3),
            ["trimmedSec"] = Math.Round(TotalSec - AchievedSec, 3),
            ["keptSpans"] = kept,
            ["droppedSpans"] = dropped
        };
        if (Reason is not null)
            report["reason"] = Reason;
        return report;
    }
}

/// <summary>
/// <c>VideoCompileStepConfig.TargetDurationMode = WholeShots</c> (docs/video-editing.md "Picking whole shots
/// for a target length"): reaches <c>TargetDurationSec</c> by dropping whole spans instead of shortening their
/// tails. The kept spans' total is as close to the target as the spans allow without exceeding it by more than
/// <see cref="OvershootRatio"/>; a cut is never lengthened and no span is ever cut inside.
/// </summary>
/// <remarks>
/// <para>
/// Order of choice: the first and last span (the opening and closing beat the editor placed on purpose) when
/// each fits under the ceiling; then, of the spans left, the subset whose total is nearest the target —
/// <b>not</b> the longest-first fill the planner used to take, which left a reachable total on the table
/// whenever the closer one needed two shorter spans instead of one long one. Rank decides between equally
/// close totals (higher first; with no ranks, longest first, ties to the earlier span). Pure and
/// deterministic: the same spans, ranks and target always choose the same subset.
/// </para>
/// <para>
/// The choice is an exact subset-sum over the spans left after the anchors, quantised to a unit of at most
/// 1 ms and at most <see cref="MaxSearchUnits"/> units wide, so a long film's span list cannot grow the search
/// without bound. Durations are rounded <i>up</i> to a unit, so a total the search accepts can never exceed the
/// ceiling.
/// </para>
/// </remarks>
public static class WholeShotPlanner
{
    /// <summary>The kept total may exceed the target by at most this fraction.</summary>
    public const double OvershootRatio = 0.10;

    /// <summary>
    /// Widest the reachable-total search may get: a long film's span list is split into at most this many
    /// units, so the bitsets stay bounded (200,000 units is 12.5 KB per span, and a film's ceiling is
    /// always covered because the unit grows with it).
    /// </summary>
    internal const int MaxSearchUnits = 200_000;

    /// <summary>The search's memory budget: a wider span list gets a coarser unit rather than more bits.</summary>
    private const long SearchWordBudget = 134_217_728;

    private const double Epsilon = 1e-9;

    public static WholeShotPlan Plan(IReadOnlyList<double> durations, IReadOnlyList<double>? ranks, double targetSec)
    {
        ArgumentNullException.ThrowIfNull(durations);
        int n = durations.Count;
        double total = durations.Sum();
        List<int> all = Enumerable.Range(0, n).ToList();

        double ceiling = targetSec * (1 + OvershootRatio);
        if (targetSec <= 0 || n == 0 || total <= ceiling + Epsilon)
            return new WholeShotPlan(all, [], total, targetSec, total, null);

        double RankOf(int i) => ranks is not null && ranks.Count == n ? ranks[i] : 0;

        HashSet<int> kept = new();
        HashSet<int> anchors = new();
        double anchorSec = 0;
        foreach (int anchor in n == 1 ? [0] : new[] { 0, n - 1 })
        {
            if (anchorSec + durations[anchor] <= ceiling + Epsilon)
            {
                kept.Add(anchor);
                anchors.Add(anchor);
                anchorSec += durations[anchor];
            }
        }

        foreach (int i in ClosestToTarget(
                     [.. all.Where(i => !anchors.Contains(i))
                         .OrderByDescending(RankOf).ThenByDescending(i => durations[i]).ThenBy(i => i)],
                     durations, anchorSec, targetSec, ceiling))
        {
            kept.Add(i);
        }

        List<int> keptSorted = kept.OrderBy(i => i).ToList();
        List<int> dropped = all.Where(i => !kept.Contains(i)).ToList();
        if (keptSorted.Count == 0)
            return new WholeShotPlan([], [], 0, targetSec, total, "no_span_fits");

        return new WholeShotPlan(keptSorted, dropped, kept.Sum(i => durations[i]), targetSec, total, null);
    }

    /// <summary>
    /// The spans of <paramref name="open"/> (already in preference order) to add to the anchors: the subset
    /// whose total, added to <paramref name="anchorSec"/>, is the nearest reachable one to
    /// <paramref name="targetSec"/> and still under <paramref name="ceilingSec"/>.
    /// </summary>
    /// <remarks>
    /// A reachability bitset per span prefix: one bit per candidate total, one row per span, and the choice
    /// read back from the rows. That is what makes the answer exact instead of the greedy fill's, and it is
    /// why a 2-for-1 replacement (the 0.5, 7, 4, 4, 0.5 cut whose 9.0 s set is nearer a 10 s target than the
    /// 8.0 s one the longest-first fill stops at) is found. Durations are rounded UP to the unit, so a total
    /// the search accepts is never over the ceiling in real time.
    /// </remarks>
    private static List<int> ClosestToTarget(
        List<int> open, IReadOnlyList<double> durations, double anchorSec, double targetSec, double ceilingSec)
    {
        double capacitySec = ceilingSec - anchorSec;
        if (open.Count == 0 || capacitySec <= Epsilon)
            return [];

        long capacityMicros = (long)Math.Floor(capacitySec * 1_000_000);
        long maxUnits = Math.Clamp(SearchWordBudget / Math.Max(1, open.Count), 20_000, MaxSearchUnits);
        long unitMicros = Math.Max(1, (capacityMicros + maxUnits - 1) / maxUnits);
        long capacityUnits = capacityMicros / unitMicros;
        if (capacityUnits <= 0)
            return [];

        long[] units = new long[open.Count];
        for (int k = 0; k < units.Length; k++)
            units[k] = durations[open[k]] <= 0 ? 0 : (long)Math.Ceiling(durations[open[k]] * 1_000_000 / unitMicros);

        // +1 word of slack: a shift that would carry past the ceiling may land in it instead of being lost.
        int words = (int)(capacityUnits / 64) + 1;
        ulong[][] reachable = new ulong[open.Count + 1][];
        reachable[0] = new ulong[words];
        reachable[0][0] = 1UL;
        for (int k = 1; k <= open.Count; k++)
        {
            ulong[] previous = reachable[k - 1];
            ulong[] current = [.. previous];
            long step = units[k - 1];
            if (step > 0 && step <= capacityUnits)
            {
                int wholeWords = (int)(step / 64);
                int bits = (int)(step % 64);
                for (int w = words - 1; w >= wholeWords; w--)
                {
                    ulong shifted = previous[w - wholeWords] << bits;
                    if (bits > 0 && w - wholeWords - 1 >= 0)
                        shifted |= previous[w - wholeWords - 1] >> (64 - bits);
                    current[w] |= shifted;
                }
            }

            reachable[k] = current;
        }

        long targetUnits = (long)Math.Round((targetSec - anchorSec) * 1_000_000 / unitMicros, MidpointRounding.AwayFromZero);
        long bestUnits = 0;
        long bestGap = long.MaxValue;
        for (long sum = 0; sum <= capacityUnits; sum++)
        {
            if ((reachable[open.Count][sum >> 6] & (1UL << (int)(sum & 63))) == 0)
                continue;
            long gap = Math.Abs(sum - targetUnits);
            // Equally near: the fuller set wins, so a tie never shortens the cut.
            if (gap < bestGap || (gap == bestGap && sum > bestUnits))
            {
                bestGap = gap;
                bestUnits = sum;
            }
        }

        List<int> chosen = [];
        long remaining = bestUnits;
        for (int k = open.Count; k >= 1; k--)
        {
            if (units[k - 1] == 0)
            {
                chosen.Add(open[k - 1]);
                continue;
            }

            // Walked from the least preferred span up, so a span is dropped whenever a
            // better-preferred set reaches the same total: rank wins every tie the search leaves.
            if ((reachable[k - 1][remaining >> 6] & (1UL << (int)(remaining & 63))) != 0)
                continue;
            chosen.Add(open[k - 1]);
            remaining -= units[k - 1];
        }

        return remaining == 0 ? chosen : [];
    }
}
