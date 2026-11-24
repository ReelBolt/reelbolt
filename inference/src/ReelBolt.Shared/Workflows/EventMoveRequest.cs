using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>Which camera move an event gets. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum EventMoveKind
{
    /// <summary>Snaps in on the event and eases back out.</summary>
    PunchIn,
    /// <summary>Pushes in slowly from the event and releases.</summary>
    PushIn,
    /// <summary>Starts close at the event and eases back out to the full frame.</summary>
    PullOut,
    /// <summary>Pans the framing to the left across the event.</summary>
    PanLeft,
    /// <summary>Pans the framing to the right across the event.</summary>
    PanRight,
    /// <summary>Pans the framing up across the event.</summary>
    PanUp,
    /// <summary>Pans the framing down across the event.</summary>
    PanDown,
    /// <summary>A short decaying handheld-style shake on the event.</summary>
    Shake
}

/// <summary>How strong an event move is. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum EventMoveIntensity
{
    Subtle,
    Normal,
    Strong
}

/// <summary>How long an event move lasts. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum EventMoveHold
{
    Short,
    Medium,
    Long
}

/// <summary>
/// One "move the camera on this event" entry of <see cref="VideoCompileStepConfig.EventMoves"/>: an event id
/// (<c>e{n}</c> of the analysis) plus words. Never a number: the compile resolves the event to its frame and
/// source second, picks the centre from the event's region, and expands the entry into ordinary
/// <see cref="VideoReframe"/>s (docs/video-editing.md "Moves on events").
/// </summary>
public sealed record EventMoveRequest(
    string EventId,
    EventMoveKind Move = EventMoveKind.PunchIn,
    EventMoveIntensity Intensity = EventMoveIntensity.Normal,
    EventMoveHold Hold = EventMoveHold.Medium,
    ReframeEasing Easing = ReframeEasing.EaseInOut)
{
    /// <summary>The most event moves one compile may carry.</summary>
    public const int MaxRequests = 16;

    private static readonly Regex EventIdPattern = new("^e(0|[1-9][0-9]{0,4})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsEventId(string? id) => id is not null && EventIdPattern.IsMatch(id);

    /// <summary>The config-only checks: count, id shape, known words, Mode = Reencode. Null when fine.</summary>
    public static VideoReframeValidation? Check(VideoCompileStepConfig config)
    {
        if (config.EventMoves is not { Count: > 0 } moves)
            return null;
        if (moves.Count > MaxRequests)
            return VideoReframeValidation.Error(EventMoveCodes.TooMany, $"{moves.Count} eventMoves entries; at most {MaxRequests}.");
        for (int i = 0; i < moves.Count; i++)
        {
            EventMoveRequest? m = moves[i];
            if (m is null || !IsEventId(m.EventId))
                return VideoReframeValidation.Error(EventMoveCodes.EventIdInvalid, $"EventMoves {i}: EventId must look like e0 or e12 (an event id from the analysis).");
            if (!Enum.IsDefined(m.Move) || !Enum.IsDefined(m.Intensity) || !Enum.IsDefined(m.Hold) || !Enum.IsDefined(m.Easing))
                return VideoReframeValidation.Error(EventMoveCodes.WordUnknown, $"EventMoves {i}: Move, Intensity, Hold or Easing is not a known word.");
        }

        if (config.Mode == VideoCompileMode.StreamCopy)
            return VideoReframeValidation.Error(EventMoveCodes.RequiresReencode, "EventMoves require Mode=Reencode - a zoom or pan has no stream-copy equivalent.");
        return null;
    }
}

public static class EventMoveCodes
{
    public const string TooMany = "EVENT_MOVES_TOO_MANY";
    public const string EventIdInvalid = "EVENT_MOVES_EVENT_ID_INVALID";
    public const string WordUnknown = "EVENT_MOVES_WORD_UNKNOWN";
    public const string RequiresReencode = "EVENT_MOVES_REQUIRE_REENCODE";

    public static readonly IReadOnlyList<string> All = [TooMany, EventIdInvalid, WordUnknown, RequiresReencode];
}
