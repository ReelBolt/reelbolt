using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>
/// The shared duration rule for every local enhancement output: the result must match the source
/// within <c>max(50 ms, one source frame)</c>. A larger drift gets one trim/pad attempt and then a
/// typed <c>ENHANCE_DURATION_MISMATCH</c>.
/// </summary>
internal static class EnhanceDuration
{
    public const double BaseToleranceSec = 0.050;

    public static double ToleranceSec(SourceFacts source) =>
        Math.Max(BaseToleranceSec, source.Fps > 0 ? 1.0 / source.Fps : 0);

    public static bool WithinTolerance(double actualSec, SourceFacts source) =>
        Math.Abs(actualSec - source.DurationSec) <= ToleranceSec(source);

    /// <summary>Trim a long output to the source duration, copying every stream (no re-encode).</summary>
    public static string[] BuildTrimArgs(string inputPath, string outputPath, double durationSec) =>
    [
        "-hide_banner", "-nostdin", "-y", "-v", "error",
        .. EnhanceInputGuards.InputOptions(),
        "-i", inputPath,
        "-map", "0:v:0", "-map", "0:a?",
        "-t", durationSec.ToString("F6", CultureInfo.InvariantCulture),
        "-c", "copy",
        "-movflags", "+faststart",
        "-f", "mp4",
        outputPath
    ];

    /// <summary>
    /// Pad a short output back to the source duration. Only the PICTURE is padded (last frame
    /// cloned with tpad); every audio stream is stream-copied untouched, the same rule as
    /// <see cref="EnhanceAudioPolicy"/>. The tolerance is already breached, so this is a
    /// best-effort repair rather than the normal path.
    /// </summary>
    public static string[] BuildPadArgs(
        string inputPath, string outputPath, double durationSec, double actualSec)
    {
        double pad = Math.Max(0.001, durationSec - actualSec + BaseToleranceSec);
        string padText = pad.ToString("F6", CultureInfo.InvariantCulture);
        string duration = durationSec.ToString("F6", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error"
        };
        args.AddRange(EnhanceInputGuards.InputOptions());
        args.AddRange([
            "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a?",
            "-vf", $"tpad=stop_mode=clone:stop_duration={padText},format=yuv420p",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-c:a", "copy",
            "-t", duration, "-movflags", "+faststart", "-f", "mp4", outputPath
        ]);
        return args.ToArray();
    }
}
