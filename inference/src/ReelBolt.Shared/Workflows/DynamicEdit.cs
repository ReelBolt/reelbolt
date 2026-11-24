using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows;

/// <summary>The closed catalogue of dynamic-edit moves (docs/design/dynamic-edit.md). <b>APPEND ONLY</b>, persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicEffect { SplitOnBeat, PunchIn, PunchOut, BeatAlternate, StrobeCut, FlashFrame, SpeedRamp, WhipOnBar }

/// <summary>When a move lands. <b>APPEND ONLY</b>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicMoment { OnBeat, OnBar, OnDrop, OnAccent }

/// <summary>How many beats a BeatAlternate runs. <b>APPEND ONLY</b>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicBeats { Two, Four, Eight, Sixteen }

/// <summary>The grid division of one alternation cell. <b>APPEND ONLY</b>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicDivision { Beat, HalfBeat, Bar }

/// <summary>Which side of a BeatAlternate holds to the end. <b>APPEND ONLY</b>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicWinner { A, B }

/// <summary>How strong a move is. <b>APPEND ONLY</b>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicIntensity { Subtle, Normal, Strong }

/// <summary>How busy the edit is. <b>APPEND ONLY</b>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DynamicPace { Calm, Medium, Fast }

/// <summary>One agent-listed move; every field a word or an offered id (no numbers on the wire).</summary>
public sealed record DynamicsEntry(
    string Effect,
    string AnchorId,
    string? OtherId,
    string Moment,
    string? Beats,
    string? Division,
    string? Winner,
    string? Intensity,
    string Reason);

/// <summary>Compile-side dynamic-edit switch (<see cref="VideoCompileStepConfig.Dynamics"/>).</summary>
public sealed record VideoDynamicsConfig(
    bool Enabled = true,
    DynamicPace Pace = DynamicPace.Medium,
    bool KeepSceneBoundaries = true)
{
    /// <summary>Config error code: Dynamics needs a re-encode.</summary>
    public const string RequiresReencodeCode = "DYNAMICS_REQUIRES_REENCODE";
}
