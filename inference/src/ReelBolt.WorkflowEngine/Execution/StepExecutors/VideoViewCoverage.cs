using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Keeps the bounded analysis view covering the WHOLE timeline of every source. When the view has
/// to leave items out for its size limit, the victim is an interior item whose neighbours (same
/// kind, same source) are closest together, so the survivors stay evenly spread over time; the
/// first and last item of each kind and source are never chosen while another candidate exists.
/// Dropping from the end of the list, which this replaces, silently cut a long source's tail.
/// Also reports what the final view covers (<c>meta.offeredCoverage</c>) and the share of the
/// size limit kept free for the detected events.
/// </summary>
internal static class VideoViewCoverage
{
    /// <summary>Warning code when the offered transcript stops well before the end of a source.</summary>
    internal const string PartialWarning = "view_coverage_partial";

    private const double PartialBelow = 0.9;

    /// <summary>Drop order, cheapest first: transcript, then silences, then shots (the list's own priority).</summary>
    private static readonly string[] KindOrder = ["segment", "silence", "shot"];

    /// <summary>
    /// The size limit the item view may use. With detected events on, a third of the limit stays free
    /// for them so a long transcript cannot starve the events (they are added after the view).
    /// </summary>
    internal static int ViewBudget(VideoAnalyzeStepConfig config, int maxOutputChars) =>
        config.Outline && config.AnalyzeEvents ? maxOutputChars - (maxOutputChars / 3) : maxOutputChars;

    /// <summary>
    /// Index of the interior item to drop next, or -1 when every item is the first or last of its
    /// kind and source (the caller then falls back to dropping from the end).
    /// </summary>
    internal static int IndexOfCoverageDrop(List<VideoAnalyzeStepExecutor.OfferedItem> offered)
    {
        foreach (string kind in KindOrder)
        {
            int victim = -1;
            double victimGap = double.PositiveInfinity;

            foreach (IGrouping<int, (int Index, double Start)> group in offered
                .Select((item, index) => (item, index))
                .Where(t => t.item.Kind == kind)
                .Select(t => (Index: t.index, Source: SourceOf(t.item), Start: StartOf(t.item)))
                .GroupBy(t => t.Source, t => (t.Index, t.Start)))
            {
                List<(int Index, double Start)> ordered = [.. group.OrderBy(t => t.Start).ThenBy(t => t.Index)];
                for (int i = 1; i < ordered.Count - 1; i++)
                {
                    double gap = ordered[i + 1].Start - ordered[i - 1].Start;
                    if (gap < victimGap || (gap == victimGap && ordered[i].Index < victim))
                    {
                        victimGap = gap;
                        victim = ordered[i].Index;
                    }
                }
            }

            if (victim >= 0)
                return victim;
        }

        return -1;
    }

    /// <summary>Thins <paramref name="offered"/> to at most <paramref name="max"/> items, evenly over time.</summary>
    internal static void ThinToCount(List<VideoAnalyzeStepExecutor.OfferedItem> offered, int max)
    {
        while (offered.Count > max && offered.Count > 0)
        {
            int index = IndexOfCoverageDrop(offered);
            offered.RemoveAt(index >= 0 ? index : offered.Count - 1);
        }
    }

