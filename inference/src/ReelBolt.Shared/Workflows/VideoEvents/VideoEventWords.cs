using System.Text;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// The words an agent-facing view uses for an event instead of a number: the type word, the strength word and the
/// region word. Shared by the analysis view (<c>view.events</c>), the outline and the read tools, so one event reads
/// the same everywhere.
/// </summary>
public static class VideoEventWords
{
    /// <summary>The snake_case word of a type: <c>MoveStart</c> becomes <c>move_start</c>.</summary>
    public static string TypeWord(VideoEventType type)
    {
        string name = type.ToString();
        StringBuilder sb = new(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }

    /// <summary>The type of a snake_case or PascalCase word, or null when it is not in the closed vocabulary.</summary>
    public static VideoEventType? ParseType(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return null;
        string trimmed = word.Trim();
        foreach (VideoEventType type in Enum.GetValues<VideoEventType>())
            if (string.Equals(TypeWord(type), trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                return type;
        return null;
    }

    /// <summary><c>weak</c> below 0.35, <c>strong</c> from 0.7, else <c>clear</c>.</summary>
    public static string StrengthWord(double strength) => strength switch
    {
        < 0.35 => "weak",
        >= 0.7 => "strong",
        _ => "clear"
    };

    /// <summary>The cell of a 3x3 grid the region's centre falls in: <c>top-left</c> .. <c>bottom-right</c>, <c>center</c> in the middle.</summary>
    public static string RegionWord(VideoEventRegion region)
    {
        double cx = region.X + region.W / 2, cy = region.Y + region.H / 2;
        string col = cx < 1.0 / 3 ? "left" : cx >= 2.0 / 3 ? "right" : "";
        string row = cy < 1.0 / 3 ? "top" : cy >= 2.0 / 3 ? "bottom" : "";
        return (row, col) switch
        {
            ("", "") => "center",
            ("", _) => col,
            (_, "") => row,
            _ => row + "-" + col
        };
    }

    /// <summary><c>small</c> under 3% of the frame, <c>large</c> from 20%, else <c>medium</c>; null when the event has no region.</summary>
    public static string? SizeWord(VideoEventRegion? region)
    {
        if (region is null)
            return null;
        double area = Math.Max(0, region.W) * Math.Max(0, region.H);
        return area < 0.03 ? "small" : area >= 0.2 ? "large" : "medium";
    }

    /// <summary>
    /// The word for a velocity (y grows downward): <c>left</c>, <c>right</c>, <c>up</c>, <c>down</c>, or a diagonal
    /// such as <c>up-left</c> when neither axis is more than twice the other. Only the ratio of the two parts counts,
    /// so any unit will do. Every producer of a direction word uses this one table.
    /// </summary>
    public static string DirectionWord(double vx, double vy)
    {
        double ax = Math.Abs(vx), ay = Math.Abs(vy);
        string horizontal = vx > 0 ? "right" : "left", vertical = vy > 0 ? "down" : "up";
        if (ax > 2 * ay)
            return horizontal;
        if (ay > 2 * ax)
            return vertical;
        return vertical + "-" + horizontal;
    }

    /// <summary>The word for a speed in screens per second: <c>slow</c> under half a screen, <c>fast</c> from two, else <c>medium</c>.</summary>
    public static string SpeedWord(double screensPerSecond) =>
        screensPerSecond < 0.5 ? "slow" : screensPerSecond >= 2.0 ? "fast" : "medium";

    /// <summary>The direction word of a camera that pushes in (a zoom toward the picture); only a camera move carries it.</summary>
    public const string CameraIn = "in";

    /// <summary>The direction word of a camera that pulls out.</summary>
    public const string CameraOut = "out";

    /// <summary>
    /// What the camera does when it goes in <paramref name="direction"/>: it pans left or right, tilts up or down,
    /// moves on a diagonal, pushes in or pulls out. Null for any other word.
    /// </summary>
    public static string? CameraPhrase(string? direction) => direction switch
    {
        CameraIn => "the camera pushes in",
        CameraOut => "the camera pulls out",
        "left" or "right" => "the camera pans " + direction,
        "up" or "down" => "the camera tilts " + direction,
        _ when IsDirectionWord(direction) => "the camera moves " + Toward(direction!),
        _ => null
    };

    /// <summary>True for a word <see cref="DirectionWord"/> can return.</summary>
    public static bool IsDirectionWord(string? word) =>
        word is "left" or "right" or "up" or "down" or "up-left" or "up-right" or "down-left" or "down-right";

    /// <summary>The side something comes from when it travels in <paramref name="direction"/>: moving right, it comes from the left.</summary>
    private static string? Opposite(string? direction) => direction switch
    {
        "left" => "the right",
        "right" => "the left",
        "up" => "below",
        "down" => "above",
        "up-left" => "the bottom right",
        "up-right" => "the bottom left",
        "down-left" => "the top right",
        "down-right" => "the top left",
        _ => null
    };

    private static string Toward(string direction) => direction switch
    {
        "up" => "upward",
        "down" => "downward",
        "up-left" => "up and to the left",
        "up-right" => "up and to the right",
        "down-left" => "down and to the left",
        "down-right" => "down and to the right",
        _ => "to the " + direction
    };

    /// <summary>
    /// What happens at an event, in plain words built only from what the detector measured: the kind of change, how big,
    /// which way and how fast. A camera move whose direction was measured says what the camera does ("the camera pans
    /// left", "the camera pushes in"). Never a number, a time or a name. Null for a type that needs no words (a beat, a speech edge).
    /// </summary>
    public static string? What(VideoAnalysisRenderEvent e) =>
        ParseType(e.Type) is { } type ? What(type, e.Frame, e.EndFrame, e.Region, e.Direction, e.SpeedWord) : null;

    /// <summary>
    /// The same words from plain fields, so an event of the analysis artifact and an event of a per-file result read
    /// alike: the type, the frame and the last frame (null when it is not spread out), the region (null when it has
    /// none) and the direction and speed words. This is the one implementation.
    /// </summary>
    public static string? What(VideoEventType type, int frame, int? endFrame, VideoEventRegion? region, string? direction, string? speed)
    {
        string? size = SizeWord(region);
        string thing = size is null ? "something" : $"a {size} element";
        bool spread = endFrame is { } end && end - frame > 3;
        string? way = IsDirectionWord(direction) ? direction : null;
        string pace = speed is "slow" or "fast" ? ", " + speed : "";
        switch (type)
        {
            case VideoEventType.Transition:
                if (endFrame is null)
                    return region is null ? "hard cut to a new picture" : "hard cut inside a panel of the frame";
                return way is null ? "the picture changes over to the next, softly" : $"the picture slides over {Toward(way)}{pace}";
            case VideoEventType.Appear:
                if (direction == "grow")
                    return $"{thing} grows in";
                if (way is not null && Opposite(way) is { } from)
                    return $"{thing} slides in from {from}{pace}";
                return spread ? $"{thing} fades in" : $"{thing} pops in";
            case VideoEventType.Disappear:
                if (way is not null)
                    return $"{thing} slides out {Toward(way)}{pace}";
                return spread ? $"{thing} fades away" : $"{thing} vanishes";
            case VideoEventType.MoveStart:
                return way is null ? $"{thing} starts to move" : $"{thing} moves {Toward(way)}{pace}";
            case VideoEventType.MoveStop:
                return $"{thing} stops moving";
            case VideoEventType.Change:
                return direction == "grow" ? $"{thing} spreads over what was there" : $"{thing} changes in place";
            case VideoEventType.TextIn:
                return "a line of text appears";
            case VideoEventType.TextOut:
                return "a line of text goes away";
            case VideoEventType.Hit:
                return "something lands or stops dead";
            case VideoEventType.Flash:
                return region is null ? "the whole picture flashes bright" : "a short flash of light, like a camera flash";
            case VideoEventType.CameraMoveStart:
                {
                    bool longMove = endFrame is { } stop && stop - frame > 150;
                    if (CameraPhrase(direction) is { } camera)
                        return longMove ? camera + ", long and slow" : camera;
                    return longMove ? "the whole picture starts a long, slow move" : "the whole picture starts to move";
                }
            case VideoEventType.CameraMoveStop:
                return "the picture comes to rest";
            case VideoEventType.CaptionTick:
                return "a caption word changes (not a moment for a sound)";
            default:
                return null;
        }
    }

    /// <summary>A rough token count of a text: one token per four characters, the ratio the assistant's turn guard uses.</summary>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;
}
