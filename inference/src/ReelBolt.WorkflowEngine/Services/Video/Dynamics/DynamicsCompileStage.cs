using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Dynamics;

/// <summary>What the dynamics stage hands back to the compile.</summary>
public sealed record DynamicsStageResult(
    List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans,
    IReadOnlyList<VideoReframe> Reframes,
    JsonObject Report,
    IReadOnlyList<(string Code, string Message)> Warnings,
    IReadOnlyList<double>? Speeds = null,
    IReadOnlyList<IReadOnlyList<(int Frame, string Color)>>? Flashes = null,
    IReadOnlyList<DynamicsWhip>? Whips = null,
    int FpsNum = 0,
    int FpsDen = 1);

/// <summary>
/// A whip at the cut in front of result span <see cref="SpanIndex"/>: <c>WhipLeft</c> or <c>WhipRight</c>, <see cref="Frames"/> whole frames on each side of the cut
/// (one eighth of a beat, rounded). Duration-neutral: the cut stays a cut, the last and first frames are slid and blurred.
/// </summary>
public sealed record DynamicsWhip(int SpanIndex, string Word, int Frames);

/// <summary>
/// The compile-side glue of the dynamics planner (docs/design/dynamic-edit.md): segments, punch reframes,
/// BeatAlternate/StrobeCut cells, one-frame flashes (per-span local frame), speed ramps (per-span speed) and whip seams.
/// Never fails: anything it cannot do leaves the spans exactly as given and says why in the report.
/// </summary>
public static class DynamicsCompileStage
{
    /// <summary>The report code for dynamics reframes the compile's own validation dropped (also the dropped entries' reason).</summary>
    public const string ReframesDroppedCode = "dynamics_reframes_dropped";

    /// <summary>
    /// Compares the reframes the planner produced with the ones that survived the compile's validation. Every reframe that did not
    /// survive takes the applied PunchIn/PunchOut entry that made it out of <c>applied</c> (last first) into <c>dropped</c> with
    /// <see cref="ReframesDroppedCode"/>; <c>counts.reframesDropped</c> and <c>reframesDropped</c> carry the number. Returns the
    /// number of reframes dropped (0 leaves the report untouched).
    /// </summary>
    public static int ReconcileDroppedReframes(JsonObject report, int plannedReframes, int survivingReframes)
    {
        int lost = plannedReframes - survivingReframes;
        if (lost <= 0) return 0;
        if (report["applied"] is JsonArray applied && report["dropped"] is JsonArray drops)
        {
            for (int k = applied.Count - 1, removed = 0; k >= 0 && removed < lost; k--)
            {
                string? effect = applied[k]?["effect"]?.GetValue<string>();
                if (effect is not (nameof(DynamicEffect.PunchIn) or nameof(DynamicEffect.PunchOut))) continue;
                applied.RemoveAt(k);
                drops.Add(new JsonObject { ["effect"] = effect, ["reason"] = ReframesDroppedCode });
                removed++;
            }
            if (report["counts"] is JsonObject counts)
            {
                counts["applied"] = applied.Count;
                counts["dropped"] = drops.Count;
                counts["reframes"] = survivingReframes;
                counts["reframesDropped"] = lost;
            }
        }
        report["reframesDropped"] = lost;
        return lost;
    }

    /// <summary>
    /// Splits every span at the offered shot starts that fall strictly inside it, so a run of abutting shots the editor kept (which the
    /// compile coalesces into one span) keeps its cuts as cut points BEFORE the beat sync snaps them. <paramref name="footage"/> (the footage
    /// each span had before a length fit, parallel to the spans) is split with them; the extra tail stays on the last part.
    /// </summary>
    public static (List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans, List<double>? Footage) SplitAtBoundaries(
        List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> spans,
        List<double>? footage,
        IReadOnlyList<(double StartSec, int SourceIndex)> shotStarts)
    {
        bool parallel = footage is not null && footage.Count == spans.Count;
        var result = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>(spans.Count);
        var resultFootage = parallel ? new List<double>(spans.Count) : null;
        for (int i = 0; i < spans.Count; i++)
        {
            var sp = spans[i];
            double at = sp.Start;
            foreach (double b in shotStarts.Where(h => h.SourceIndex == sp.SourceIndex && h.StartSec > sp.Start + 1e-3 && h.StartSec < sp.End - 1e-3)
                         .Select(h => h.StartSec).Distinct().Order())
            {
                result.Add((at, b, sp.SourceIndex, sp.OriginIds));
                resultFootage?.Add(b - at);
                at = b;
            }

            result.Add((at, sp.End, sp.SourceIndex, sp.OriginIds));
            resultFootage?.Add(sp.End - at + (parallel ? Math.Max(0, footage![i] - (sp.End - sp.Start)) : 0));
        }

        return (result, resultFootage);
    }

