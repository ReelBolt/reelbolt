using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Dynamics;

/// <summary>
/// Turns the agent's closed-catalogue words into frame-aligned products (docs/design/dynamic-edit.md). Pure and deterministic:
/// every number comes from the grid, the spans and the fps; a word or id it cannot honour drops that entry with a reason,
/// never fails the plan. Drop order when over budget: BeatAlternate, StrobeCut, SpeedRamp, SplitOnBeat, PunchIn, PunchOut,
/// FlashFrame, WhipOnBar are listed highest priority first and the tail goes first. SpeedRamp, BeatAlternate, StrobeCut and
/// SplitOnBeat re-time or re-source the rest of their span, so they own the span from their start to its end.
/// </summary>
public static class DynamicsPlanner
{
    private static readonly DynamicEffect[] Priority =
    [
        DynamicEffect.BeatAlternate, DynamicEffect.StrobeCut, DynamicEffect.SpeedRamp, DynamicEffect.SplitOnBeat,
        DynamicEffect.PunchIn, DynamicEffect.PunchOut, DynamicEffect.FlashFrame, DynamicEffect.WhipOnBar
    ];

    private sealed record PaceRule(
        int EntriesPer10s, DynamicEffect[] Allowed, DynamicIntensity MaxIntensity, int MinGapBeats,
        int MaxAlternateBeats, int AutoCadenceBeats);

    private static PaceRule RuleFor(DynamicPace pace)
    {
        DynamicEffect[] calm = [DynamicEffect.PunchIn, DynamicEffect.WhipOnBar];
        DynamicEffect[] medium = [.. calm, DynamicEffect.SplitOnBeat, DynamicEffect.PunchOut, DynamicEffect.FlashFrame];
        DynamicEffect[] fast = [.. medium, DynamicEffect.BeatAlternate, DynamicEffect.StrobeCut, DynamicEffect.SpeedRamp];
        return pace switch
        {
            DynamicPace.Calm => new(1, calm, DynamicIntensity.Subtle, 4, 0, 0),
            DynamicPace.Medium => new(3, medium, DynamicIntensity.Normal, 2, 0, 4),
            _ => new(6, fast, DynamicIntensity.Strong, 1, 16, 2)
        };
    }

    /// <summary>Zoom of a punch by intensity.</summary>
    public static double ZoomFor(DynamicIntensity i) => i switch { DynamicIntensity.Subtle => 1.08, DynamicIntensity.Strong => 1.30, _ => 1.15 };

    private static double SpeedFor(DynamicIntensity i) => i switch { DynamicIntensity.Subtle => 0.6, DynamicIntensity.Strong => 0.4, _ => 0.5 };

    private sealed record Line(long Frame, double Sec);

    private sealed record Piece(string SpanId, int SourceIndex, long Start, long Frames, long Out, double Speed);

    private sealed record Cand(
        int Order, DynamicEffect Effect, string AnchorId, int SpanIdx, DynamicMoment Moment, DynamicIntensity Intensity,
        int OtherIdx, DynamicBeats Beats, DynamicDivision Division, DynamicWinner Winner, bool Auto, Line? Line, int Framing = 0);

    private sealed record Product(
        long From, long To, int SpanIdx, long ReplaceFrom, List<Piece>? Pieces, List<VideoReframe> Reframes,
        List<DynamicsFlash> Flashes, DynamicsSeam? Seam, List<double> OffsetsSec, long CellsEnd = 0);

    private sealed class Ctx
    {
        public required IReadOnlyList<DynamicsSpan> Spans { get; init; }
        /// <summary>Footage the program does not play but an alternation may cut to (offered, not kept); never a program span.</summary>
        public IReadOnlyList<DynamicsSpan> Pool { get; init; } = [];
        public required long[] ProgStart { get; init; }
        public required long[] ProgEnd { get; init; }
        public required long[] SrcStart { get; init; }
        public required double Fps { get; init; }
        public required double BeatSec { get; init; }
        public required long BeatFrames { get; init; }
        public required PaceRule Rule { get; init; }
        public required IReadOnlyList<long> BarFrames { get; init; }
        public required IReadOnlyList<long> ShotFrames { get; init; }
        public required DynamicsGrid Grid { get; init; }
    }

