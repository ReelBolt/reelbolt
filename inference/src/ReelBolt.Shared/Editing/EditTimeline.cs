using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// Rational number representation (numerator/denominator).
/// </summary>
public record Rational(int Num, int Den);

/// <summary>
/// Settings for an editing timeline including dimensions, frame rate, and aspect handling.
/// </summary>
public record EditTimelineSettings(
    int Width,
    int Height,
    Rational Rate,
    int SampleRate,
    Rational PixelAspect,
    AspectPolicy AspectPolicy,
    long StartTimecodeFrames
);

/// <summary>
/// A track in an editing timeline containing clips and other items.
/// </summary>
public record Track(
    Guid Id,
    TrackKind Kind,
    string Name,
    string? Color,
    double Height,
    bool Locked,
    bool Enabled,
    bool Mute,
    bool Solo,
    Guid? BusId,
    IReadOnlyList<Item> Items,
    Keyframed<double>? GainDb = null,
    double Pan = 0,
    TrackEq? Eq = null,
    AudioDynamics? Dynamics = null,
    AudioDenoise? Denoise = null,
    AudioLeveler? Leveler = null
)
{
    /// <summary>
    /// P5: the track fader. <c>Value</c> is the static level in dB, <c>Keys</c> an automation
    /// envelope over timeline frames (the ducking derivation writes here). <c>null</c> is unity.
    /// </summary>
    public Keyframed<double>? GainDb { get; init; } = GainDb;

    /// <summary>P5: stereo balance, -1 (hard left) .. 1 (hard right); 0 is centre and the default.</summary>
    public double Pan { get; init; } = Pan;

    /// <summary>P5: the track's equaliser. <c>null</c> is flat.</summary>
    public TrackEq? Eq { get; init; } = Eq;

    /// <summary>P8: the track's dynamics (compressor/limiter/gate/de-esser). <c>null</c> is none.</summary>
    public AudioDynamics? Dynamics { get; init; } = Dynamics;

    /// <summary>P8: the track's denoiser. <c>null</c> is none.</summary>
    public AudioDenoise? Denoise { get; init; } = Denoise;

    /// <summary>P8: the track's leveler. <c>null</c> is none.</summary>
    public AudioLeveler? Leveler { get; init; } = Leveler;
}

/// <summary>
/// A track equaliser (P5): an optional low cut, an optional high cut and up to
/// <see cref="EditTimelineValidator.MaxEqBands"/> parametric bands.
/// </summary>
public sealed record TrackEq(double? LowCutHz = null, double? HighCutHz = null, IReadOnlyList<EqBand>? Bands = null)
{
    public double? LowCutHz { get; } = LowCutHz;

    public double? HighCutHz { get; } = HighCutHz;

    public IReadOnlyList<EqBand>? Bands { get; } = Bands;
}

/// <summary>One parametric EQ band (P5): centre frequency in Hz, gain in dB and Q.</summary>
public sealed record EqBand(double FrequencyHz, double GainDb, double Q = 1.0)
{
    public double FrequencyHz { get; } = FrequencyHz;

    public double GainDb { get; } = GainDb;

    public double Q { get; } = Q;
}

/// <summary>
/// A marker point in an editing timeline.
/// </summary>
public record Marker(
    Guid Id,
    long Frame,
    string Name,
    string? Color,
    string? Note,
    long? DurationFrames
);

/// <summary>
/// A range with in and out points.
/// </summary>
public record EditRange(
    long InFrame,
    long OutFrame
);

/// <summary>
/// Seed data for timeline generation.
/// </summary>
public record EditTimelineSeed(
    Guid ExecutionId,
    int Version
);

/// <summary>
/// Complete video editing timeline with tracks, markers, items, and metadata.
/// SchemaVersion is fixed at 2.
/// </summary>
public record EditTimeline(
    int SchemaVersion,
    Guid Id,
    Guid ProjectId,
    string Name,
    EditTimelineSettings Settings,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Marker> Markers,
    EditRange? InOut = null,
    EditTimelineSeed? Seed = null,
    IReadOnlyList<ColorGroup>? ColorGroups = null,
    IReadOnlyList<EditSequence>? Sequences = null,
    IReadOnlyList<AudioBus>? Buses = null
)
{
    /// <summary>
    /// Creates a new EditTimeline with SchemaVersion always set to 2.
    /// </summary>
    [JsonConstructor]
    public EditTimeline(
        Guid id,
        Guid projectId,
        string name,
        EditTimelineSettings settings,
        IReadOnlyList<Track> tracks,
        IReadOnlyList<Marker> markers,
        EditRange? inOut = null,
        EditTimelineSeed? seed = null,
        IReadOnlyList<ColorGroup>? colorGroups = null,
        IReadOnlyList<EditSequence>? sequences = null,
        IReadOnlyList<AudioBus>? buses = null)
        : this(2, id, projectId, name, settings, tracks, markers, inOut, seed, colorGroups, sequences, buses)
    {
    }

    /// <summary>P7: clips graded together. <c>null</c>/empty is no groups; a clip is in at most one group.</summary>
    public IReadOnlyList<ColorGroup>? ColorGroups { get; } = ColorGroups;

    /// <summary>P8: the nested timelines compound clips play. <c>null</c>/empty is none.</summary>
    public IReadOnlyList<EditSequence>? Sequences { get; } = Sequences;

    /// <summary>P8: the submix buses tracks route to. <c>null</c>/empty is none (every track feeds the master).</summary>
    public IReadOnlyList<AudioBus>? Buses { get; } = Buses;
}
