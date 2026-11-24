namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// What kind of picture an event happens in: a closed list of three words. <c>graphic</c> is a designed element or page
/// (a card, a shape, an icon, an animation), <c>text</c> is words coming or changing, <c>footage</c> is ordinary filmed
/// material moving (a person walking, a hand, a plain cut between two camera shots). Set by code where the pixels are
/// clear, and by the picture model when it was asked; null when nobody could tell. Anything outside the list is null.
/// </summary>
public static class VideoEventKinds
{
    public const string Graphic = "graphic";
    public const string Footage = "footage";
    public const string Text = "text";

    /// <summary>The canonical word for <paramref name="raw"/> (trimmed, any letter case), or null when it is not one of the three.</summary>
    public static string? Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        Graphic => Graphic,
        Footage => Footage,
        Text => Text,
        _ => null
    };
}

/// <summary>How many looks at the picture a step takes when its author left the number open.</summary>
public static class VisionLookDefaults
{
    /// <summary>Fewest looks an automatic count gives.</summary>
    public const int MinEventLooks = 12;

    /// <summary>Most looks an automatic count gives (a video of three minutes or more).</summary>
    public const int MaxAutoEventLooks = 30;

    /// <summary>The automatic count of a video of about two minutes: what an estimate shows before the length is known.</summary>
    public const int TypicalEventLooks = 20;

    /// <summary>Most looks one step may ever take, whatever is asked.</summary>
    public const int MaxEventLooks = 40;

    /// <summary>One look per this many seconds of video.</summary>
    public const double SecondsPerEventLook = 6.0;

    /// <summary>Seconds of budget each look adds to the step's vision time.</summary>
    public const int SecondsPerLookBudget = 10;

    /// <summary>
    /// The automatic number of event looks for <paramref name="totalDurationSec"/> seconds of video: one per
    /// <see cref="SecondsPerEventLook"/> seconds, never fewer than <see cref="MinEventLooks"/> nor more than
    /// <see cref="MaxAutoEventLooks"/>. The range is kept narrow on purpose (2.5 times), so the price shown before a run
    /// stays inside the estimate's band whatever the length. An unknown or zero length gives the minimum.
    /// </summary>
    public static int EventLooks(double totalDurationSec) =>
        double.IsFinite(totalDurationSec) && totalDurationSec > 0
            ? (int)Math.Clamp(Math.Ceiling(totalDurationSec / SecondsPerEventLook), MinEventLooks, MaxAutoEventLooks)
            : MinEventLooks;

    /// <summary>
    /// The count a step uses: the author's number, at most <see cref="MaxEventLooks"/>, or the automatic one when none
    /// was given. 0 (or less) is the author switching the looks off: it stays 0 and is never raised to one.
    /// </summary>
    public static int EventLooks(int? asked, double totalDurationSec) =>
        asked is { } n ? (n <= 0 ? 0 : Math.Min(n, MaxEventLooks)) : EventLooks(totalDurationSec);
}
