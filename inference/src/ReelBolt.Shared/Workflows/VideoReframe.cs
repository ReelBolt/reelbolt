using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// How a reframe moves from one keyframe to the next (see docs/video-editing.md "Keyframed
/// reframing"). The easing belongs to the keyframe the move LEAVES: keyframe <c>i</c>'s easing
/// shapes the stretch from <c>i</c> to <c>i + 1</c>; the last keyframe's easing is unused.
/// <b>APPEND ONLY</b> — persisted by name in <c>video_compile_config_json</c>.
/// </summary>
public enum ReframeEasing
{
    /// <summary>Constant speed.</summary>
    Linear,
    /// <summary>Starts slow, ends fast (<c>u²</c>).</summary>
    EaseIn,
    /// <summary>Starts fast, settles into the next keyframe (<c>u(2 − u)</c>).</summary>
    EaseOut,
    /// <summary>Slow at both ends (smoothstep, <c>u²(3 − 2u)</c>).</summary>
    EaseInOut,
    /// <summary>Holds this keyframe's framing until the next keyframe, then cuts to it.</summary>
    Hold
}

/// <summary>
/// What a reframe's keyframe centres name. <b>APPEND ONLY</b> — persisted by name in
/// <c>video_compile_config_json</c>.
/// </summary>
public enum ReframeAnchor
{
    /// <summary>
    /// The keyframe centre is the centre of the zoom WINDOW cropped out of the source frame (clamped
    /// so the window stays inside it), before the clip is fitted to the canvas. Under a covering fit
    /// the canvas then shows a centred strip of that window, so the picture cannot pan to the edges.
    /// </summary>
    Window,
    /// <summary>
    /// The keyframe centre is the centre of what the CANVAS shows, as fractions of the source frame,
    /// and zoom is relative to the clip's own fit (1 = the fitted picture). Under a covering fit the
    /// engine pans the cover crop as well, so the picture reaches the source edges. Under a contain
    /// fit (nothing cropped) it equals <see cref="Window"/>.
    /// </summary>
    Visible
}

/// <summary>
/// One framing of a reframe. <paramref name="AtSec"/> is relative to the reframe's
/// <see cref="VideoReframe.FromSec"/> in SOURCE time. <paramref name="Zoom"/> is 1.0 (the full
/// frame) to 8.0; <paramref name="CenterX"/>/<paramref name="CenterY"/> are the window's centre as
/// fractions (0..1) of the source frame — clamped so the window stays inside it.
/// </summary>
public sealed record ReframeKeyframe(
    double AtSec,
    double Zoom = 1.0,
    double CenterX = 0.5,
    double CenterY = 0.5,
    ReframeEasing Easing = ReframeEasing.Linear);