    /// <summary>
    /// <c>meta.offeredCoverage</c>: how much of the timeline the offered items reach, for the source
    /// with the least coverage. Measured on the transcript when the source has one, else on every item.
    /// <c>Partial</c> is true only when something was left out AND the offered end is under 90% of the source.
    /// </summary>
    internal static (JsonObject Node, bool Partial)? Coverage(
        VideoAnalysisArtifact artifact, IReadOnlyList<VideoAnalyzeStepExecutor.OfferedItem> offered)
    {
        IEnumerable<int> sources = artifact.Sources is { Count: > 0 } infos
            ? infos.Select(s => s.SourceIndex)
            : [0];

        (double First, double Last, double Duration, double Ratio, bool Dropped)? worst = null;
        foreach (int src in sources)
        {
            double duration = artifact.Sources?.FirstOrDefault(s => s.SourceIndex == src)?.Media.DurationSec
                ?? artifact.Media.DurationSec;
            if (duration <= 0)
                continue;

            bool hasTranscript = artifact.Segments.Any(s => s.SourceIndex == src);
            List<(double Start, double End)> shown = [.. offered
                .Where(i => SourceOf(i) == src && (!hasTranscript || i.Kind == "segment"))
                .Select(i => (StartOf(i), EndOf(i)))];
            if (shown.Count == 0)
                continue;

            double all = hasTranscript
                ? artifact.Segments.Where(s => s.SourceIndex == src).Max(s => s.EndSec)
                : double.PositiveInfinity;
            double first = shown.Min(t => t.Start);
            double last = shown.Max(t => t.End);
            double ratio = last / duration;
            if (worst is null || ratio < worst.Value.Ratio)
                worst = (first, last, duration, ratio, last < all - 1e-6);
        }

        if (worst is not { } w)
            return null;

        return (new JsonObject
        {
            ["firstSec"] = Math.Round(w.First, 2),
            ["lastSec"] = Math.Round(w.Last, 2),
            ["sourceDurationSec"] = Math.Round(w.Duration, 2)
        }, w.Dropped && w.Ratio < PartialBelow);
    }

    /// <summary>The <see cref="PartialWarning"/> warning when <c>meta.offeredCoverage</c> says the view stops early.</summary>
    internal static List<JsonObject> Warnings(JsonObject meta, VideoAnalyzeStepConfig config)
    {
        if (meta["offeredCoverage"] is not JsonObject node || node["partial"]?.GetValue<bool>() != true)
            return [];

        if (config.Outline)
            return
            [
                new JsonObject
                {
                    ["code"] = PartialWarning,
                    ["message"] = "The analysis was long, so the editors are shown the film only up to " +
                                  $"{node["lastSec"]!.GetValue<double>().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s of " +
                                  $"{node["sourceDurationSec"]!.GetValue<double>().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s in the list. " +
                                  "ReadVideoSection and ReadVideoTranscript return the rest."
                }
            ];

        int budget = Math.Clamp(config.MaxOutputChars, 256, 200_000);
        return
        [
            new JsonObject
            {
                ["code"] = PartialWarning,
                ["message"] = "The analysis was too long for its size limit, so the editors are shown the film only up to " +
                              $"{node["lastSec"]!.GetValue<double>().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s of " +
                              $"{node["sourceDurationSec"]!.GetValue<double>().ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s. " +
                              $"Raise maxOutputChars on this analysis step (now {budget.ToString(System.Globalization.CultureInfo.InvariantCulture)}), or give it less to describe.",
                ["maxOutputChars"] = budget
            }
        ];
    }

    /// <summary>
    /// The events to show when only <paramref name="fits"/> fit: the list is cut into <paramref name="fits"/> runs
    /// of consecutive (time-ordered) events and the strongest of each run is kept, so the survivors are the
    /// strong ones AND spread over the whole timeline.
    /// </summary>
    internal static HashSet<string> SelectSpread(IReadOnlyList<VideoAnalysisRenderEvent> events, int fits)
    {
        HashSet<string> keep = new(StringComparer.Ordinal);
        for (int b = 0; b < fits; b++)
        {
            int from = (int)((long)b * events.Count / fits);
            int to = (int)((long)(b + 1) * events.Count / fits);
            int best = -1;
            for (int i = from; i < to; i++)
                if (best < 0 || (events[i].Strength ?? 0) > (events[best].Strength ?? 0))
                    best = i;
            if (best >= 0)
                keep.Add(events[best].Id);
        }

        return keep;
    }

    private static int SourceOf(VideoAnalyzeStepExecutor.OfferedItem i) =>
        i.Shot?.SourceIndex ?? i.Silence?.SourceIndex ?? i.Segment?.SourceIndex ?? 0;

    private static double StartOf(VideoAnalyzeStepExecutor.OfferedItem i) =>
        i.Shot?.StartSec ?? i.Silence?.StartSec ?? i.Segment?.StartSec ?? 0;

    private static double EndOf(VideoAnalyzeStepExecutor.OfferedItem i) =>
        i.Shot?.EndSec ?? i.Silence?.EndSec ?? i.Segment?.EndSec ?? 0;
}
