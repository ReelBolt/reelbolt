using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The frame-sampling visual check a <c>ReviewLoop</c> step can run before its reviewer
/// (<see cref="ReviewLoopStepConfig.VisualCheck"/>; docs/video-editing.md "Visual check in review loops").
///
/// <para>
/// When <see cref="Enabled"/>, every iteration samples <see cref="Frames"/> evenly spaced frames plus the
/// last frame of the output the loop reviews (the latest render or compile in the loop's window), sends
/// them in one Vision call with a fixed checklist (text overflow, cropped text, text outside the safe
/// margin, text too small, broken layout) and appends the findings to the reviewer's input and to the
/// revision brief. Any <c>high</c> finding caps the iteration score at <see cref="FailScoreCap"/>.
/// Off (the default) or absent is byte-identical to a review loop without the check.
/// </para>
///
/// <para>
/// Positional members are the raw, client-supplied values and round-trip unchanged; the executor reads
/// only the <c>Clamped*</c> values, which are computed and kept out of JSON and the step schema.
/// </para>
/// </summary>
/// <param name="Enabled">Run the check before the reviewer on every iteration. Default false.</param>
/// <param name="Frames">Evenly spaced frames to sample (2 to 16); the last frame is always added on top.</param>
/// <param name="SafeMarginPercent">The safe margin, in percent of each frame edge (0 to 20), text must stay inside.</param>
/// <param name="MinTextHeightPercent">The smallest acceptable text cap height, in percent of the frame height.</param>
/// <param name="FailScoreCap">The highest score an iteration with a high-severity finding can get (0 to 10).</param>
/// <param name="VisionProviderId">The Vision provider to ask, or null for the default Vision provider.</param>
/// <param name="TimelineFacts">Append a bounded <c>[TIMELINE FACTS]</c> block (what the compile actually rendered: spans,
/// overlays, sound effects, music, transitions, loudness) to the reviewer's input. First-party facts, no model call.
/// Independent of <see cref="Enabled"/>. Default false.</param>
/// <param name="AudioFacts">Measure the film's sound with the engine's ffmpeg (loudness, true peak, loudness range,
/// loudness around each sound effect and cut, silences, clipping) and append an <c>[AUDIO FACTS]</c> block. No audio
/// leaves the host. Independent of <see cref="Enabled"/>. Default false.</param>
/// <param name="TargetedLooks">Let the reviewer ask for more frames at times it names (optionally a region, larger),
/// answered by the vision model and fed back to the reviewer, at most <see cref="MaxExtraRounds"/> rounds of at most
/// <see cref="MaxFramesPerRound"/> frames each. Each round is metered and must fit the step's credit hold, else it is
/// skipped with a recorded reason. Default false.</param>
/// <param name="MaxExtraRounds">Most targeted-look rounds per review iteration (0 to 3).</param>
/// <param name="MaxFramesPerRound">Most frames one targeted-look round extracts (1 to 8).</param>
/// <param name="QualityChecks">
/// Run the engine's own render quality checks on the finished film (frozen frames, loudness against the
/// compile's target, burned-in text contrast): free, nothing leaves the host, independent of
/// <see cref="Enabled"/>. Advisory in the reviewer's input; only a high-severity finding caps the score at
/// <see cref="ClampedFailScoreCap"/>. See docs/video-editing.md "Render quality checks".
/// </param>
public sealed record VisualCheckConfig(
    bool Enabled = false,
    int Frames = 8,
    double SafeMarginPercent = 5,
    double MinTextHeightPercent = 2.5,
    int FailScoreCap = 6,
    Guid? VisionProviderId = null,
    bool TimelineFacts = false,
    bool AudioFacts = false,
    bool TargetedLooks = false,
    int MaxExtraRounds = 2,
    int MaxFramesPerRound = 6,
    bool QualityChecks = false)
{
    /// <summary>The most targeted-look rounds one iteration may run.</summary>
    public const int MaxExtraRoundsCeiling = 3;

    /// <summary>The most frames one targeted-look round may extract.</summary>
    public const int MaxFramesPerRoundCeiling = 8;

    /// <summary><see cref="MaxExtraRounds"/> clamped to 0..<see cref="MaxExtraRoundsCeiling"/>; 0 when looks are off.</summary>
    [JsonIgnore]
    public int ClampedExtraRounds => TargetedLooks ? Math.Clamp(MaxExtraRounds, 0, MaxExtraRoundsCeiling) : 0;

    /// <summary><see cref="MaxFramesPerRound"/> clamped to 1..<see cref="MaxFramesPerRoundCeiling"/>.</summary>
    [JsonIgnore]
    public int ClampedFramesPerRound => Math.Clamp(MaxFramesPerRound, 1, MaxFramesPerRoundCeiling);

    /// <summary>The fewest evenly spaced frames a check samples.</summary>
    public const int MinFrames = 2;

    /// <summary>The most evenly spaced frames a check samples (the last frame is added on top).</summary>
    public const int MaxFrames = 16;

    /// <summary>The widest safe margin, in percent of a frame edge.</summary>
    public const double MaxSafeMarginPercent = 20;

    /// <summary>The smallest minimum text height the check will state, in percent of the frame height.</summary>
    public const double MinTextHeightPercentFloor = 0.5;

    /// <summary>The largest minimum text height the check will state, in percent of the frame height.</summary>
    public const double MinTextHeightPercentCeiling = 20;

    /// <summary><see cref="Frames"/> clamped to <see cref="MinFrames"/>..<see cref="MaxFrames"/>.</summary>
    [JsonIgnore]
    public int ClampedFrames => Math.Clamp(Frames, MinFrames, MaxFrames);

    /// <summary><see cref="SafeMarginPercent"/> clamped to 0..<see cref="MaxSafeMarginPercent"/> (NaN is 0).</summary>
    [JsonIgnore]
    public double ClampedSafeMarginPercent =>
        double.IsNaN(SafeMarginPercent) ? 0 : Math.Clamp(SafeMarginPercent, 0, MaxSafeMarginPercent);

    /// <summary><see cref="MinTextHeightPercent"/> clamped to the floor..ceiling (NaN is the floor).</summary>
    [JsonIgnore]
    public double ClampedMinTextHeightPercent =>
        double.IsNaN(MinTextHeightPercent)
            ? MinTextHeightPercentFloor
            : Math.Clamp(MinTextHeightPercent, MinTextHeightPercentFloor, MinTextHeightPercentCeiling);

    /// <summary><see cref="FailScoreCap"/> clamped to the 0..10 review score range.</summary>
    [JsonIgnore]
    public int ClampedFailScoreCap => Math.Clamp(FailScoreCap, 0, 10);
}
