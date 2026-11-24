using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// A value that can optionally be keyframed for animation.
/// </summary>
/// <typeparam name="T">The type of value being keyframed.</typeparam>
public record Keyframed<T>(T? Value, IReadOnlyList<Keyframe<T>>? Keys = null)
{
    /// <summary>
    /// Base/default value when not keyframed or before the first keyframe.
    /// </summary>
    public T? Value { get; } = Value;

    /// <summary>
    /// Optional keyframes for animation over time.
    /// </summary>
    public IReadOnlyList<Keyframe<T>>? Keys { get; } = Keys;
}

/// <summary>
/// A single keyframe point in an animation sequence.
/// </summary>
/// <typeparam name="T">The type of value at this keyframe.</typeparam>
public record Keyframe<T>(long Frame, T Value, InterpKind Interp)
{
    /// <summary>
    /// Frame position of this keyframe.
    /// </summary>
    public long Frame { get; } = Frame;

    /// <summary>
    /// Value at this keyframe.
    /// </summary>
    public T Value { get; } = Value;

    /// <summary>
    /// Interpolation method to the next keyframe.
    /// </summary>
    public InterpKind Interp { get; } = Interp;
}
