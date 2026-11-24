namespace ReelBolt.Shared.Workflows;

/// <summary>A point as fractions (0..1) of the upright source picture.</summary>
public sealed record ObjectSeedPoint(double X, double Y);

/// <summary>A box as fractions (0..1) of the upright source picture: top-left corner plus size.</summary>
public sealed record ObjectSeedBox(double X, double Y, double W, double H);

/// <summary>
/// What a person did to name an object instead of describing it: a tap (<see cref="Point"/>, always present), an optional drawn
/// <see cref="Box"/>, and the moment of the SOURCE it was made at (<see cref="AtSec"/>, never a timeline second).
/// Wire shape: <c>{ "atSec": 12.4, "point": { "x": 0.42, "y": 0.61 }, "box": { "x": 0.33, "y": 0.48, "w": 0.18, "h": 0.26 } }</c>
/// (<c>box</c> optional). Carries no content: three numbers and a time.
/// </summary>
public sealed record ObjectSeed(double AtSec, ObjectSeedPoint? Point, ObjectSeedBox? Box = null)
{
    /// <summary>The stable code a refused seed answers with.</summary>
    public const string InvalidCode = "OBJECT_SEED_INVALID";

    /// <summary>A drawn box must be at least this fraction of the picture wide and tall (the follow route's rule).</summary>
    public const double MinBoxFraction = 0.01;

    /// <summary>
    /// Why this seed cannot be used, or null when it can. Fractions must be finite and inside 0..1, a box at least
    /// <see cref="MinBoxFraction"/> wide and tall and inside the picture, and the time inside the video when its length is known.
    /// </summary>
    public static string? Validate(ObjectSeed? seed, double? durationSec)
    {
        if (seed is null) return "Send a seed with the time, a point and, optionally, a box.";
        if (!double.IsFinite(seed.AtSec) || seed.AtSec < 0)
            return "atSec must be a time in seconds, zero or more.";
        if (durationSec is { } duration && duration > 0 && seed.AtSec > duration)
            return "atSec must be inside the video.";
        if (seed.Point is not { } point)
            return "point is required: tap the object.";
        if (!InUnit(point.X) || !InUnit(point.Y))
            return "point x and y must be between 0 and 1.";
        if (seed.Box is { } box)
        {
            if (!InUnit(box.X) || !InUnit(box.Y) || !double.IsFinite(box.W) || !double.IsFinite(box.H))
                return "box x, y, w and h must be numbers, with x and y between 0 and 1.";
            if (box.W < MinBoxFraction || box.H < MinBoxFraction)
                return "box must be at least 1 percent of the picture wide and tall.";
            if (box.X + box.W > 1.0000001 || box.Y + box.H > 1.0000001)
                return "box must lie inside the picture.";
        }

        return null;
    }

    private static bool InUnit(double v) => double.IsFinite(v) && v >= 0 && v <= 1;

    /// <summary>The box as the normalised quad (TL, TR, BL, BR) every <see cref="ObjectTrackTarget.Region"/> uses.</summary>
    public VideoInsertSurfaceQuad? BoxAsQuad() => Box is { } b
        ? new VideoInsertSurfaceQuad(b.X, b.Y, b.X + b.W, b.Y, b.X, b.Y + b.H, b.X + b.W, b.Y + b.H)
        : null;
}
