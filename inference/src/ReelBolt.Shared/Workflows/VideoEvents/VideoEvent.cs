using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// Closed vocabulary of the events the event detectors can anchor to a source frame. Persisted by
/// name, so the set is append-only: never rename or reorder a member, only add to the end.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoEventType
{
    Appear,
    Disappear,
    MoveStart,
    MoveStop,
    Hit,
    TextIn,
    TextOut,
    CameraMoveStart,
    CameraMoveStop,
    Transition,
    Change,
    Beat,
    SpeechStart,
    SpeechEnd,
    /// <summary>A short brightness spike that falls back (a camera flash, a strobe): the picture was lit for a few frames.</summary>
    Flash,
    /// <summary>One word of a burned-in caption popping on or off: a rhythm of the edit, not a moment a sound belongs on.</summary>
    CaptionTick
}

/// <summary>Where an event came from: measured in pixels, measured in audio, declared by the source, or proposed by a model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoEventOrigin
{
    Pixels,
    Audio,
    Declared,
    Model
}

/// <summary>A normalised region of the frame: x, y, width and height, each in 0..1 (origin top left).</summary>
public sealed record VideoEventRegion(double X, double Y, double W, double H);

/// <summary>
/// One event anchored to a frame of the source grid. <see cref="Frame"/> is the first frame the event is true
/// (for a fade or a wipe, the half-way frame); <see cref="EndFrame"/> is the last frame of a spread-out event.
/// <see cref="Direction"/> (left, right, up, down, up-left ... or <c>grow</c> for something scaling in) and <see cref="Speed"/>
/// (slow, medium, fast) are set on moves and pop-ins. On a camera move start the direction is the way the camera goes:
/// one of the same words, or <c>in</c> / <c>out</c> for a zoom; null when it went no clear way.
/// </summary>
public sealed record VideoEvent(
    VideoEventType Type,
    int Frame,
    int? EndFrame,
    VideoEventRegion? Region,
    double Strength,
    VideoEventOrigin Origin,
    double Confidence,
    string? Direction = null,
    string? Speed = null,
    /// <summary>What kind of picture the event happens in (<see cref="VideoEventKinds"/>), when the pixels made it clear; else null.</summary>
    string? Kind = null);

/// <summary>
/// The events found in one source, ordered by frame. <see cref="Suppressed"/> counts the events dropped by the
/// <c>MaxEvents</c> cap (weakest first), so a capped set is never mistaken for a complete one.
/// </summary>
public sealed record VideoEventSet(
    double Fps,
    int FrameCount,
    int Width,
    int Height,
    IReadOnlyList<VideoEvent> Events,
    int Suppressed = 0)
{
    /// <summary>
    /// True when a part of the detection failed and was left out (the speech edges, the beats, or the check for
    /// a variable frame rate), so another run on the same file may find more. It is a fact of this one run:
    /// the set itself never stores it, and a set that carries it is never kept in a cache. A step that used such a
    /// set says so in its output (<c>meta.events.degraded</c>), so the step cache does not keep that result either.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Degraded { get; init; }
}