/// <summary>
/// A keyframed zoom/crop/pan over one stretch of one source clip (see docs/video-editing.md
/// "Keyframed reframing"). <paramref name="FromSec"/>/<paramref name="ToSec"/> bound it in the
/// clip's own time (null = the clip's start / end); outside it the clip is shown whole. Before
/// the first keyframe the first framing holds, after the last the last framing holds. Applied to
/// every kept span of the clip that overlaps the stretch, before the clip is fitted to the canvas.
/// </summary>
public sealed record VideoReframe(
    int SourceIndex,
    double? FromSec,
    double? ToSec,
    IReadOnlyList<ReframeKeyframe> Keyframes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ReframeAnchor Anchor = ReframeAnchor.Window)
{
    /// <summary>The most keyframes one reframe may carry (each is three expression terms in the filter).</summary>
    public const int MaxKeyframes = 32;

    /// <summary>The most reframes one source clip may carry (each is a nested zoompan curve on every span it touches).</summary>
    public const int MaxReframesPerSource = 16;

    /// <summary>The most reframes one compile may carry, across every source.</summary>
    public const int MaxReframesTotal = 64;

    /// <summary>The most keyframes one compile may carry, across every reframe.</summary>
    public const int MaxKeyframesTotal = 512;

    /// <summary>The full frame.</summary>
    public const double MinZoom = 1.0;

    /// <summary>An eighth of the frame each way — past that a 1080p source is visibly soft.</summary>
    public const double MaxZoom = 8.0;

    /// <summary>The reframe's start in source seconds (<see cref="FromSec"/>, or the clip's start). Derived; never persisted.</summary>
    [JsonIgnore]
    public double StartSec => FromSec ?? 0;

    /// <summary>The largest <see cref="VideoCompileStepConfig.SubjectSafeArea"/> (a quarter of the canvas each side).</summary>
    public const double MaxSafeArea = 0.25;

    /// <summary>The safe-area margin used when <see cref="VideoCompileStepConfig.SubjectSafeArea"/> is null.</summary>
    public const double DefaultSafeArea = 0.05;

    /// <summary>
    /// The config-only checks of reframing and the subject safe area, before anything is
    /// resolved: <see cref="Validate"/> on <see cref="VideoCompileStepConfig.Reframes"/>,
    /// <c>Mode = Reencode</c> when there are any, and the safe-area range. Source indices are
    /// checked against the analysis later (they need the artifact).
    /// </summary>
    public static VideoReframeValidation CheckConfig(VideoCompileStepConfig config)
    {
        if (config.SubjectSafeArea is { } margin && (!double.IsFinite(margin) || margin < 0 || margin > MaxSafeArea))
            return VideoReframeValidation.Error(VideoReframeCodes.SubjectSafeAreaOutOfRange, $"SubjectSafeArea must be 0 to {MaxSafeArea}.");
        if (config.Reframes is { Count: > 0 } && config.Mode == VideoCompileMode.StreamCopy)
            return VideoReframeValidation.Error(VideoReframeCodes.RequiresReencode, "Reframes require Mode=Reencode — a zoom or pan has no stream-copy equivalent.");
        if (FollowSubjectRequest.Check(config) is { } followError)
            return followError;
        if (EventMoveRequest.Check(config) is { } eventMoveError)
            return eventMoveError;
        if (FlashCueRequest.Check(config) is { } flashCueError)
            return flashCueError;
        if (SpeedRampRequest.Check(config) is { } speedRampError)
            return speedRampError;
        if (EventCutCodes.Check(config) is { } eventCutError)
            return eventCutError;
        return Validate(config.Reframes);
    }

    /// <summary>
    /// Checks every reframe of a config. The first violation is the result's error (a config
    /// error: <c>REFRAME_*</c>); a valid list comes back normalized — each keyframe's centre
    /// moved, where needed, so its zoom window stays inside the frame, every such move reported
    /// in <see cref="VideoReframeValidation.Clamps"/>.
    /// </summary>
    public static VideoReframeValidation Validate(IReadOnlyList<VideoReframe>? reframes)
    {
        if (reframes is null || reframes.Count == 0)
            return VideoReframeValidation.Valid([], []);

        // Caps first, before any per-keyframe work: every reframe becomes a nested expression on
        // every span it touches, so the list's size is bounded before it is walked.
        if (reframes.Count > MaxReframesTotal)
            return VideoReframeValidation.Error(VideoReframeCodes.TooManyReframes, $"{reframes.Count} reframes; at most {MaxReframesTotal} per compile.");
        if (reframes.Where(r => r is not null).GroupBy(r => r.SourceIndex).FirstOrDefault(g => g.Count() > MaxReframesPerSource) is { } crowded)
            return VideoReframeValidation.Error(VideoReframeCodes.TooManyReframes, $"Source {crowded.Key} has {crowded.Count()} reframes; at most {MaxReframesPerSource} per source.");
        long totalKeyframes = reframes.Sum(r => (long)(r?.Keyframes?.Count ?? 0));
        if (totalKeyframes > MaxKeyframesTotal)
            return VideoReframeValidation.Error(VideoReframeCodes.TooManyKeyframes, $"{totalKeyframes} keyframes in all; at most {MaxKeyframesTotal} per compile.");

        var normalized = new List<VideoReframe>(reframes.Count);
        var clamps = new List<ReframeClamp>();
        for (int r = 0; r < reframes.Count; r++)
        {
            VideoReframe reframe = reframes[r];
            if (reframe is null)
                return VideoReframeValidation.Error(VideoReframeCodes.NoKeyframes, $"Reframe {r} is empty.");
            if (reframe.SourceIndex < 0)
                return VideoReframeValidation.Error(VideoReframeCodes.UnknownSource, $"Reframe {r}: SourceIndex {reframe.SourceIndex} is not a source clip.");
            if (reframe.FromSec is { } from && (!double.IsFinite(from) || from < 0))
                return VideoReframeValidation.Error(VideoReframeCodes.RangeInvalid, $"Reframe {r}: FromSec must be a non-negative number of seconds.");
            if (reframe.ToSec is { } to && (!double.IsFinite(to) || to <= reframe.StartSec))
                return VideoReframeValidation.Error(VideoReframeCodes.RangeInvalid, $"Reframe {r}: ToSec must be after FromSec.");
            if (!Enum.IsDefined(reframe.Anchor))
                return VideoReframeValidation.Error(VideoReframeCodes.AnchorUnknown, $"Reframe {r}: Anchor is not a known anchor.");
            if (reframe.Keyframes is not { Count: > 0 })
                return VideoReframeValidation.Error(VideoReframeCodes.NoKeyframes, $"Reframe {r} has no keyframes.");
            if (reframe.Keyframes.Count > MaxKeyframes)
                return VideoReframeValidation.Error(VideoReframeCodes.TooManyKeyframes, $"Reframe {r} has {reframe.Keyframes.Count} keyframes; at most {MaxKeyframes}.");

            var keys = new List<ReframeKeyframe>(reframe.Keyframes.Count);
            double previous = double.NegativeInfinity;
            for (int k = 0; k < reframe.Keyframes.Count; k++)
            {
                ReframeKeyframe key = reframe.Keyframes[k];
                if (key is null || !double.IsFinite(key.AtSec) || key.AtSec < 0 || key.AtSec <= previous)
                    return VideoReframeValidation.Error(VideoReframeCodes.KeyframesUnsorted, $"Reframe {r}: keyframe times must be non-negative and strictly increasing.");
                previous = key.AtSec;
                if (reframe.ToSec is { } end && reframe.StartSec + key.AtSec > end + 1e-9)
                    return VideoReframeValidation.Error(VideoReframeCodes.KeyframeOutsideRange, $"Reframe {r}: keyframe {k} (at {key.AtSec}s) is past ToSec.");
                if (!double.IsFinite(key.Zoom) || key.Zoom < MinZoom || key.Zoom > MaxZoom)
                    return VideoReframeValidation.Error(VideoReframeCodes.ZoomOutOfRange, $"Reframe {r}: keyframe {k} zoom must be {MinZoom} to {MaxZoom}.");
                if (!double.IsFinite(key.CenterX) || !double.IsFinite(key.CenterY) ||
                    key.CenterX < 0 || key.CenterX > 1 || key.CenterY < 0 || key.CenterY > 1)
                    return VideoReframeValidation.Error(VideoReframeCodes.CenterOutOfRange, $"Reframe {r}: keyframe {k} centre must be 0..1 on both axes.");
                if (!Enum.IsDefined(key.Easing))
                    return VideoReframeValidation.Error(VideoReframeCodes.EasingUnknown, $"Reframe {r}: keyframe {k} easing is not a known easing.");

                if (reframe.Anchor == ReframeAnchor.Visible)
                {
                    // A visible centre is contained by the engine against the fit it knows (the cover crop);
                    // the config only bounds it to the frame.
                    keys.Add(key);
                    continue;
                }

                double half = 0.5 / key.Zoom;
                double cx = Math.Clamp(key.CenterX, half, 1 - half);
                double cy = Math.Clamp(key.CenterY, half, 1 - half);
                if (cx != key.CenterX)
                    clamps.Add(new ReframeClamp(r, k, "centerX", key.CenterX, cx));
                if (cy != key.CenterY)
                    clamps.Add(new ReframeClamp(r, k, "centerY", key.CenterY, cy));
                keys.Add(key with { CenterX = cx, CenterY = cy });
            }

            normalized.Add(reframe with { Keyframes = keys });
        }

        foreach (IGrouping<int, VideoReframe> bySource in normalized.GroupBy(r => r.SourceIndex))
        {
            List<VideoReframe> ordered = bySource.OrderBy(r => r.StartSec).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                double previousEnd = ordered[i - 1].ToSec ?? double.PositiveInfinity;
                if (ordered[i].StartSec < previousEnd - 1e-9)
                    return VideoReframeValidation.Error(VideoReframeCodes.Overlap, $"Two reframes of source {bySource.Key} overlap.");
            }
        }

        return VideoReframeValidation.Valid(normalized, clamps);
    }
}

