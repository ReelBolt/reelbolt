namespace ReelBolt.WorkflowEngine.Services.Video.Quality;

/// <summary>Closed set of quality finding kinds. Reusable sampling/analysis helper constants.</summary>
public static class QualityKinds
{
    public const string LowContrast = "low_contrast";
    public const string FrozenFrames = "frozen_frames";
    public const string LoudnessOffTarget = "loudness_off_target";
    public const string LoudnessRangeWide = "loudness_range_wide";
    public const string TruePeakHigh = "true_peak_high";
    public const string Clipping = "clipping";
    public const string LongSilence = "long_silence";
    public const string NoSound = "no_sound";
}

/// <summary>Severity levels. High is reserved for definite problems because it caps a review score.</summary>
public static class QualitySeverity
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}

/// <summary>
/// One render-quality finding. <see cref="Detail"/> is plain English a customer can read.
/// Part of the reusable sampling/analysis helpers; pure data, no I/O.
/// </summary>
public sealed record QualityFinding(
    string Kind,
    string Severity,
    double? StartSec,
    double? EndSec,
    double? Measured,
    double? Limit,
    string Detail);
