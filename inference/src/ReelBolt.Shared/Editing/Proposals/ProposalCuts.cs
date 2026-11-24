using System.Globalization;

namespace ReelBolt.Shared.Editing.Proposals;

/// <summary>One cut inside a clip, in SOURCE seconds of the item's asset (same basis as <c>sourceIn / fps</c>); the client turns it into a ripple cut.</summary>
public sealed record EditProposalCut(Guid ItemId, double StartSec, double EndSec);

/// <summary>What a proposal did, by class: spans cut (and their seconds), whole clips removed, trims, mutes and anything else.</summary>
public sealed record EditProposalDid(int Cut, double CutSeconds, int Remove, int Trim, int Mute, int Other, IReadOnlyList<EditProposalDidCut>? Cuts = null);

/// <summary>Cuts by kind (speech | quiet | fillers | lines | keep): how many spans and how many seconds.</summary>
public sealed record EditProposalDidCut(string Kind, int Spans, double Seconds);

/// <summary>Compiler-only reference to one media clip in range: its alias, item, file, visible source window and facts. Never rendered into the prompt.</summary>
public sealed record ProposalClipRef(
    string Alias, Guid ItemId, Guid FileId, bool Video, double SrcStart, double SrcEnd, SourceFacts? Facts, string? TwinOfAlias, string? InputProfile = null,
    bool Cuttable = true, long RecordIn = 0, long RecordOut = 0, bool StepResult = false);

/// <summary>Compiler-only: an offered line alias with the clip it was offered under and its source span.</summary>
public sealed record ProposalLineRef(string Alias, string ClipAlias, double StartSec, double EndSec);

/// <summary>What stored analysis one media source in range has (a project file, or a workflow render output when <c>StepResult</c>; <c>FileId</c> is then the step result id).</summary>
public sealed record ProposalFileCoverage(Guid FileId, bool HasLines, bool HasWords, bool HasGaps, bool StepResult = false);

/// <summary>Which analysis a content target still needs; <c>NoneCovered</c> when no file in range satisfies it.</summary>
public sealed record AnalysisGap(IReadOnlyList<string> Kinds, IReadOnlyList<Guid> MissingFileIds, bool NoneCovered, IReadOnlySet<Guid>? StepResultIds = null)
{
    /// <summary>The media ref of a missing source: <c>step_result:{guid}</c> for a render output, else <c>file:{guid}</c>.</summary>
    public string RefOf(Guid id) => StepResultIds is not null && StepResultIds.Contains(id) ? $"step_result:{id:D}" : $"file:{id:D}";
}

/// <summary>The deterministic span arithmetic behind the content-cut kinds: stored facts in, source-second spans out. The model supplies no number.</summary>
public static class ProposalCuts
{
    /// <summary>Outward pad around a spoken run so the cut does not clip a word's attack or tail.</summary>
    public const double PadSec = 0.05;

    /// <summary>Words closer than this belong to one spoken passage.</summary>
    public const double RunGapSec = 0.5;

    /// <summary>Spans closer than this are one cut: a keep shorter than a quarter second between two cuts is not worth a separate cut.</summary>
    public const double MinKeepSec = 0.25;

    /// <summary>The most cut spans one group carries, and the most one proposal carries.</summary>
    public const int MaxSpansPerGroup = 200;

    public const int MaxSpansPerProposal = 300;

    /// <summary>A span shorter than this is dropped.</summary>
    public const double MinCutSec = 0.05;