/// <summary>A keyframe centre moved so its zoom window stays inside the frame.</summary>
public sealed record ReframeClamp(int Reframe, int Keyframe, string Axis, double From, double To);

/// <summary>The result of <see cref="VideoReframe.Validate"/>.</summary>
public sealed record VideoReframeValidation(
    string? ErrorCode,
    string? Message,
    IReadOnlyList<VideoReframe> Normalized,
    IReadOnlyList<ReframeClamp> Clamps)
{
    public bool IsValid => ErrorCode is null;

    internal static VideoReframeValidation Valid(IReadOnlyList<VideoReframe> normalized, IReadOnlyList<ReframeClamp> clamps) =>
        new(null, null, normalized, clamps);

    internal static VideoReframeValidation Error(string code, string message) => new(code, message, [], []);
}

/// <summary>
/// The config-error codes of keyframed reframing, per-source fits and the subject safe area.
/// Kept with the config they describe; every one is the customer's to fix.
/// </summary>
public static class VideoReframeCodes
{
    public const string RequiresReencode = "REFRAME_REQUIRES_REENCODE";
    public const string UnknownSource = "REFRAME_UNKNOWN_SOURCE";
    public const string RangeInvalid = "REFRAME_RANGE_INVALID";
    public const string NoKeyframes = "REFRAME_NO_KEYFRAMES";
    public const string TooManyKeyframes = "REFRAME_TOO_MANY_KEYFRAMES";
    public const string KeyframesUnsorted = "REFRAME_KEYFRAMES_UNSORTED";
    public const string KeyframeOutsideRange = "REFRAME_KEYFRAME_OUTSIDE_RANGE";
    public const string ZoomOutOfRange = "REFRAME_ZOOM_OUT_OF_RANGE";
    public const string CenterOutOfRange = "REFRAME_CENTER_OUT_OF_RANGE";
    public const string EasingUnknown = "REFRAME_EASING_UNKNOWN";
    public const string Overlap = "REFRAME_OVERLAP";
    public const string TooManyReframes = "REFRAME_TOO_MANY_REFRAMES";
    public const string AnchorUnknown = "REFRAME_ANCHOR_UNKNOWN";
    public const string SourceFitUnknownSource = "SOURCE_FIT_UNKNOWN_SOURCE";
    public const string SubjectSafeAreaOutOfRange = "SUBJECT_SAFE_AREA_OUT_OF_RANGE";

    /// <summary>Every code above, for the failure classifier.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RequiresReencode, UnknownSource, RangeInvalid, NoKeyframes, TooManyKeyframes, KeyframesUnsorted,
        KeyframeOutsideRange, ZoomOutOfRange, CenterOutOfRange, EasingUnknown, Overlap, TooManyReframes,
        SourceFitUnknownSource, SubjectSafeAreaOutOfRange, AnchorUnknown
    ];
}