    /// <summary>
    /// Plans the dynamics. <paramref name="spans"/> are the kept spans in program order, <paramref name="grid"/> the music grid
    /// in program time, <paramref name="shotBoundaries"/> the existing cuts in program seconds, <paramref name="entries"/> the
    /// agent's words (empty = the pace's auto cadence), <paramref name="fps"/> the output frame rate.
    /// </summary>
    public static DynamicsPlan Plan(
        IReadOnlyList<DynamicsSpan> spans,
        DynamicsGrid grid,
        IReadOnlyList<double> shotBoundaries,
        IReadOnlyList<DynamicsEntry> entries,
        DynamicPace pace,
        double fps,
        IReadOnlyList<DynamicsSpan>? pool = null,
        double? budgetSec = null,
        bool fill = true,
        IReadOnlyList<long>? ownedFrames = null)
    {
        var dropped = new List<DynamicsDropped>();
        if (spans.Count == 0 || !(fps > 0) || !double.IsFinite(fps) || !(grid.BeatSec > 0) || !double.IsFinite(grid.BeatSec))
        {
            foreach (DynamicsEntry? e in entries)
                dropped.Add(new(e?.Effect ?? "", e?.AnchorId ?? "", "no_usable_grid_or_spans"));
            return Assemble(spans, new Ctx0(fps).Segments(spans), [], [], [], [], dropped, 0);
        }

        PaceRule rule = RuleFor(pace);
        (long[] progStart, long[] progEnd, long[] srcStart) = Layout(spans, fps);
        long cursor = progEnd[^1];
        double totalSec = cursor / fps;
        double until = totalSec + 1;
        var ctx = new Ctx
        {
            Spans = spans,
            Pool = pool ?? [],
            ProgStart = progStart,
            ProgEnd = progEnd,
            SrcStart = srcStart,
            Fps = fps,
            BeatSec = grid.BeatSec,
            BeatFrames = Math.Max(1, F(grid.BeatSec, fps)),
            Rule = rule,
            Grid = grid,
            BarFrames = grid.Bars(until).Select(t => F(t, fps)).ToList(),
            ShotFrames = shotBoundaries.Where(double.IsFinite).Select(t => F(t, fps)).Distinct().Order().ToList()
        };

        List<Cand> cands = [];
        var usedLines = new HashSet<(DynamicEffect, int, long)>();
        for (int n = 0; n < entries.Count; n++)
        {
            if (Resolve(ctx, entries[n], n, usedLines, dropped) is { } c) cands.Add(c);
        }

        // The pace bound is per 10 s of the film: the caller may pass the film's length (target length, with the cold open and end card)
        // when it is longer than the body the planner sees.
        double boundSec = Math.Max(totalSec, budgetSec ?? 0);
        int budget = rule.EntriesPer10s == 0 ? 0 : Math.Max(1, (int)Math.Floor(rule.EntriesPer10s * boundSec / 10.0 + 0.5));
        long gap = rule.MinGapBeats * ctx.BeatFrames;
        var accepted = new List<(Cand C, Product P)>();
        var perSource = new Dictionary<int, int>();
        int totalReframes = 0, totalKeys = 0;
        void Accept(Cand c)
        {
            // The auto fill is silent about slots it could not use: they are not the agent's words, so there is nothing to report.
            void Reject(string why) { if (!c.Auto) Drop(dropped, c, why); }
            if (accepted.Count >= budget) { Reject("pace_budget"); return; }
            (Product? p, string? why) = Realize(ctx, c);
            if (p is null) { Reject(why!); return; }
            // The gap runs from the START of a move (its anchor moment) to the start of the next, not from the end of a move that owns a
            // span: a move in the next shot, or a whip on the bar after a split, ramp or strobe, is not crowded by the span it follows.
            // A move that rewrites a span (pieces) still keeps every other move's start out of its own range, so two never overlap.
            bool Crowded(Product a) =>
                Math.Abs(p.From - a.From) < gap
                || (a.Pieces is not null && p.From >= a.From && p.From < a.To)
                || (p.Pieces is not null && a.From >= p.From && a.From < p.To);
            if (accepted.Any(a => Crowded(a.P))) { Reject("min_gap"); return; }
            // Two reframes of one source must never overlap (the compile drops every dynamics punch when they do): a plain punch is
            // bounded by the start of the next reframe on its own span (the earlier one is cut short, its ease compressed); anything
            // else that would overlap is a min_gap drop.
            for (int ai = 0; ai < accepted.Count; ai++)
            {
                (Cand ac, Product ap) = accepted[ai];
                if (!ReframesOverlap(p, ap)) continue;
                if (p.From >= ap.From && Truncate(ctx, ac, ap, p.From) is { } cutA) { accepted[ai] = (ac, cutA); continue; }
                if (p.From < ap.From && Truncate(ctx, c, p, ap.From) is { } cutP) { p = cutP; continue; }
                Reject("min_gap");
                return;
            }
            if (accepted.Any(a => ReframesOverlap(p, a.P))) { Reject("min_gap"); return; }
            // An event move, flash cue or speed ramp of the compile's own config already owns this moment: it keeps it.
            if (ownedFrames is { Count: > 0 } && ownedFrames.Any(o => p.From < o + 1 + gap && o < p.To + gap)) { Reject("event_move_owns_moment"); return; }
            int addR = p.Reframes.Count;
            int addK = p.Reframes.Sum(r => r.Keyframes.Count);
            if (totalReframes + addR > VideoReframe.MaxReframesTotal || totalKeys + addK > VideoReframe.MaxKeyframesTotal
                || p.Reframes.GroupBy(r => r.SourceIndex).Any(g => perSource.GetValueOrDefault(g.Key) + g.Count() > VideoReframe.MaxReframesPerSource))
            { Reject("reframe_cap"); return; }
            totalReframes += addR;
            totalKeys += addK;
            foreach (VideoReframe r in p.Reframes) perSource[r.SourceIndex] = perSource.GetValueOrDefault(r.SourceIndex) + 1;
            accepted.Add((c, p));
        }

        foreach (Cand c in cands.OrderBy(c => Array.IndexOf(Priority, c.Effect)).ThenBy(c => c.Order))
            Accept(c);
        // The fill: after every word of the agent, in time order, never within the min gap of an accepted move, never over the budget.
        if (fill)
            foreach (Cand c in AutoFill(ctx, until))
                Accept(c);

        accepted = [.. accepted.OrderBy(a => a.P.From).ThenBy(a => a.C.Order)];
        var seams = new List<DynamicsSeam>();
        int whips = 0;
        foreach (var a in accepted)
            if (a.P.Seam is { } s)
                seams.Add(s with { Word = whips++ % 2 == 0 ? "WhipLeft" : "WhipRight" });

        var reps = accepted.Where(a => a.P.Pieces is not null).Select(a => a.P).ToList();
        var segments = new List<DynamicsSegment>();
        for (int i = 0; i < spans.Count; i++)
        {
            long at = progStart[i];
            foreach (Product p in reps.Where(r => r.SpanIdx == i).OrderBy(r => r.ReplaceFrom))
            {
                if (p.ReplaceFrom > at) segments.Add(Identity(ctx, i, at, p.ReplaceFrom));
                foreach (Piece pc in p.Pieces!) segments.Add(new(pc.SpanId, pc.SourceIndex, pc.Start, pc.Start + pc.Frames, pc.Out, pc.Speed));
                at = progEnd[i];
            }
            if (at < progEnd[i]) segments.Add(Identity(ctx, i, at, progEnd[i]));
        }
        var retimes = new List<DynamicsRetime>();
        for (int i = 0; i < segments.Count; i++)
            if (segments[i].Speed != 1.0) retimes.Add(new(i, segments[i].Speed));

        double maxOffset = accepted.SelectMany(a => a.P.OffsetsSec).DefaultIfEmpty(0).Max(Math.Abs) * 1000.0;
        var applied = accepted.Select(a => new DynamicsApplied(
            a.C.Effect.ToString(), a.C.AnchorId, a.C.Moment.ToString(), a.P.From, a.C.Auto,
            a.C.Effect is DynamicEffect.BeatAlternate or DynamicEffect.StrobeCut ? a.C.Division.ToString() : null,
            a.C.Effect is DynamicEffect.BeatAlternate or DynamicEffect.StrobeCut ? BeatCount(a.C.Beats) : 0,
            a.P.CellsEnd)).ToList();
        return new DynamicsPlan(
            segments,
            accepted.SelectMany(a => a.P.Reframes).ToList(),
            accepted.SelectMany(a => a.P.Flashes).OrderBy(f => f.ProgramFrame).ToList(),
            retimes,
            seams,
            new DynamicsReport(applied, dropped, Math.Round(maxOffset, 3)));
    }

