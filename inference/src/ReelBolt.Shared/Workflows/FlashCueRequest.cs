namespace ReelBolt.Shared.Workflows;

/// <summary>The tint of an event flash. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum FlashColour
{
    /// <summary>A neutral white flash.</summary>
    White,
    /// <summary>A warm, slightly amber flash.</summary>
    Warm,
    /// <summary>A cool, slightly blue flash.</summary>
    Cool
}

/// <summary>How bright an event flash gets. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum FlashStrength
{
    Subtle,
    Normal,
    Strong
}

/// <summary>How long an event flash lasts. <b>APPEND ONLY</b> - persisted by name.</summary>
public enum FlashLength
{
    Short,
    Medium,
    Long
}

/// <summary>
/// One "flash the picture on this event" entry of <see cref="VideoCompileStepConfig.FlashCues"/>: an event id
/// (<c>e{n}</c> of the analysis) plus words. Never a number: the compile resolves the event to its output second
/// and draws a short brightness and colour overlay on the program there (docs/video-editing.md "Flash and glow").
/// </summary>
public sealed record FlashCueRequest(
    string EventId,
    FlashColour Colour = FlashColour.White,
    FlashStrength Strength = FlashStrength.Normal,
    FlashLength Length = FlashLength.Short)
{
    /// <summary>The most flash cues one compile may carry.</summary>
    public const int MaxRequests = 16;

    /// <summary>The config-only checks: count, id shape, known words, Mode = Reencode. Null when fine.</summary>
    public static VideoReframeValidation? Check(VideoCompileStepConfig config)
    {
        if (config.FlashCues is not { Count: > 0 } cues)
            return null;
        if (cues.Count > MaxRequests)
            return VideoReframeValidation.Error(FlashCueCodes.TooMany, $"{cues.Count} flashCues entries; at most {MaxRequests}.");
        for (int i = 0; i < cues.Count; i++)
        {
            FlashCueRequest? c = cues[i];
            if (c is null || !EventMoveRequest.IsEventId(c.EventId))
                return VideoReframeValidation.Error(FlashCueCodes.EventIdInvalid, $"FlashCues {i}: EventId must look like e0 or e12 (an event id from the analysis).");
            if (!Enum.IsDefined(c.Colour) || !Enum.IsDefined(c.Strength) || !Enum.IsDefined(c.Length))
                return VideoReframeValidation.Error(FlashCueCodes.WordUnknown, $"FlashCues {i}: Colour, Strength or Length is not a known word.");
        }

        if (config.Mode == VideoCompileMode.StreamCopy)
            return VideoReframeValidation.Error(FlashCueCodes.RequiresReencode, "FlashCues require Mode=Reencode - a flash has no stream-copy equivalent.");
        return null;
    }
}

public static class FlashCueCodes
{
    public const string TooMany = "FLASH_CUES_TOO_MANY";
    public const string EventIdInvalid = "FLASH_CUES_EVENT_ID_INVALID";
    public const string WordUnknown = "FLASH_CUES_WORD_UNKNOWN";
    public const string RequiresReencode = "FLASH_CUES_REQUIRE_REENCODE";

    public static readonly IReadOnlyList<string> All = [TooMany, EventIdInvalid, WordUnknown, RequiresReencode];
}
