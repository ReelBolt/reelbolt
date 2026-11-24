using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video.Cleanup;

/// <summary>
/// First-party ffmpeg argv of the <c>AudioCleanup</c> step beyond the local filter chain
/// (<see cref="LocalAudioCleaner.BuildArgs"/>): extracting a source's audio for a hosted request, conforming a
/// vendor's stem back to the source's own format and length, and putting cleaned audio back under the picture. Every
/// number is formatted from a typed value measured by a probe or fixed here; no model output, URL or user text
/// reaches an argument.
/// </summary>
public static class AudioCleanupArgv
{
    /// <summary>The demuxers a vendor's stem may arrive in (the stem is decoded by this host, never by the vendor's URL).</summary>
    public const string StemFormatWhitelist = "mp3,wav,aac,flac,ogg";

    /// <summary>The probe of a vendor's stem: JSON, local files only, audio demuxers only.</summary>
    public static string[] BuildStemProbeArgs(string stemPath) =>
    [
        "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
        "-protocol_whitelist", "file",
        "-format_whitelist", StemFormatWhitelist,
        stemPath
    ];

    /// <summary>The source's first audio stream as 16-bit PCM WAV with <paramref name="channels"/> channels (at most 2), picture dropped.</summary>
    public static string[] BuildExtractAudioArgs(string inputPath, string outputPath, int channels) =>
    [
        "-hide_banner", "-nostdin", "-y", "-v", "error",
        "-protocol_whitelist", "file",
        "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
        "-i", inputPath,
        "-vn", "-map", "0:a:0",
        "-ac", Math.Clamp(channels, 1, 2).ToString(CultureInfo.InvariantCulture),
        "-c:a", "pcm_s16le",
        "-f", "wav",
        outputPath
    ];

    /// <summary>
    /// A vendor's stem decoded to the source's sample rate and channel count, padded or trimmed to exactly the source's
    /// duration (the same pin the local cleaner uses, so a few frames of codec delay never shift the length).
    /// </summary>
    public static string[] BuildConformArgs(string stemPath, string outputPath, double durationSec, int sampleRate, int channels)
    {
        string d = durationSec.ToString("F6", CultureInfo.InvariantCulture);
        return
        [
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", StemFormatWhitelist,
            "-i", stemPath,
            "-vn",
            "-af", $"apad=whole_dur={d},atrim=end={d}",
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", Math.Clamp(channels, 1, 2).ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            "-f", "wav",
            outputPath
        ];
    }

    /// <summary>
    /// The picture of <paramref name="videoPath"/> copied untouched (<c>-c:v copy</c>, first video stream only) with
    /// <paramref name="cleanWavPath"/> as its audio. An mp4 takes AAC; a mov takes 16-bit PCM (which the mov container
    /// carries without a lossy second encode). The audio is the only stream that is re-encoded.
    /// </summary>
    public static string[] BuildRemuxArgs(string videoPath, string cleanWavPath, string outputPath, bool mov)
    {
        List<string> args =
        [
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", videoPath,
            "-protocol_whitelist", "file",
            "-format_whitelist", "wav",
            "-i", cleanWavPath,
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy"
        ];
        if (mov)
        {
            args.AddRange(["-c:a", "pcm_s16le", "-f", "mov"]);
        }
        else
        {
            args.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4"]);
        }

        args.Add(outputPath);
        return [.. args];
    }
}