    // ---- helpers ----

    /// <summary>
    /// The frame layout of the kept spans on the program (QA-DYN-44). Every cut boundary is rounded from the EXACT cumulative time of the spans before it
    /// (sum of EndSec - StartSec), never from each span's own rounded length, so the error of one cut never carries into the next: laid per span
    /// (round(end) - round(start)), spans whose lengths are a fraction of a frame (a beat at 140 bpm is 12.857 frames) all rounded the same way and every
    /// later cut drifted a further 0.43 frame off its beat (82 ms by the last cut of the prod film). A span takes the frames its boundary asks for, but
    /// never more than its own window plus one source frame, and never past <see cref="DynamicsSpan.LimitEndSec"/> (the next shot's first frame): the
    /// shortfall is left to the next span, whose boundary is still the exact one. Spans whose lengths are whole frames lay out exactly as before.
    /// </summary>
    public static (long[] ProgStart, long[] ProgEnd, long[] SrcStart) Layout(IReadOnlyList<DynamicsSpan> spans, double fps)
    {
        var progStart = new long[spans.Count];
        var progEnd = new long[spans.Count];
        var srcStart = new long[spans.Count];
        long cursor = 0;
        double exact = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            srcStart[i] = F(spans[i].StartSec, fps);
            long ownLen = Math.Max(1, F(spans[i].EndSec, fps) - srcStart[i]);
            exact += Math.Max(0, spans[i].EndSec - spans[i].StartSec);
            long target = F(exact, fps) - cursor;
            long room = spans[i].LimitEndSec is { } limit && double.IsFinite(limit)
                ? Math.Max(ownLen, Math.Min(ownLen + 1, F(limit, fps) - srcStart[i]))
                : ownLen + 1;
            long len = Math.Max(1, Math.Min(target, room));
            progStart[i] = cursor;
            cursor += len;
            progEnd[i] = cursor;
        }