    /// <summary>
    /// Keeps at most <paramref name="maxSegments"/> program segments, with every parallel list (speeds, flashes, whips) cut to match.
    /// Reports what it dropped as <c>dynamics_segment_cap</c>.
    /// </summary>
    public static DynamicsStageResult CapSegments(DynamicsStageResult result, int maxSegments)
    {
        if (maxSegments < 1 || result.Spans.Count <= maxSegments)
            return result;

        int dropped = result.Spans.Count - maxSegments;
        result.Report["segmentsDroppedOverCap"] = dropped;
        if (result.Report["counts"] is JsonObject counts)
            counts["segments"] = maxSegments;
        return result with
        {
            Spans = result.Spans.Take(maxSegments).ToList(),
            Speeds = result.Speeds?.Take(maxSegments).ToList(),
            Flashes = result.Flashes?.Take(maxSegments).ToList(),
            Whips = result.Whips?.Where(w => w.SpanIndex < maxSegments).ToList(),
            Warnings = [.. result.Warnings, ("dynamics_segment_cap", $"Dynamics left {result.Spans.Count} segments; {dropped} over the {maxSegments} cap were dropped.")]
        };
    }

    /// <summary>The most agent entries read; the output schema allows the same number.</summary>
    public const int MaxAgentEntries = 32;

