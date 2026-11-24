namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The CLOSED list of audio-cleanup targets that run locally (free, offline, ffmpeg only).
/// Persisted by name by later layers: append-only, never rename or reorder. "music" removal is
/// deliberately not here; it is hosted-AI only.
/// </summary>
public enum LocalCleanupTarget
{
    VoiceBasic,
    Hum,
    Wind,
    Hiss,
    Echo
}