        return (progStart, progEnd, srcStart);
    }

    private sealed class Ctx0(double fps)
    {
        public List<DynamicsSegment> Segments(IReadOnlyList<DynamicsSpan> spans) =>
            spans.Select(s =>
            {
                long a = F(s.StartSec, fps);
                long b = Math.Max(a + 1, F(s.EndSec, fps));
                return new DynamicsSegment(s.Id, s.SourceIndex, a, b, b - a);
            }).ToList();
    }

    private static DynamicsPlan Assemble(
        IReadOnlyList<DynamicsSpan> spans, List<DynamicsSegment> segments, List<VideoReframe> reframes,
        List<DynamicsFlash> flashes, List<DynamicsRetime> retimes, List<DynamicsSeam> seams,
        List<DynamicsDropped> dropped, double offset) =>
        new(segments, reframes, flashes, retimes, seams, new DynamicsReport([], dropped, offset));

    private static long F(double sec, double fps) => (long)Math.Round(sec * fps, MidpointRounding.AwayFromZero);

    private static void Drop(List<DynamicsDropped> dropped, Cand c, string reason) =>
        dropped.Add(new(c.Effect.ToString(), c.AnchorId, reason));

    private static DynamicsSegment Identity(Ctx ctx, int spanIdx, long from, long to)
    {
        long start = ctx.SrcStart[spanIdx] + (from - ctx.ProgStart[spanIdx]);
        DynamicsSpan s = ctx.Spans[spanIdx];
        return new(s.Id, s.SourceIndex, start, start + (to - from), to - from);
    }

    private static bool TryWord<T>(string? s, out T value) where T : struct, Enum
    {
        value = default;
        // The whole trimmed token must equal one member name (case-insensitive). Enum.TryParse would also take a comma list
        // ("PunchIn, PunchOut" ORs two names into a third), a number or a substring, none of which is a word of the catalogue.
        string t = (s ?? "").Trim();
        foreach (string name in Enum.GetNames<T>())
        {
            if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse<T>(name);
                return true;
            }
        }

        return false;
    }

    private static int SpanIndex(IReadOnlyList<DynamicsSpan> spans, string? id)
    {
        for (int i = 0; i < spans.Count; i++)
            if (string.Equals(spans[i].Id, (id ?? "").Trim(), StringComparison.Ordinal)) return i;
        return -1;
    }

    private static List<Line> LinesFor(Ctx ctx, DynamicMoment m, double until)
    {
        IEnumerable<double> secs = m switch
        {
            DynamicMoment.OnBeat => ctx.Grid.Beats(until),
            DynamicMoment.OnBar => ctx.Grid.Bars(until),
            DynamicMoment.OnDrop => ctx.Grid.DropSec is { } d ? [d] : [],
            _ => ctx.Grid.Accents(until)
        };
        return secs.Select(t => new Line(F(t, ctx.Fps), t)).ToList();
    }

    private static double Until(Ctx ctx) => ctx.ProgEnd[^1] / ctx.Fps + 1;

    private static Cand? Resolve(Ctx ctx, DynamicsEntry? e, int order, HashSet<(DynamicEffect, int, long)> used, List<DynamicsDropped> dropped)
    {
        void D(string why) => dropped.Add(new(e?.Effect ?? "", e?.AnchorId ?? "", why));
        if (e is null) { D("entry_null"); return null; }
        if (!TryWord(e.Effect, out DynamicEffect effect)) { D("unknown_effect"); return null; }
        int idx = SpanIndex(ctx.Spans, e.AnchorId);
        if (idx < 0) { D("unknown_anchor_id"); return null; }
        if (!ctx.Rule.Allowed.Contains(effect)) { D("effect_not_allowed_at_pace"); return null; }

        DynamicMoment moment = DynamicMoment.OnBeat;
        if (effect == DynamicEffect.WhipOnBar) moment = DynamicMoment.OnBar;
        else if (!TryWord(e.Moment, out moment)) { D("unknown_moment"); return null; }

        DynamicIntensity intensity = DynamicIntensity.Normal;
        if (!string.IsNullOrWhiteSpace(e.Intensity) && !TryWord(e.Intensity, out intensity)) { D("unknown_intensity"); return null; }
        if (intensity > ctx.Rule.MaxIntensity) intensity = ctx.Rule.MaxIntensity;

        int other = -1;
        DynamicBeats beats = DynamicBeats.Four;
        DynamicDivision division = DynamicDivision.Beat;
        DynamicWinner winner = DynamicWinner.A;
        if (effect is DynamicEffect.BeatAlternate or DynamicEffect.StrobeCut)
        {
            other = SpanIndex(ctx.Spans, e.OtherId);
            if (other < 0 && SpanIndex(ctx.Pool, e.OtherId) is >= 0 and var inPool) other = ctx.Spans.Count + inPool;
            if (other < 0) { D("unknown_other_id"); return null; }
            if (other == idx) { D("other_id_equals_anchor"); return null; }
            if (effect == DynamicEffect.BeatAlternate)
            {
                if (!TryWord(e.Beats, out beats)) { D("unknown_beats"); return null; }
                if (!TryWord(e.Division, out division)) { D("unknown_division"); return null; }
                if (!TryWord(e.Winner, out winner)) { D("unknown_winner"); return null; }
                if (BeatCount(beats) > ctx.Rule.MaxAlternateBeats) { D("beats_over_pace_cap"); return null; }
            }
            else
            {
                beats = DynamicBeats.Four;
                if (!string.IsNullOrWhiteSpace(e.Division) && !TryWord(e.Division, out division)) { D("unknown_division"); return null; }
                winner = DynamicWinner.A;
            }
        }

        Line? line = null;
        if (effect != DynamicEffect.WhipOnBar)
        {
            foreach (Line l in LinesFor(ctx, moment, Until(ctx)))
            {
                bool inside = l.Frame >= ctx.ProgStart[idx] && l.Frame < ctx.ProgEnd[idx] - 1;
                if (effect == DynamicEffect.SplitOnBeat) inside = l.Frame > ctx.ProgStart[idx] && l.Frame < ctx.ProgEnd[idx];
                // The beat sync puts the drop on a cut, so the span before it ends exactly on the drop: a ramp INTO the drop belongs to that span.
                if (effect == DynamicEffect.SpeedRamp) inside = l.Frame > ctx.ProgStart[idx] && l.Frame <= ctx.ProgEnd[idx];
                if (!inside || used.Contains((effect, idx, l.Frame))) continue;
                line = l;
                break;
            }
            if (line is null) { D("no_grid_line_in_window"); return null; }
            used.Add((effect, idx, line.Frame));
        }
        return new Cand(order, effect, ((e.AnchorId ?? "").Trim()), idx, moment, intensity, other, beats, division, winner, false, line);
    }

    private static int BeatCount(DynamicBeats b) => b switch { DynamicBeats.Two => 2, DynamicBeats.Four => 4, DynamicBeats.Eight => 8, _ => 16 };

    /// <summary>Framings of the auto fill: the centre of each punch, a small closed table so the fill does not repeat one framing.</summary>
    private static readonly (double X, double Y)[] Framings = [(0.5, 0.5), (0.42, 0.5), (0.58, 0.5), (0.5, 0.42)];

    /// <summary>
    /// The auto fill (docs/design/dynamic-edit.md): Fast punches every 2 beats on beat lines, Medium every 4 beats on the accent
    /// lines (bars and the drop), Calm none. Alternating normal and strong punches, cycling the framing table. A candidate is only a
    /// slot: the acceptance loop drops it inside the min gap of any other move and stops at the pace budget.
    /// </summary>
    private static IEnumerable<Cand> AutoFill(Ctx ctx, double until)
    {
        int cadence = ctx.Rule.AutoCadenceBeats;
        if (cadence == 0) yield break;
        IEnumerable<Line> lines = ctx.Rule.MinGapBeats <= 1
            ? LinesFor(ctx, DynamicMoment.OnBeat, until).Where((_, i) => i % cadence == 0)
            : LinesFor(ctx, DynamicMoment.OnAccent, until);
        long minStep = cadence * ctx.BeatFrames;
        long last = long.MinValue / 2;
        int order = 0;
        foreach (Line l in lines)
        {
            if (l.Frame - last < minStep - ctx.BeatFrames / 2) continue;
            int idx = -1;
            for (int i = 0; i < ctx.Spans.Count; i++)
                if (l.Frame >= ctx.ProgStart[i] && l.Frame < ctx.ProgEnd[i] - ctx.BeatFrames / 2) { idx = i; break; }
            if (idx < 0) continue;
            last = l.Frame;
            DynamicIntensity strength = order % 2 == 0 ? DynamicIntensity.Normal : DynamicIntensity.Strong;
            yield return new Cand(order, DynamicEffect.PunchIn, ctx.Spans[idx].Id, idx, DynamicMoment.OnAccent,
                strength > ctx.Rule.MaxIntensity ? ctx.Rule.MaxIntensity : strength, -1, DynamicBeats.Four, DynamicDivision.Beat,
                DynamicWinner.A, true, l, Framing: order % Framings.Length);
            order++;
        }
    }

    private static bool ReframesOverlap(Product x, Product y) =>
        x.Reframes.Any(a => y.Reframes.Any(b => a.SourceIndex == b.SourceIndex
            && a.StartSec < (b.ToSec ?? double.PositiveInfinity) - 1e-9 && b.StartSec < (a.ToSec ?? double.PositiveInfinity) - 1e-9));

    /// <summary>
    /// A plain punch (one reframe, no pieces) cut short so it ends where the next move starts, on its own span; null when the move
    /// is not a plain punch, the next start is on another span, or too little is left.
    /// </summary>
    private static Product? Truncate(Ctx ctx, Cand c, Product p, long newTo)
    {
        if (p.Pieces is not null || p.Reframes.Count != 1 || c.Effect is not (DynamicEffect.PunchIn or DynamicEffect.PunchOut)) return null;
        if (newTo <= p.From || newTo > p.To || newTo > ctx.ProgEnd[p.SpanIdx] || p.From < ctx.ProgStart[p.SpanIdx]) return null;
        long len = newTo - p.From;
        if (len < 2) return null;
        VideoReframe r = c.Effect == DynamicEffect.PunchIn
            ? Punch(ctx, p.SpanIdx, p.From, len, c.Intensity, true, c.Framing)
            : PunchOutReframe(ctx, p.SpanIdx, p.From, len, c.Intensity);
        return p with { To = newTo, Reframes = [r] };
    }

    private static VideoReframe PunchOutReframe(Ctx ctx, int spanIdx, long progFrom, long len, DynamicIntensity intensity)
    {
        long src = ctx.SrcStart[spanIdx] + (progFrom - ctx.ProgStart[spanIdx]);
        return new VideoReframe(ctx.Spans[spanIdx].SourceIndex, src / ctx.Fps, (src + len) / ctx.Fps,
            [new(0, ZoomFor(intensity), 0.5, 0.5, ReframeEasing.EaseOut), new(len / ctx.Fps, 1.0, 0.5, 0.5, ReframeEasing.Linear)]);
    }

    private static VideoReframe Punch(Ctx ctx, int spanIdx, long progFrom, long frames, DynamicIntensity intensity, bool ease, int framing = 0)
    {
        long src = ctx.SrcStart[spanIdx] + (progFrom - ctx.ProgStart[spanIdx]);
        double z = ZoomFor(intensity);
        double fps = ctx.Fps;
        var keys = new List<ReframeKeyframe>();
        (double cx, double cy) = Framings[framing % Framings.Length];
        long hold = Math.Min(ctx.BeatFrames, frames);
        if (!ease || frames <= 1)
            keys.Add(new(0, z, cx, cy, ReframeEasing.Hold));
        else if (frames > hold)
        {
            keys.Add(new(0, z, cx, cy, ReframeEasing.Hold));
            keys.Add(new(hold / fps, z, cx, cy, ReframeEasing.EaseOut));
            keys.Add(new(frames / fps, 1.0, cx, cy, ReframeEasing.Linear));
        }
        else
        {
            keys.Add(new(0, z, cx, cy, ReframeEasing.EaseOut));
            keys.Add(new(frames / fps, 1.0, cx, cy, ReframeEasing.Linear));
        }
        return new VideoReframe(ctx.Spans[spanIdx].SourceIndex, src / fps, (src + frames) / fps, keys);
    }

    private static (Product?, string?) Realize(Ctx ctx, Cand c)
    {
        DynamicsSpan span = ctx.Spans[c.SpanIdx];
        long pf0 = ctx.ProgStart[c.SpanIdx], pf1 = ctx.ProgEnd[c.SpanIdx];
        long beat = ctx.BeatFrames;
        double fps = ctx.Fps;
        Line? line = c.Line;
        var offsets = new List<double>();
        if (line is not null) offsets.Add(line.Frame / fps - line.Sec);

        switch (c.Effect)
        {
            case DynamicEffect.PunchIn:
                {
                    // The fill punches last one beat, so two beats apart they sit a beat clear of each other (the min gap at Fast).
                    long len = Math.Min((c.Auto ? 1 : 2) * beat, pf1 - line!.Frame);
                    if (len < 1) return (null, "window_too_short");
                    return (new Product(line.Frame, line.Frame + len, c.SpanIdx, 0, null,
                        [Punch(ctx, c.SpanIdx, line.Frame, len, c.Intensity, true, c.Framing)], [], null, offsets), null);
                }
            case DynamicEffect.PunchOut:
                {
                    long len = Math.Min(beat, pf1 - line!.Frame);
                    if (len < 2) return (null, "window_too_short");
                    VideoReframe r = PunchOutReframe(ctx, c.SpanIdx, line.Frame, len, c.Intensity);
                    return (new Product(line.Frame, line.Frame + len, c.SpanIdx, 0, null, [r], [], null, offsets), null);
                }
            case DynamicEffect.FlashFrame:
                return (new Product(line!.Frame, line.Frame + 1, c.SpanIdx, 0, null, [],
                    [new(line.Frame, c.Intensity == DynamicIntensity.Strong ? "black" : "white")], null, offsets), null);
            case DynamicEffect.SpeedRamp:
                {
                    if (c.Moment is not (DynamicMoment.OnDrop or DynamicMoment.OnAccent)) return (null, "moment_not_supported");
                    if (!span.SourceAudioMuted) return (null, "source_audio_not_muted");
                    long a = line!.Frame - beat;
                    if (a < pf0) return (null, "window_too_short");
                    double speed = SpeedFor(c.Intensity);
                    long sf = Math.Max(1, (long)Math.Round(beat * speed, MidpointRounding.AwayFromZero));
                    long srcA = ctx.SrcStart[c.SpanIdx] + (a - pf0);
                    long postOut = pf1 - line.Frame;
                    // A tail of one or two frames is a flicker, not a piece (and a one-frame segment breaks the encode's concat): the ramp
                    // piece takes it, so the span keeps its frame count.
                    bool foldTail = postOut is > 0 and < 3;
                    var pieces = new List<Piece> { new(span.Id, span.SourceIndex, srcA, sf, foldTail ? beat + postOut : beat, speed) };
                    if (postOut >= 3) pieces.Add(new(span.Id, span.SourceIndex, srcA + sf, postOut, postOut, 1.0));
                    return (new Product(a, pf1, c.SpanIdx, a, pieces, [], [], null, offsets), null);
                }
            case DynamicEffect.SplitOnBeat:
                {
                    long spacing = Math.Max(ctx.Rule.MinGapBeats, 2) * beat;
                    var cuts = new List<long>();
                    bool tooClose = false;
                    foreach (Line l in LinesFor(ctx, c.Moment, Until(ctx)))
                    {
                        if (l.Frame < line!.Frame || l.Frame >= pf1) continue;
                        // A line within a beat of the span's start or end would leave a sliver piece: ignored.
                        if (l.Frame - pf0 < beat || pf1 - l.Frame < beat) { tooClose = true; continue; }
                        if (cuts.Count == 0 || l.Frame - cuts[^1] >= spacing)
                        {
                            cuts.Add(l.Frame);
                            if (cuts.Count > 1) offsets.Add(l.Frame / fps - l.Sec);
                        }
                    }
                    if (cuts.Count == 0) return (null, tooClose ? "split_too_close_to_edge" : "no_grid_line_in_window");
                    var pieces = new List<Piece>();
                    var reframes = new List<VideoReframe>();
                    for (int k = 0; k < cuts.Count; k++)
                    {
                        long from = cuts[k], to = k + 1 < cuts.Count ? cuts[k + 1] : pf1;
                        long src = ctx.SrcStart[c.SpanIdx] + (from - pf0);
                        pieces.Add(new(span.Id, span.SourceIndex, src, to - from, to - from, 1.0));
                        if (k % 2 == 0)
                        {
                            double z = ZoomFor(c.Intensity);
                            reframes.Add(new VideoReframe(span.SourceIndex, src / fps, (src + to - from) / fps,
                                [new(0, z, 0.5, 0.5, ReframeEasing.Hold)]));
                        }
                    }
                    return (new Product(cuts[0], pf1, c.SpanIdx, cuts[0], pieces, reframes, [], null, offsets), null);
                }
            case DynamicEffect.BeatAlternate:
            case DynamicEffect.StrobeCut:
                return Alternate(ctx, c, offsets);
            case DynamicEffect.WhipOnBar:
                {
                    long dur = Math.Max(1, F(ctx.BeatSec / 8.0, fps));
                    var cands = new List<long>();
                    if (c.SpanIdx > 0) cands.Add(pf0);
                    cands.AddRange(ctx.ShotFrames.Where(f => f > pf0 && f < pf1));
                    foreach (long s in cands)
                    {
                        if (ctx.BarFrames.Count == 0) break;
                        long best = ctx.BarFrames.Min(b => Math.Abs(b - s));
                        if (best <= beat / 2)
                        {
                            long bar = ctx.BarFrames.First(b => Math.Abs(b - s) == best);
                            offsets.Add((s - bar) / fps);
                            return (new Product(s, s + dur, c.SpanIdx, 0, null, [], [],
                                new DynamicsSeam(s, span.Id, "WhipLeft", dur), offsets), null);
                        }
                    }
                    return (null, "seam_not_on_bar");
                }
        }
        return (null, "unsupported");
    }

    private static (Product?, string?) Alternate(Ctx ctx, Cand c, List<double> offsets)
    {
        DynamicsSpan a = ctx.Spans[c.SpanIdx], b = c.OtherIdx < ctx.Spans.Count ? ctx.Spans[c.OtherIdx] : ctx.Pool[c.OtherIdx - ctx.Spans.Count];
        long pf0 = ctx.ProgStart[c.SpanIdx], pf1 = ctx.ProgEnd[c.SpanIdx];
        double fps = ctx.Fps;
        Line line = c.Line!;
        double cellSec = ctx.BeatSec * c.Division switch { DynamicDivision.HalfBeat => 0.5, DynamicDivision.Bar => 4.0, _ => 1.0 };
        int beats = BeatCount(c.Beats);
        int cells = Math.Max(1, (int)Math.Round(beats * ctx.BeatSec / cellSec, MidpointRounding.AwayFromZero));
        // Cell boundaries are rounded from the exact grid time, so rounding never drifts across cells.
        var bounds = new List<long>();
        for (int k = 0; k <= cells; k++)
            bounds.Add(Math.Min(pf1, k == 0 ? line.Frame : F(line.Sec + k * cellSec, fps)));
        int usable = 0;
        for (int k = 0; k < cells; k++)
            // Only whole cells count: a cell the window end clips to a frame or two is not an alternation, it is a flicker
            // (and a one-frame segment breaks the encode's concat). The remainder goes to the hold.
            if (bounds[k + 1] > bounds[k] && F(line.Sec + (k + 1) * cellSec, fps) <= pf1) usable++; else break;
        if (usable < 2) return (null, "window_too_short");
        cells = usable;
        // A tail of one or two frames after the last whole cell is a flicker: the last cell takes it.
        if (pf1 - bounds[cells] is > 0 and < 3) bounds[cells] = pf1;
        // A cell cut short by the end of the anchor window ends on that existing cut, not on a grid line the plan made: not an offset.
        for (int k = 1; k <= cells; k++)
            if (F(line.Sec + k * cellSec, fps) <= pf1) offsets.Add(bounds[k] / fps - (line.Sec + k * cellSec));

        long curA = ctx.SrcStart[c.SpanIdx] + (line.Frame - pf0);
        long curB = F(b.StartSec, fps);
        long endB = curB + Math.Max(1, F(b.EndSec, fps) - curB);
        var pieces = new List<Piece>();
        for (int k = 0; k < cells; k++)
        {
            long n = bounds[k + 1] - bounds[k];
            if (k % 2 == 0) { pieces.Add(new(a.Id, a.SourceIndex, curA, n, n, 1.0)); curA += n; }
            else { pieces.Add(new(b.Id, b.SourceIndex, curB, n, n, 1.0)); curB += n; }
        }
        long tail = pf1 - bounds[cells];
        if (tail > 0)
        {
            if (c.Winner == DynamicWinner.A) pieces.Add(new(a.Id, a.SourceIndex, curA, tail, tail, 1.0));
            else { pieces.Add(new(b.Id, b.SourceIndex, curB, tail, tail, 1.0)); curB += tail; }
        }
        if (curB > endB) return (null, "other_footage_too_short");
        return (new Product(line.Frame, pf1, c.SpanIdx, line.Frame, pieces, [], [], null, offsets, bounds[cells]), null);
    }
}