    /// <summary>Runs the stage. <paramref name="idTime"/> resolves an offered id to its source time window.</summary>
    public static DynamicsStageResult Run(
        List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> spans,
        VideoDynamicsConfig dynamics,
        bool muteSourceAudio,
        IReadOnlyList<DynamicsEntryOutput>? agentEntries,
        MusicBeatAnalysis? grid,
        string? noGridReason,
        IReadOnlyList<(double StartSec, int SourceIndex)> shotStarts,
        Func<string, (double Start, double End, int SourceIndex)?> idTime,
        Func<int, (int Num, int Den)> fpsOf,
        double filmSec = 0,
        bool autoFill = true,
        IReadOnlyList<(int SourceIndex, double Sec)>? ownedMoments = null)
    {
        agentEntries = agentEntries?.Take(MaxAgentEntries).ToList();
        var unrealisedWhips = new HashSet<long>();
        int flashCount = 0, retimeCount = 0, whipCount = 0;
        var dropped = new List<(string Effect, string Reason)>();
        var warnings = new List<(string, string)>();
        JsonObject Report(DynamicsPlan? plan, int segments)
        {
            var applied = new JsonArray();
            foreach (DynamicsApplied a in plan?.Report.Applied ?? [])
                if (!(a.Effect == nameof(DynamicEffect.WhipOnBar) && unrealisedWhips.Contains(a.ProgramFrame)))
                {
                    var entry = new JsonObject { ["effect"] = a.Effect, ["anchorId"] = a.AnchorId, ["moment"] = a.Moment, ["programFrame"] = a.ProgramFrame, ["auto"] = a.Auto };
                    // An alternation says its cell division and length, so the file's cuts can be checked against the grid they sit on.
                    if (a.Division is not null) { entry["division"] = a.Division; entry["beats"] = a.Beats; entry["cellsEndFrame"] = a.CellsEndFrame; }
                    applied.Add(entry);
                }
            var drops = new JsonArray();
            foreach ((string e, string r) in dropped)
                drops.Add(new JsonObject { ["effect"] = e, ["reason"] = r });
            return new JsonObject
            {
                ["paceUsed"] = dynamics.Pace.ToString(),
                ["applied"] = applied,
                ["dropped"] = drops,
                ["counts"] = new JsonObject
                {
                    ["applied"] = applied.Count,
                    ["dropped"] = drops.Count,
                    ["segments"] = segments,
                    ["reframes"] = plan?.Reframes.Count ?? 0,
                    ["autoAdded"] = plan?.Report.Applied.Count(a => a.Auto) ?? 0,
                    ["flashes"] = flashCount,
                    ["retimes"] = retimeCount,
                    ["whips"] = whipCount
                },
                ["maxBeatOffsetMs"] = plan?.Report.MaxBeatOffsetMs ?? 0
            };
        }

        DynamicsStageResult Unchanged(string code, string message)
        {
            warnings.Add((code, message));
            return new(spans, [], Report(null, spans.Count), warnings);
        }

        if (spans.Count == 0)
            return Unchanged("dynamics_no_spans", "Dynamics had no spans to work on.");
        if (grid is null || !(grid.BeatSec > 0))
        {
            foreach (DynamicsEntryOutput e in agentEntries ?? [])
                dropped.Add((e?.Effect ?? "", "dynamics_no_beat_grid"));
            return Unchanged("dynamics_no_beat_grid",
                $"Dynamics needs a music beat grid and found none ({noGridReason ?? "no_beat_grid"}); the cut is unchanged.");
        }

        // Offered shot boundaries stay cut points: a span that runs across one is split there (no coalescing across them).
        var work = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>(spans.Count);
        foreach (var sp in spans)
        {
            double at = sp.Start;
            if (dynamics.KeepSceneBoundaries)
            {
                foreach (double b in shotStarts.Where(h => h.SourceIndex == sp.SourceIndex && h.StartSec > sp.Start + 1e-3 && h.StartSec < sp.End - 1e-3)
                             .Select(h => h.StartSec).Distinct().Order())
                {
                    work.Add((at, b, sp.SourceIndex, sp.OriginIds));
                    at = b;
                }
            }
            work.Add((at, sp.End, sp.SourceIndex, sp.OriginIds));
        }

        (int num, int den) = fpsOf(work[0].SourceIndex);
        double fps = num > 0 && den > 0 ? (double)num / den : 30.0;

        // A span may read one source frame past its end (QA-DYN-44's cumulative layout) but never into the next shot: the limit is the first shot start at or after its end.
        double? LimitOf((double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds) w) =>
            shotStarts.Where(h => h.SourceIndex == w.SourceIndex && h.StartSec >= w.End - 1e-3).Select(h => (double?)h.StartSec).Min();
        var planSpans = work.Select((w, i) => new DynamicsSpan($"p{i}", w.SourceIndex, w.Start, w.End, muteSourceAudio, LimitOf(w))).ToList();
        // The planner lays spans on whole frames (the cumulative layout of DynamicsPlanner.Layout); the cuts it is told must sit on those same
        // frames, or a cut a hair off the real seam looks like a second cut one frame away.
        var cuts = new List<double>();
        (long[] layoutStart, _, _) = DynamicsPlanner.Layout(planSpans, fps);
        foreach (long startFrame in layoutStart)
            cuts.Add(startFrame / fps);

        // Offered footage the story did not keep can still be the other side of an alternation: it is a pool span, never played on its own.
        var pool = new List<DynamicsSpan>();
        string Remap(string? id, bool allowPool = false)
        {
            string raw = (id ?? "").Trim();
            if (idTime(raw) is not { } t) return raw;
            double mid = (t.Start + t.End) / 2;
            for (int i = 0; i < work.Count; i++)
                if (work[i].SourceIndex == t.SourceIndex && mid >= work[i].Start - 1e-6 && mid < work[i].End + 1e-6)
                    return $"p{i}";
            if (!allowPool || !(t.End > t.Start)) return raw;
            int at = pool.FindIndex(q => q.SourceIndex == t.SourceIndex && q.StartSec == t.Start && q.EndSec == t.End);
            if (at < 0)
            {
                pool.Add(new DynamicsSpan($"q{pool.Count}", t.SourceIndex, t.Start, t.End, muteSourceAudio));
                at = pool.Count - 1;
            }
            return pool[at].Id;
        }

        var entries = new List<DynamicsEntry>();
        foreach (DynamicsEntryOutput? e in agentEntries ?? [])
        {
            if (e is null) { dropped.Add(("", "entry_null")); continue; }
            entries.Add(new DynamicsEntry(e.Effect ?? "", Remap(e.AnchorId), Remap(e.OtherId, allowPool: true), e.Moment ?? "", e.Beats, e.Division, e.Winner, e.Intensity, e.Reason ?? ""));
        }

        // Source moments an event move, flash cue or speed ramp owns, as program frames of the spans this stage lays out.
        var owned = new List<long>();
        foreach ((int srcIndex, double sec) in ownedMoments ?? [])
        {
            long at = 0;
            for (int i = 0; i < work.Count; i++)
            {
                long len = Math.Max(1, (long)Math.Round(work[i].End * fps, MidpointRounding.AwayFromZero) - (long)Math.Round(work[i].Start * fps, MidpointRounding.AwayFromZero));
                if (work[i].SourceIndex == srcIndex && sec >= work[i].Start && sec < work[i].End)
                {
                    owned.Add(at + (long)Math.Round((sec - work[i].Start) * fps, MidpointRounding.AwayFromZero));
                    break;
                }

                at += len;
            }
        }

        DynamicsPlan plan = DynamicsPlanner.Plan(planSpans, DynamicsGrid.From(grid), cuts, entries, dynamics.Pace, fps, pool, filmSec > 0 ? filmSec : null, autoFill, owned);
        foreach (DynamicsDropped d in plan.Report.Dropped)
            dropped.Add((d.Effect, d.Reason));
        foreach ((string e, string r) in dropped)
            warnings.Add((DynamicsDropped.Code, $"{e}: {r}"));
        int warnedDrops = dropped.Count;

        var result = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>(plan.Segments.Count);
        var speeds = new List<double>(plan.Segments.Count);
        var starts = new List<long>(plan.Segments.Count);
        long programAt = 0;
        foreach (DynamicsSegment seg in plan.Segments)
        {
            // A piece cut from pool footage carries no origin ids of its own (it came from an offered id the story left out).
            IReadOnlyList<string> origin = seg.SpanId[0] == 'q' ? [] : work[int.Parse(seg.SpanId[1..], System.Globalization.CultureInfo.InvariantCulture)].OriginIds;
            result.Add((seg.StartFrame / fps, seg.EndFrame / fps, seg.SourceIndex, origin));
            // Source frames per program frame, exact, so the retimed window lasts OutputFrames frames to the frame.
            speeds.Add(seg.Speed == 1.0 || seg.OutputFrames <= 0 ? 1.0 : (double)(seg.EndFrame - seg.StartFrame) / seg.OutputFrames);
            starts.Add(programAt);
            programAt += seg.OutputFrames;
        }
        retimeCount = speeds.Count(v => v != 1.0);

        // Flashes ride on the span that holds the frame (its own frame counter), so seam overlaps never move them.
        var flashes = new List<List<(int Frame, string Color)>>(result.Count);
        for (int i = 0; i < result.Count; i++) flashes.Add([]);
        foreach (DynamicsFlash f in plan.Flashes)
        {
            int at = starts.FindLastIndex(st => st <= f.ProgramFrame);
            if (at < 0 || f.ProgramFrame >= starts[at] + plan.Segments[at].OutputFrames)
            {
                dropped.Add((nameof(DynamicEffect.FlashFrame), "flash_frame_outside_program"));
                continue;
            }
            flashes[at].Add(((int)(f.ProgramFrame - starts[at]), f.Color));
            flashCount++;
        }

        // A whip needs a cut at that program frame and room for its frames on both sides. It is duration-neutral (no overlap), so it
        // needs no transition policy and keeps every later cut on its beat, also under beat sync.
        var whips = new List<DynamicsWhip>();
        foreach (DynamicsSeam seam in plan.Seams)
        {
            int at = starts.IndexOf(seam.ProgramFrame);
            string? why = at <= 0 ? "whip_not_at_cut"
                : plan.Segments[at - 1].OutputFrames <= seam.DurationFrames || plan.Segments[at].OutputFrames <= seam.DurationFrames ? "whip_span_too_short"
                : null;
            if (why is not null)
            {
                unrealisedWhips.Add(seam.ProgramFrame);
                dropped.Add((nameof(DynamicEffect.WhipOnBar), why));
                continue;
            }
            whips.Add(new DynamicsWhip(at, seam.Word, (int)seam.DurationFrames));
            whipCount++;
        }
        foreach ((string e, string r) in dropped.Skip(warnedDrops))
            warnings.Add((DynamicsDropped.Code, $"{e}: {r}"));
        return new(result, plan.Reframes, Report(plan, result.Count), warnings, speeds, [.. flashes], whips, num > 0 && den > 0 ? num : 30, num > 0 && den > 0 ? den : 1);
    }
}