    /// <summary>Without a silence analysis, a gap between words at least this long counts as quiet.</summary>
    public const double QuietWordGapSec = 1.0;

    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "um", "umm", "uh", "uhm", "er", "erm", "ah", "hmm", "hm", "mm", "mmm",
        "ehm", "ehh", "eh", "mh", "mhm", "boh", "cioè", "cioe"
    };

    public static IReadOnlyList<(double Start, double End)> Speech(ProposalClipRef clip)
    {
        SourceFacts f = clip.Facts ?? SourceFacts.Empty;
        List<(double, double)> raw = f.Words.Count > 0
            ? f.Words.Select(w => (w.StartSec, w.EndSec)).ToList()
            : f.Lines.Select(l => (l.StartSec, l.EndSec)).ToList();
        List<(double, double)> runs = [];
        foreach ((double s, double e) in raw.OrderBy(x => x.Item1))
        {
            if (e <= clip.SrcStart || s >= clip.SrcEnd) continue;
            if (runs.Count > 0 && s - runs[^1].Item2 <= RunGapSec) runs[^1] = (runs[^1].Item1, Math.Max(runs[^1].Item2, e));
            else runs.Add((s, e));
        }

        return Finish(runs.Select(r => (r.Item1 - PadSec, r.Item2 + PadSec)), clip);
    }

    public static IReadOnlyList<(double Start, double End)> Quiet(ProposalClipRef clip)
    {
        SourceFacts f = clip.Facts ?? SourceFacts.Empty;
        if (f.Gaps.Count > 0) return Finish(f.Gaps.Select(g => (g.StartSec, g.EndSec)), clip);

        List<(double S, double E)> spoken = f.Words.Count > 0
            ? f.Words.Select(w => (w.StartSec, w.EndSec)).OrderBy(x => x.StartSec).ToList()
            : f.Lines.Select(l => (l.StartSec, l.EndSec)).OrderBy(x => x.StartSec).ToList();
        List<(double, double)> gaps = [];
        for (int i = 1; i < spoken.Count; i++)
        {
            double from = spoken[i - 1].E, to = spoken[i].S;
            if (to - from >= QuietWordGapSec) gaps.Add((from, to));
        }

        return Finish(gaps, clip);
    }

    public static IReadOnlyList<(double Start, double End)> Fillers(ProposalClipRef clip)
    {
        SourceFacts f = clip.Facts ?? SourceFacts.Empty;
        return Finish(f.Words.Where(w => FillerWords.Contains(Normalize(w.Text))).Select(w => (w.StartSec, w.EndSec)), clip);
    }

    public static IReadOnlyList<(double Start, double End)> Lines(ProposalClipRef clip, IEnumerable<ProposalLineRef> lines) =>
        Finish(lines.Select(l => (l.StartSec, l.EndSec)), clip);

    /// <summary>Everything in the clip's visible window except the kept lines (each kept span padded so no word is clipped).</summary>
    public static IReadOnlyList<(double Start, double End)> AllButLines(ProposalClipRef clip, IEnumerable<ProposalLineRef> kept)
    {
        List<(double Start, double End)> keep = Merge(kept.Select(l => (Math.Max(clip.SrcStart, l.StartSec - PadSec), Math.Min(clip.SrcEnd, l.EndSec + PadSec))).Where(x => x.Item2 > x.Item1), WordSpans(clip));
        List<(double, double)> rest = [];
        double cursor = clip.SrcStart;
        foreach ((double s, double e) in keep)
        {
            if (s > cursor) rest.Add((cursor, s));
            cursor = Math.Max(cursor, e);
        }

        if (cursor < clip.SrcEnd) rest.Add((cursor, clip.SrcEnd));
        return Finish(rest, clip);
    }

    /// <summary>Clipped to the clip's visible source window, sorted, merged, empty and tiny spans dropped.</summary>
    private static IReadOnlyList<(double Start, double End)> Finish(IEnumerable<(double Start, double End)> spans, ProposalClipRef clip)
    {
        IEnumerable<(double, double)> clipped = spans
            .Where(x => double.IsFinite(x.Start) && double.IsFinite(x.End))
            .Select(x => (Math.Max(clip.SrcStart, x.Start), Math.Min(clip.SrcEnd, x.End)))
            .Where(x => x.Item2 - x.Item1 >= MinCutSec);
        return Merge(clipped, WordSpans(clip)).Where(x => x.End - x.Start >= MinCutSec).ToList();
    }

    /// <summary>The transcript's word spans (else its line spans) of a clip's file: the content a coalesced cut must never swallow.</summary>
    internal static IReadOnlyList<(double Start, double End)> WordSpans(ProposalClipRef clip)
    {
        SourceFacts f = clip.Facts ?? SourceFacts.Empty;
        return f.Words.Count > 0 ? f.Words.Select(w => (w.StartSec, w.EndSec)).ToList() : f.Lines.Select(l => (l.StartSec, l.EndSec)).ToList();
    }

    /// <summary>
    /// Sorts and merges spans. Overlapping or touching spans always merge; spans less than <see cref="MinKeepSec"/> apart merge only
    /// when no transcript word overlaps the gap between them (a short kept word is never cut away).
    /// </summary>
    internal static List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> spans, IReadOnlyList<(double Start, double End)>? words = null)
    {
        List<(double Start, double End)> merged = [];

        // Words sorted by start with a running maximum of their ends: "does any word start before s and end after prevEnd" is one binary search.
        double[]? starts = null;
        double[]? maxEnds = null;
        if (words is { Count: > 0 })
        {
            (double Start, double End)[] sorted = [.. words.OrderBy(w => w.Start)];
            starts = new double[sorted.Length];
            maxEnds = new double[sorted.Length];
            double running = double.NegativeInfinity;
            for (int i = 0; i < sorted.Length; i++)
            {
                starts[i] = sorted[i].Start;
                running = Math.Max(running, sorted[i].End);
                maxEnds[i] = running;
            }
        }

        bool WordInGap(double s, double prevEnd)
        {
            if (starts is null || maxEnds is null) return false;
            int lo = 0, hi = starts.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (starts[mid] < s) lo = mid + 1; else hi = mid;
            }

            return lo > 0 && maxEnds[lo - 1] > prevEnd;
        }

        foreach ((double s, double e) in spans.OrderBy(x => x.Start))
        {
            bool join = false;
            if (merged.Count > 0)
            {
                double prevEnd = merged[^1].End;
                join = s <= prevEnd || (s - prevEnd < MinKeepSec && !WordInGap(s, prevEnd));
            }

            if (join) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            else merged.Add((s, e));
        }

        return merged;
    }

    private static string Normalize(string? text) =>
        new string((text ?? string.Empty).Where(c => char.IsLetter(c)).ToArray()).ToLower(CultureInfo.InvariantCulture);
}
