using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// P8: a nested timeline a <see cref="CompoundClip"/> plays. A sequence has its own tracks (the same
/// <see cref="Track"/> shape as the root, items in sequence-local frames starting at 0) and lives in
/// <see cref="EditTimeline.Sequences"/>; several compound clips may play the same sequence. A sequence
/// may itself contain compound clips, to a nesting depth of <see cref="EditTimelineValidator.MaxNestingDepth"/>,
/// and never (directly or through another sequence) itself.
/// </summary>
public record EditSequence(Guid Id, string Name, IReadOnlyList<Track> Tracks);

/// <summary>
/// P8: one camera angle of a <see cref="MulticamClip"/>. <see cref="OffsetFrames"/> is the angle's
/// source frame at multicam-source frame 0 — the audio-sync result — so the angle reads
/// <c>offsetFrames + sourceIn + (frame − recordIn)</c> at timeline <c>frame</c>.
/// <see cref="InputProfile"/> is the camera log profile the angle's source was shot in — one of
/// <c>CameraProfiles.Ids</c>, or <c>null</c> for display space — carried onto every clip the
/// angle is flattened into.
/// </summary>
public record MulticamAngle(
    Guid Id,
    string Name,
    AssetRef AssetRef,
    long OffsetFrames = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputProfile = null);

/// <summary>P8: an angle switch at a record frame inside the multicam clip's span; from here on <see cref="AngleId"/> plays.</summary>
public record MulticamCut(long AtFrame, Guid AngleId);

/// <summary>P8: a compressor — threshold in dBFS, ratio, attack/release in ms, make-up gain in dB.</summary>
public record AudioCompressor(double ThresholdDb = -18, double Ratio = 3, double AttackMs = 20, double ReleaseMs = 250, double MakeupDb = 0);

/// <summary>P8: a brick-wall limiter at <see cref="CeilingDb"/> dBFS.</summary>
public record AudioLimiter(double CeilingDb = -1);

/// <summary>P8: a noise gate — threshold in dBFS, ratio, attack/release in ms.</summary>
public record AudioGate(double ThresholdDb = -40, double Ratio = 2, double AttackMs = 20, double ReleaseMs = 250);

/// <summary>P8: a de-esser at an intensity in [0, 1].</summary>
public record AudioDeesser(double Intensity = 0.5);

/// <summary>P8: the dynamics section of a track or bus; every stage is optional and absent by default.</summary>
public record AudioDynamics(AudioCompressor? Compressor = null, AudioLimiter? Limiter = null, AudioGate? Gate = null, AudioDeesser? Deesser = null);

/// <summary>P8: a denoiser. <see cref="Kind"/> is <c>fft</c> (the only kind this wave ships); <see cref="Strength"/> in [0, 1].</summary>
public record AudioDenoise(string Kind = "fft", double Strength = 0.5);

/// <summary>P8: a leveler. <see cref="Kind"/> is <c>speech</c> or <c>dynamic</c>; <see cref="TargetDb"/> the level it works toward, in dBFS.</summary>
public record AudioLeveler(string Kind = "speech", double TargetDb = -18);

/// <summary>
/// P8: a submix bus. Tracks route to it through <see cref="Track.BusId"/>, a bus routes to another
/// through <see cref="OutputBusId"/> (never in a cycle, depth ≤ <see cref="EditTimelineValidator.MaxNestingDepth"/>),
/// and a bus with no output feeds the master. A bus is processed like a track: fader (static or
/// automated), pan, EQ, dynamics, denoise, leveler.
/// </summary>
public record AudioBus(
    Guid Id,
    string Name,
    Keyframed<double>? GainDb = null,
    double Pan = 0,
    TrackEq? Eq = null,
    AudioDynamics? Dynamics = null,
    AudioDenoise? Denoise = null,
    AudioLeveler? Leveler = null,
    Guid? OutputBusId = null);
