namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Speed-ramp extension of the output timeline. The compiled program is composed first (cuts, seams, captions, SFX, overlays,
/// graphics, inserts, region effects), then retimed as a whole; this maps any time on the composed program to the retimed one.
/// The reserved <c>EnableSpanMotion</c> config member is a no-op kept for stored configs: the ramps here are driven by
/// <c>SpeedRamps</c>, which supersedes it.
/// </summary>
public sealed partial class OutputTimeline
{
    private RampTimeline? _ramps;

    /// <summary>The program retime in force (null = none: every method below equals its unramped counterpart).</summary>
    internal RampTimeline? Ramps => _ramps;

    internal OutputTimeline WithRamps(RampTimeline? ramps)
    {
        _ramps = ramps is { IsIdentity: false } ? ramps : null;
        return this;
    }

    /// <summary>Total length after the ramps (<see cref="TotalSec"/> when there are none).</summary>
    public double RetimedTotalSec => _ramps is null ? TotalSec : _ramps.OutputSec;

    /// <summary>A time on the composed program as a time on the retimed program.</summary>
    public double RetimeOutputSec(double composedSec) => _ramps?.MapToOutput(composedSec) ?? composedSec;

    /// <summary>A source second as a time on the retimed program (null when the second is cut away).</summary>
    public double? MapToRetimedOutputSec(double sourceSec, int sourceIndex = 0) =>
        MapToOutputSec(sourceSec, sourceIndex) is { } t ? RetimeOutputSec(t) : null;
}
