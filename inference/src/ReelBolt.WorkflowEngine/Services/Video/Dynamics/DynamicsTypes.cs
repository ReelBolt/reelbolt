using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Dynamics;

/// <summary>One kept span, in program order. Times are source seconds; <see cref="SourceAudioMuted"/> is the span's <c>MuteSourceAudio</c>. <see cref="LimitEndSec"/> is the source time the span must not read past (the next shot's start), null when unknown.</summary>
public sealed record DynamicsSpan(string Id, int SourceIndex, double StartSec, double EndSec, bool SourceAudioMuted, double? LimitEndSec = null);

/// <summary>
/// The music grid in program time. <see cref="AccentSec"/> are grid lines whose onset strength is in the top decile; the
/// beat probe does not expose onset strength today, so the caller passes null and the planner falls back to
/// the drop line plus the bar lines (docs/design/dynamic-edit.md, accent).
/// </summary>
public sealed record DynamicsGrid(
    double BeatSec,
    double BeatPhaseSec,
    double BarPhaseSec,
    double? DropSec,
    IReadOnlyList<double>? AccentSec = null)
{
    /// <summary>The planner grid of a measured analysis.</summary>
    public static DynamicsGrid From(MusicBeatAnalysis a, IReadOnlyList<double>? accentSec = null) =>
        new(a.BeatSec, a.BeatPhaseSec, a.BarPhaseSec, a.DropSec, accentSec);

    /// <summary>Beat lines in [0, untilSec).</summary>
    public IReadOnlyList<double> Beats(double untilSec) => Lines(BeatPhaseSec, BeatSec, untilSec);

    /// <summary>Bar lines in [0, untilSec).</summary>
    public IReadOnlyList<double> Bars(double untilSec) => Lines(BarPhaseSec, BeatSec * 4, untilSec);

    /// <summary>Accent lines in [0, untilSec): the measured ones, else the drop line and every bar line.</summary>
    public IReadOnlyList<double> Accents(double untilSec)
    {
        if (AccentSec is { Count: > 0 })
            return AccentSec.Where(t => t >= 0 && t < untilSec).OrderBy(t => t).ToList();
        var all = new List<double>(Bars(untilSec));
        if (DropSec is { } d && d >= 0 && d < untilSec) all.Add(d);
        return all.OrderBy(t => t).Distinct().ToList();
    }

    private static List<double> Lines(double phase, double step, double untilSec)
    {
        var result = new List<double>();
        if (!(step > 0) || !double.IsFinite(step) || !double.IsFinite(phase)) return result;
        long k = phase < 0 ? (long)Math.Ceiling(-phase / step) : 0;
        for (double t = phase + k * step; t < untilSec; t = phase + ++k * step)
            result.Add(t);
        return result;
    }
}

/// <summary>One output segment: source frames [StartFrame, EndFrame) shown over <see cref="OutputFrames"/> frames (Speed = source frames per output frame).</summary>
public sealed record DynamicsSegment(string SpanId, int SourceIndex, long StartFrame, long EndFrame, long OutputFrames, double Speed = 1.0);

/// <summary>A one-frame colour overlay at a program frame (<c>white</c> or <c>black</c>).</summary>
public sealed record DynamicsFlash(long ProgramFrame, string Color);

/// <summary>A video-only retime of segment <see cref="SegmentIndex"/> at <see cref="Speed"/> (0.6/0.5/0.4).</summary>
public sealed record DynamicsRetime(int SegmentIndex, double Speed);

/// <summary>A whip seam at <see cref="ProgramFrame"/>: an <c>EditorTransitionCatalog</c> word (WhipLeft/WhipRight) of <see cref="DurationFrames"/>.</summary>
public sealed record DynamicsSeam(long ProgramFrame, string SpanId, string Word, long DurationFrames);

/// <summary>
/// An applied move. <see cref="CellsEndFrame"/> is, for an alternation, the program frame where its last whole cell ends (the cells are
/// <see cref="ProgramFrame"/> up to it; whatever follows is the winner's hold); 0 for every other move.
/// </summary>
public sealed record DynamicsApplied(string Effect, string AnchorId, string Moment, long ProgramFrame, bool Auto, string? Division = null, int Beats = 0, long CellsEndFrame = 0);

/// <summary>A dropped entry (<c>dynamics_entry_dropped</c>) and why.</summary>
public sealed record DynamicsDropped(string Effect, string AnchorId, string Reason)
{
    /// <summary>The report event name.</summary>
    public const string Code = "dynamics_entry_dropped";
}

/// <summary>What the planner did; goes to <c>outputSummary.dynamics</c>.</summary>
public sealed record DynamicsReport(
    IReadOnlyList<DynamicsApplied> Applied,
    IReadOnlyList<DynamicsDropped> Dropped,
    double MaxBeatOffsetMs);

/// <summary>The planner's products; each maps to an existing compile stage.</summary>
public sealed record DynamicsPlan(
    IReadOnlyList<DynamicsSegment> Segments,
    IReadOnlyList<VideoReframe> Reframes,
    IReadOnlyList<DynamicsFlash> Flashes,
    IReadOnlyList<DynamicsRetime> Retimes,
    IReadOnlyList<DynamicsSeam> Seams,
    DynamicsReport Report);
