using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.Events;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One speed ramp's outcome, as reported in the step output.</summary>
internal sealed record SpeedRampReportEntry(string EventId, bool Applied, string? Reason, double AtSec, double OutputAtSec, int Pieces);

/// <summary>The result of <see cref="SpeedRampPlanner.Plan"/>: the program retime and a per-entry report.</summary>
internal sealed record SpeedRampPlan(RampTimeline Timeline, IReadOnlyList<SpeedRampReportEntry> Report, IReadOnlyList<string> Warnings)
{
    internal bool Any => !Timeline.IsIdentity;

    internal static SpeedRampPlan None(double inputSec) => new(RampTimeline.Identity(inputSec), [], []);

    internal JsonObject ToNode()
    {
        var applied = new JsonArray();
        var dropped = new JsonArray();
        foreach (SpeedRampReportEntry e in Report)
        {
            if (e.Applied)
                applied.Add(new JsonObject { ["eventId"] = e.EventId, ["atSec"] = Math.Round(e.AtSec, 3), ["outputAtSec"] = Math.Round(e.OutputAtSec, 3), ["pieces"] = e.Pieces });
            else
                dropped.Add(new JsonObject { ["eventId"] = e.EventId, ["reason"] = e.Reason });
        }

        var node = new JsonObject
        {
            ["rampsApplied"] = applied,
            ["dropped"] = dropped,
            ["inputDurationSec"] = Math.Round(Timeline.InputSec, 3),
            ["outputDurationSec"] = Math.Round(Timeline.OutputSec, 3)
        };
        if (Warnings.Count > 0)
            node["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
        return node;
    }
}

/// <summary>
/// Turns <see cref="VideoCompileStepConfig.SpeedRamps"/> into one <see cref="RampTimeline"/> over the finished program
/// (docs/video-editing.md "Speed ramps"). Event ids resolve to a program time through the same output timeline the SFX cues
/// use; every number comes from the word tables here, never from config. A ramp that cannot apply is dropped with a reason,
/// never relocated. Pure: no I/O.
/// </summary>
internal static class SpeedRampPlanner
{
    public const string UnknownAnchorReason = "unknown_anchor_id";
    public const string CutAwayReason = "anchor_cut_away";
    public const string NotInSourceReason = "event_not_in_source";
    public const string OverlapReason = "overlaps_other_ramp";
    public const string TooShortReason = "span_outside_program";

    // ---- Word tables. ----
    // Playback rate of the slowed part (1 = unchanged). Audio can follow 0.25..4 (two atempo stages); these stay inside.
    internal static double SlowRate(SpeedRampStrength s) => s switch
    {
        SpeedRampStrength.Subtle => 0.75,
        SpeedRampStrength.Strong => 0.35,
        _ => 0.5
    };

    // Playback rate of a sped-up part.
    internal static double FastRate(SpeedRampStrength s) => s switch
    {
        SpeedRampStrength.Subtle => 1.5,
        SpeedRampStrength.Strong => 3.0,
        _ => 2.0
    };

    // Seconds of the INPUT program a ramp covers (a freeze holds for FreezeSeconds instead).
    internal static double SpanSeconds(SpeedRampSpan s) => s switch
    {
        SpeedRampSpan.Short => 0.5,
        SpeedRampSpan.Long => 1.5,
        _ => 1.0
    };

    internal static double FreezeSeconds(SpeedRampSpan s) => s switch
    {
        SpeedRampSpan.Short => 0.3,
        SpeedRampSpan.Long => 1.0,
        _ => 0.6
    };

    // A ramp in or out is a staircase of this many equal steps between normal speed and the strength rate.
    internal const int RampSteps = 4;

    // A window shorter than this once clamped to the program is dropped.
    internal const double MinWindowSeconds = 0.2;

    // Audio can follow a rate only inside this range (atempo 0.5..2 per stage, two stages).
    internal const double MinAudioRate = 0.25;
    internal const double MaxAudioRate = 4.0;

    // Two ranged windows conflict when they overlap; a freeze conflicts with a ranged window it falls strictly inside, and
    // with another freeze on the same instant.
    private static bool Conflicts(RampWindow a, RampWindow b)
    {
        if (a.IsFreeze && b.IsFreeze)
            return Math.Abs(a.Start - b.Start) < 1e-9;
        if (a.IsFreeze || b.IsFreeze)
        {
            (RampWindow f, RampWindow r) = a.IsFreeze ? (a, b) : (b, a);
            return f.Start > r.Start + 1e-9 && f.Start < r.End - 1e-9;
        }

        return a.Start < b.End - 1e-9 && b.Start < a.End - 1e-9;
    }

