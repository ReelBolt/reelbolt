namespace ReelBolt.Shared.Workflows.Follow;

/// <summary>
/// How much of the visible window the followed subject should fill (fit in BOTH dimensions).
/// <b>APPEND ONLY</b> — persisted by name. Fill fractions live in <see cref="FollowTuning"/>.
/// </summary>
public enum FollowTightness
{
    /// <summary>Subject fills about 0.35 of the window.</summary>
    Loose,
    /// <summary>Subject fills about 0.50 of the window.</summary>
    Medium,
    /// <summary>Subject fills about 0.65 of the window.</summary>
    Tight
}

/// <summary>
/// How lazily the camera moves. <b>APPEND ONLY</b> — persisted by name. Constants live in
/// <see cref="FollowTuning"/>.
/// </summary>
public enum FollowSmoothness
{
    /// <summary>Slow, cinematic (time constant about 1.2 s).</summary>
    Gentle,
    /// <summary>Default (about 0.7 s).</summary>
    Normal,
    /// <summary>Quick, sporty (about 0.35 s).</summary>
    Snappy
}

/// <summary>What the camera does while the subject is lost. <b>APPEND ONLY</b> — persisted by name.</summary>
public enum FollowOnLost
{
    /// <summary>Keep the last known framing.</summary>
    Hold,
    /// <summary>Hold 1 s, then ease out to the full frame over 2 s.</summary>
    Widen
}

/// <summary>
/// Every tuning constant of the follow core. These are STARTING VALUES chosen by judgement, NOT
/// measured against real footage; expect to retune after seeing real output.
/// </summary>
public static class FollowTuning
{
    /// <summary>Resample rate of the camera path, Hz.</summary>
    public const double GridHz = 10.0;

    /// <summary>Gaps between present samples shorter than this are bridged by interpolation, s.</summary>
    public const double BridgeSec = 0.5;

    /// <summary>Lost stretches at least this long are reported in <see cref="CameraPathResult.LostSpans"/>, s.</summary>
    public const double LostSpanMinSec = 0.5;

    /// <summary>Widen: hold the last framing this long before moving, s.</summary>
    public const double WidenHoldSec = 1.0;

    /// <summary>Widen: then move to the full frame over this long, s.</summary>
    public const double WidenRampSec = 2.0;

    /// <summary>Dead zone: fraction of the current visible window (each axis) the target may roam without moving the camera.</summary>
    public const double DeadZoneFraction = 0.25;

    /// <summary>Dead zone: ln(zoom) difference below which the zoom intention does not change.</summary>
    public const double ZoomDeadZoneLn = 0.15;

    /// <summary>The zoom cap used when soft zoom is allowed (never below the sharp cap).</summary>
    public const double SoftZoomCap = 4.0;

    /// <summary>Wanted zoom must exceed the cap by more than this factor to count as cap-limited.</summary>
    public const double CapLimitedFactor = 1.05;

    /// <summary>Decimation starting tolerance (window units / ln zoom).</summary>
    public const double DecimationStartTolerance = 0.01;

    /// <summary>
    /// Spare room kept between a followed subject's box and the visible window's edge, as a fraction of the
    /// window, when the camera has to move to keep the subject in the picture.
    /// </summary>
    public const double KeepInsideMargin = 0.03;

    /// <summary>
    /// Share of the pan-speed cap the keep-in-the-picture correction may use, so the decimated keys stay within
    /// the cap once interpolated.
    /// </summary>
    public const double KeepInsideSpeedShare = 0.9;

    /// <summary>Output decimal places.</summary>
    public const int Decimals = 5;

    /// <summary>Subject fill fraction of the visible window.</summary>
    public static double Fill(FollowTightness t) => t switch
    {
        FollowTightness.Loose => 0.35,
        FollowTightness.Tight => 0.65,
        _ => 0.50
    };

    /// <summary>Smoothing time constant, s.</summary>
    public static double TimeConstantSec(FollowSmoothness s) => s switch
    {
        FollowSmoothness.Gentle => 1.2,
        FollowSmoothness.Snappy => 0.35,
        _ => 0.7
    };

    /// <summary>Max pan speed in visible-window widths per second.</summary>
    public static double MaxPanWindowsPerSec(FollowSmoothness s) => s switch
    {
        FollowSmoothness.Gentle => 0.35,
        FollowSmoothness.Snappy => 1.0,
        _ => 0.6
    };

    /// <summary>Max zoom change in ln(zoom) per second.</summary>
    public static double MaxZoomLnPerSec(FollowSmoothness s) => s switch
    {
        FollowSmoothness.Gentle => 0.35,
        FollowSmoothness.Snappy => 1.0,
        _ => 0.6
    };
}

/// <summary>
/// One tracked subject box at one time, in fractions (0..1) of the SOURCE frame. Not present
/// means the tracker had no subject then; the box is ignored.
/// </summary>
public sealed record SubjectSample(double TimeSec, double X, double Y, double Width, double Height, bool Present);

/// <summary>
/// The fraction of the SOURCE frame that is visible at zoom 1 once the clip is fitted to the
/// output. At zoom z the visible window is (VisibleWidth/z, VisibleHeight/z).
/// </summary>
public sealed record FollowFraming(double VisibleWidth, double VisibleHeight)
{
    /// <summary>The whole frame is visible (same aspect, or a contain fit).</summary>
    public static FollowFraming Full { get; } = new(1.0, 1.0);

    /// <summary>A source of <paramref name="sourceAspect"/> covered into an output of <paramref name="outputAspect"/> (both width/height).</summary>
    public static FollowFraming Cover(double sourceAspect, double outputAspect)
    {
        if (!double.IsFinite(sourceAspect) || !double.IsFinite(outputAspect) || sourceAspect <= 0 || outputAspect <= 0)
            throw new ArgumentException("Aspect ratios must be positive finite numbers.");
        return outputAspect < sourceAspect
            ? new FollowFraming(outputAspect / sourceAspect, 1.0)
            : new FollowFraming(1.0, sourceAspect / outputAspect);
    }
}

/// <summary>Everything the caller chooses about a follow.</summary>
public sealed record FollowOptions(
    FollowTightness Tightness,
    FollowSmoothness Smoothness,
    FollowOnLost OnLost,
    bool AllowSoftZoom,
    FollowFraming Framing,
    int SourceHeightPx,
    int OutputHeightPx,
    int MaxKeys,
    double MaxZoomLimit = VideoReframe.MaxZoom);

/// <summary>A camera key: centre as fractions of the SOURCE frame, zoom &gt;= 1 (as <see cref="ReframeKeyframe"/>).</summary>
public sealed record CameraKey(double TimeSec, double CenterX, double CenterY, double Zoom);

/// <summary>A stretch where the subject was not tracked.</summary>
public sealed record FollowLostSpan(double FromSec, double ToSec);

/// <summary>The computed camera path and its quality facts.</summary>
public sealed record CameraPathResult(
    IReadOnlyList<CameraKey> Keys,
    double ZoomCap,
    bool ZoomCapLimited,
    IReadOnlyList<FollowLostSpan> LostSpans,
    int ClampCount,
    double MaxErrorAfterDecimation);
