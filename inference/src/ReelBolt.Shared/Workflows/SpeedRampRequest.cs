using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>Which speed ramp an event gets. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum SpeedRampKind
{
    /// <summary>Plays slower from the event for the span.</summary>
    SlowMo,
    /// <summary>Plays faster from the event for the span.</summary>
    SpeedUp,
    /// <summary>Holds one frame on the event.</summary>
    FreezeHit,
    /// <summary>Slows down progressively, arriving at full slow motion on the event.</summary>
    RampIn,
    /// <summary>Starts in slow motion on the event and speeds back up to normal.</summary>
    RampOut
}

/// <summary>How strong a speed ramp is. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum SpeedRampStrength
{
    Subtle,
    Normal,
    Strong
}

/// <summary>How long a speed ramp lasts (the hold of a freeze). <b>APPEND ONLY</b> - persisted by name.</summary>
public enum SpeedRampSpan
{
    Short,
    Medium,
    Long
}

/// <summary>
/// One "change the speed on this event" entry of <see cref="VideoCompileStepConfig.SpeedRamps"/>: an event id (<c>e{n}</c> of
/// the analysis) plus words. Never a number: the compile resolves the event to its place on the finished program and the word
/// tables in <c>SpeedRampPlanner</c> pick the rates and lengths (docs/video-editing.md "Speed ramps").
/// </summary>
public sealed record SpeedRampRequest(
    string EventId,
    SpeedRampKind Ramp = SpeedRampKind.SlowMo,
    SpeedRampStrength Strength = SpeedRampStrength.Normal,
    SpeedRampSpan Span = SpeedRampSpan.Medium)
{
    /// <summary>The most speed ramps one compile may carry.</summary>
    public const int MaxRequests = 8;

    private static readonly Regex EventIdPattern = new("^e(0|[1-9][0-9]{0,4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsEventId(string? id) => id is not null && EventIdPattern.IsMatch(id);

    /// <summary>The config-only checks: count, id shape, known words, Mode = Reencode. Null when fine.</summary>
    public static VideoReframeValidation? Check(VideoCompileStepConfig config)
    {
        if (config.SpeedRamps is not { Count: > 0 } ramps)
            return null;
        if (ramps.Count > MaxRequests)
            return VideoReframeValidation.Error(SpeedRampCodes.TooMany, $"{ramps.Count} speedRamps entries; at most {MaxRequests}.");
        for (int i = 0; i < ramps.Count; i++)
        {
            SpeedRampRequest? r = ramps[i];
            if (r is null || !IsEventId(r.EventId))
                return VideoReframeValidation.Error(SpeedRampCodes.EventIdInvalid, $"SpeedRamps {i}: EventId must look like e0 or e12 (an event id from the analysis).");
            if (!Enum.IsDefined(r.Ramp) || !Enum.IsDefined(r.Strength) || !Enum.IsDefined(r.Span))
                return VideoReframeValidation.Error(SpeedRampCodes.WordUnknown, $"SpeedRamps {i}: Ramp, Strength or Span is not a known word.");
        }

        if (config.Mode == VideoCompileMode.StreamCopy)
            return VideoReframeValidation.Error(SpeedRampCodes.RequiresReencode, "SpeedRamps require Mode=Reencode - a speed change has no stream-copy equivalent.");
        return null;
    }
}

public static class SpeedRampCodes
{
    public const string TooMany = "SPEED_RAMPS_TOO_MANY";
    public const string EventIdInvalid = "SPEED_RAMPS_EVENT_ID_INVALID";
    public const string WordUnknown = "SPEED_RAMPS_WORD_UNKNOWN";
    public const string RequiresReencode = "SPEED_RAMPS_REQUIRE_REENCODE";

    public static readonly IReadOnlyList<string> All = [TooMany, EventIdInvalid, WordUnknown, RequiresReencode];
}