    /// <summary>The windows of one ramp placed at program time <paramref name="at"/>, clamped to <c>[0, programSec]</c>.</summary>
    internal static List<RampWindow> Windows(SpeedRampRequest r, double at, double programSec)
    {
        double span = SpanSeconds(r.Span);
        switch (r.Ramp)
        {
            case SpeedRampKind.FreezeHit:
                return [RampWindow.Freeze(Math.Clamp(at, 0, programSec), FreezeSeconds(r.Span))];
            case SpeedRampKind.SlowMo:
                return One(at, at + span, SlowRate(r.Strength));
            case SpeedRampKind.SpeedUp:
                return One(at, at + span, FastRate(r.Strength));
            case SpeedRampKind.RampIn:
            case SpeedRampKind.RampOut:
                {
                    double from = r.Ramp == SpeedRampKind.RampIn ? at - span : at;
                    double to = from + span;
                    double target = SlowRate(r.Strength);
                    var list = new List<RampWindow>();
                    double step = span / RampSteps;
                    for (int i = 0; i < RampSteps; i++)
                    {
                        // RampIn heads from 1 down to the target (the last step is the target); RampOut starts on it and heads back up.
                        double f = r.Ramp == SpeedRampKind.RampIn ? (i + 1.0) / RampSteps : 1.0 - (double)i / RampSteps;
                        double rate = 1 + (target - 1) * f;
                        double a = from + i * step, b = a + step;
                        if (b <= 0 || a >= programSec)
                            continue;
                        a = Math.Max(a, 0);
                        b = Math.Min(b, programSec);
                        if (b - a > 1e-9)
                            list.Add(new RampWindow(a, b, rate));
                    }

                    return list;
                }
            default:
                return [];
        }

        List<RampWindow> One(double a, double b, double rate)
        {
            a = Math.Max(a, 0);
            b = Math.Min(b, programSec);
            return b - a > 1e-9 ? [new RampWindow(a, b, rate)] : [];
        }
    }

    /// <summary>
    /// Plans every entry. <paramref name="composedProgramSec"/> is the length of the finished program the ramps retime;
    /// <paramref name="programOffsetSec"/> is what sits before the cut timeline in it (a cold open).
    /// </summary>
    internal static SpeedRampPlan Plan(
        VideoCompileStepConfig config,
        VideoAnalysisArtifact artifact,
        OutputTimeline timeline,
        double composedProgramSec,
        double programOffsetSec)
    {
        if (config.SpeedRamps is not { Count: > 0 } requests)
            return SpeedRampPlan.None(composedProgramSec);

        var offered = new HashSet<string>(artifact.OfferedEventIds ?? [], StringComparer.Ordinal);
        var windows = new List<RampWindow>();
        var report = new List<SpeedRampReportEntry>();
        foreach (SpeedRampRequest request in requests)
        {
            if (!offered.Contains(request.EventId) || VideoEventOffers.Find(artifact, request.EventId) is not { } ev)
            {
                report.Add(new(request.EventId, false, UnknownAnchorReason, 0, 0, 0));
                continue;
            }

            if (ev.SourceIndex is not { } source)
            {
                report.Add(new(request.EventId, false, NotInSourceReason, 0, 0, 0));
                continue;
            }

            if (timeline.MapToOutputSec(ev.ContentSec, source) is not { } composed)
            {
                report.Add(new(request.EventId, false, CutAwayReason, 0, 0, 0));
                continue;
            }

            double at = composed + programOffsetSec;
            List<RampWindow> mine = Windows(request, at, composedProgramSec);
            if (mine.Count == 0 || (!mine[0].IsFreeze && mine[^1].End - mine[0].Start < MinWindowSeconds))
            {
                report.Add(new(request.EventId, false, TooShortReason, at, 0, 0));
                continue;
            }

            if (windows.Any(w => mine.Any(m => Conflicts(m, w))))
            {
                report.Add(new(request.EventId, false, OverlapReason, at, 0, 0));
                continue;
            }

            windows.AddRange(mine);
            report.Add(new(request.EventId, true, null, at, 0, mine.Count));
        }

        windows = [.. windows.OrderBy(w => w.Start).ThenBy(w => w.IsFreeze ? 0 : 1)];
        RampTimeline timelineOut = RampTimeline.Build(composedProgramSec, windows);
        var warnings = new List<string>();
        for (int i = 0; i < report.Count; i++)
        {
            if (report[i].Applied)
                report[i] = report[i] with { OutputAtSec = timelineOut.MapToOutput(report[i].AtSec) };
        }

        return new SpeedRampPlan(timelineOut, report, warnings);
    }
}

/// <summary>
/// The ffmpeg filter graph of a <see cref="RampTimeline"/>: the program is cut into its pieces with trim/atrim, each piece
/// retimed (setpts; a freeze is one frame held with tpad; audio atempo stages, or silence when the rate is out of range) and the
/// pieces concatenated. Pure string building from typed numbers; nothing here reads config text.
/// </summary>
internal static class SpeedRampFilterBuilder
{
    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The atempo chain for a rate, or null when it lies outside <see cref="SpeedRampPlanner.MinAudioRate"/>..<see cref="SpeedRampPlanner.MaxAudioRate"/>.</summary>
    internal static string? AtempoChain(double rate)
    {
        if (!(rate >= SpeedRampPlanner.MinAudioRate - 1e-9 && rate <= SpeedRampPlanner.MaxAudioRate + 1e-9))
            return null;
        if (Math.Abs(rate - 1) < 1e-9)
            return string.Empty;
        // One stage covers 0.5..2; beyond that split into two equal stages (each inside the range).
        if (rate >= 0.5 - 1e-9 && rate <= 2 + 1e-9)
            return $"atempo={F(rate)}";
        double stage = Math.Sqrt(rate);
        return $"atempo={F(stage)},atempo={F(stage)}";
    }

