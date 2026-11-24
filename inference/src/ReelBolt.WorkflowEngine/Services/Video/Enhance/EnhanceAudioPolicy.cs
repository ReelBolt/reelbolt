namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>
/// The audio rule shared by <see cref="LocalVideoEnhancer"/> and <see cref="EnhancedAudioRemux"/>:
/// the source's original audio streams are never altered or dropped. When every source audio
/// stream is already MP4-compatible it is copied (<c>-c:a copy</c>, packet-identical); otherwise it
/// is re-encoded to AAC from the table below. A source with no audio stays silent.
/// </summary>
public static class EnhanceAudioPolicy
{
    /// <summary>Compatible codecs are copied; anything else is re-encoded to AAC.</summary>
    public static readonly IReadOnlySet<string> Mp4CompatibleCodecs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aac", "mp3", "ac3", "eac3", "alac"
        };

    /// <summary>AAC bitrate table used only when a copy is impossible.</summary>
    public const int AacBitrateKbps = 192;

    public const string AacCodec = "aac";

    public static bool CanCopy(string? audioCodec) =>
        !string.IsNullOrEmpty(audioCodec) && Mp4CompatibleCodecs.Contains(audioCodec);

    /// <summary>
    /// The map + codec arguments for the source audio (input 0): copy when compatible, AAC otherwise,
    /// <c>-an</c> when there is none. Never maps audio from a second (picture) input.
    /// </summary>
    public static string[] BuildArgs(bool hasAudio, string? audioCodec)
    {
        if (!hasAudio)
        {
            return ["-an"];
        }

        if (CanCopy(audioCodec))
        {
            return ["-map", "0:a", "-c:a", "copy"];
        }

        return
        [
            "-map", "0:a",
            "-c:a", AacCodec,
            "-b:a", $"{FfmpegArgvFormat.Number(AacBitrateKbps)}k"
        ];
    }
}
