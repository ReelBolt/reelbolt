namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One retimed stretch of the program: <c>[Start, End)</c> of the INPUT program plays at <see cref="Rate"/>
/// (2 = twice as fast), or - when <c>Start == End</c> and <see cref="HoldSec"/> is above 0 - the frame at
/// <c>Start</c> is held for <see cref="HoldSec"/> seconds.
/// </summary>
internal readonly record struct RampWindow(double Start, double End, double Rate, double HoldSec = 0)
{
    public bool IsFreeze => End <= Start && HoldSec > 0;

    internal static RampWindow Freeze(double at, double holdSec) => new(at, at, 1, holdSec);
}

/// <summary>One piece of a <see cref="RampTimeline"/>: input <c>[SrcStart, SrcEnd]</c> shown for <see cref="OutDuration"/> seconds.</summary>
internal readonly record struct RampPiece(double SrcStart, double SrcEnd, double OutDuration, double OutStart)
{
    public bool IsFreeze => SrcEnd <= SrcStart;

    public double Rate => IsFreeze || OutDuration <= 0 ? 0 : (SrcEnd - SrcStart) / OutDuration;

    public double OutEnd => OutStart + OutDuration;
}

/// <summary>
/// The pure piecewise-linear mapping between the program as it was composed (captions, SFX, overlays, graphics, inserts,
/// region effects and music are all placed on it) and the same program after the speed ramps. Pieces cover the whole input:
/// untouched stretches play at rate 1, windows play at their rate, a freeze adds a hold. No I/O, no ffmpeg; the filter
/// builder and the executor both read their numbers from here, so the picture, the audio and every reported time agree.
/// <para>
/// Mapping rules: input to output is monotone and continuous except at a freeze, where the held instant maps to the START of
/// the hold (so "the hit" lands where the hold begins) and every later input time shifts by the hold. Output to input is the
/// inverse; a time inside a hold maps back to the held instant.
/// </para>
/// </summary>
internal sealed class RampTimeline
{
    private RampTimeline(IReadOnlyList<RampPiece> pieces, double inputSec)
    {
        Pieces = pieces;
        InputSec = inputSec;
        OutputSec = pieces.Count == 0 ? 0 : pieces[^1].OutEnd;
    }

    public IReadOnlyList<RampPiece> Pieces { get; }

    public double InputSec { get; }

    public double OutputSec { get; }

    /// <summary>True when any piece is retimed (the identity timeline has none).</summary>
    public bool IsIdentity => Pieces.All(p => !p.IsFreeze && Math.Abs(p.Rate - 1) < 1e-12);

    public static RampTimeline Identity(double inputSec) => Build(inputSec, []);

    /// <summary>
    /// Builds the timeline. Windows must lie inside <c>[0, inputSec]</c>, be in order and not overlap, and each need a positive
    /// rate (or hold); anything else throws <see cref="ArgumentException"/> - the planner never produces such windows.
    /// </summary>
    public static RampTimeline Build(double inputSec, IReadOnlyList<RampWindow> windows)
    {
        if (!(inputSec >= 0))
            throw new ArgumentException("inputSec must be non-negative.", nameof(inputSec));

        var pieces = new List<RampPiece>();
        double src = 0, outCursor = 0;

        void Add(double from, double to, double outDur)
        {
            pieces.Add(new RampPiece(from, to, outDur, outCursor));
            outCursor += outDur;
        }

        foreach (RampWindow w in windows)
        {
            if (w.Start < src - 1e-9 || w.End > inputSec + 1e-9 || w.End < w.Start)
                throw new ArgumentException($"Ramp window [{w.Start}, {w.End}] is out of order or outside the program.", nameof(windows));
            if (w.IsFreeze)
            {
                if (w.Start > src + 1e-9)
                    Add(src, w.Start, w.Start - src);
                Add(w.Start, w.Start, w.HoldSec);
                src = w.Start;
                continue;
            }

            if (!(w.Rate > 0) || w.End - w.Start <= 1e-9)
                throw new ArgumentException("A ramp window needs a positive rate and length.", nameof(windows));
            if (w.Start > src + 1e-9)
                Add(src, w.Start, w.Start - src);
            Add(w.Start, w.End, (w.End - w.Start) / w.Rate);
            src = w.End;
        }

        if (inputSec > src + 1e-9)
            Add(src, inputSec, inputSec - src);
        return new RampTimeline(pieces, inputSec);
    }

    /// <summary>Output second of an input second (clamped to the program).</summary>
    public double MapToOutput(double inputSec)
    {
        double t = Math.Clamp(inputSec, 0, InputSec);
        foreach (RampPiece p in Pieces)
        {
            if (p.IsFreeze)
            {
                // The held instant maps to the start of its hold; the next piece continues after the hold.
                if (Math.Abs(t - p.SrcStart) < 1e-12)
                    return p.OutStart;
                continue;
            }

            if (t <= p.SrcEnd + 1e-12 && t >= p.SrcStart - 1e-12)
                return p.OutStart + (t - p.SrcStart) / (p.SrcEnd - p.SrcStart) * p.OutDuration;
        }

        return OutputSec;
    }

    /// <summary>Input second shown at an output second (inside a hold: the held instant).</summary>
    public double MapFromOutput(double outputSec)
    {
        double t = Math.Clamp(outputSec, 0, OutputSec);
        foreach (RampPiece p in Pieces)
        {
            if (t > p.OutEnd + 1e-12)
                continue;
            if (p.IsFreeze)
                return p.SrcStart;
            return p.SrcStart + (t - p.OutStart) / p.OutDuration * (p.SrcEnd - p.SrcStart);
        }

        return InputSec;
    }

    /// <summary>An input window as an output window: its start and end each mapped (a window across a freeze grows by the hold).</summary>
    public (double Start, double End) MapWindow(double startSec, double endSec)
    {
        double a = MapToOutput(startSec);
        double b = MapToOutput(endSec);
        return (a, b);
    }

    /// <summary>Output length of an input stretch (its retimed duration).</summary>
    public double MapDuration(double startSec, double endSec)
    {
        (double a, double b) = MapWindow(startSec, endSec);
        return b - a;
    }
}