    /// <summary>
    /// The filter_complex that retimes input 0 into <c>[vout]</c> (and <c>[aout]</c> when <paramref name="hasAudio"/>).
    /// <paramref name="fpsNum"/>/<paramref name="fpsDen"/> pin the output frame rate so the retimed picture stays constant-rate.
    /// Pieces whose audio rate is out of range play silence and add a warning.
    /// </summary>
    internal static string Build(RampTimeline timeline, bool hasAudio, int fpsNum, int fpsDen, List<string>? warnings = null)
    {
        var sb = new StringBuilder();
        int n = timeline.Pieces.Count;
        var vl = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            RampPiece p = timeline.Pieces[i];
            string vin = $"[v{i}]", ain = $"[a{i}]";
            if (p.IsFreeze)
            {
                // One frame at the held instant, held for the whole piece.
                sb.Append($"[0:v]trim=start={F(p.SrcStart)}:end={F(p.SrcStart + 0.1)},setpts=PTS-STARTPTS,trim=end_frame=1,tpad=stop_mode=clone:stop_duration={F(p.OutDuration)},trim=duration={F(p.OutDuration)},setpts=PTS-STARTPTS{vin};");
                if (hasAudio)
                    sb.Append($"[0:a]atrim=start={F(p.SrcStart)}:end={F(p.SrcStart + 0.02)},asetpts=PTS-STARTPTS,volume=0,apad=whole_dur={F(p.OutDuration)}{ain};");
            }
            else
            {
                string scale = Math.Abs(p.Rate - 1) < 1e-9 ? "setpts=PTS-STARTPTS" : $"setpts=(PTS-STARTPTS)*{F(1 / p.Rate)}";
                sb.Append($"[0:v]trim=start={F(p.SrcStart)}:end={F(p.SrcEnd)},{scale}{vin};");
                if (hasAudio)
                {
                    string? chain = AtempoChain(p.Rate);
                    if (chain is null)
                    {
                        warnings?.Add($"audio_muted_rate_out_of_range:{F(p.Rate)}");
                        sb.Append($"[0:a]atrim=start={F(p.SrcStart)}:end={F(p.SrcStart + 0.02)},asetpts=PTS-STARTPTS,volume=0,apad=whole_dur={F(p.OutDuration)}{ain};");
                    }
                    else
                    {
                        string tail = chain.Length == 0 ? string.Empty : "," + chain;
                        sb.Append($"[0:a]atrim=start={F(p.SrcStart)}:end={F(p.SrcEnd)},asetpts=PTS-STARTPTS{tail}{ain};");
                    }
                }
            }

            vl.Append(vin);
        }

        string fps = fpsNum > 0 && fpsDen > 0 ? $",fps={fpsNum}/{fpsDen}" : string.Empty;
        // One concat for picture and sound: it pads the shorter stream of each piece, so a piece never drifts the sync.
        if (hasAudio)
            sb.Append($"{string.Concat(Enumerable.Range(0, n).Select(i => $"[v{i}][a{i}]"))}concat=n={n}:v=1:a=1[vcat][aout];[vcat]null{fps}[vout]");
        else
            sb.Append($"{vl}concat=n={n}:v=1:a=0{fps}[vout]");
        return sb.ToString();
    }
}
