using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// A small, silent, low-effort copy of a large source that the shot, sub-shot and visual-grid passes read instead of
/// the source itself: one decode of the big file replaces two or more. Frame timing is kept exactly
/// (<c>-fps_mode passthrough</c>, same frames, same timestamps), so every time found on the copy is a time on the
/// source. Every token is a constant or an integer this code owns; nothing a user or a model wrote reaches argv.
/// </summary>
public static class AnalysisProxy
{
    /// <summary>The copy's short side in pixels.</summary>
    public const int ShortSide = 540;

    /// <summary>A source is only copied when its short side is above this.</summary>
    public const int MinShortSide = 1080;

    /// <summary>Scratch space to reserve for the copy, per second of source (540p ultrafast crf 26 runs well under this).</summary>
    public const long EstimatedBytesPerSecond = 250_000;

    public const string FileSuffix = ".mkv";

    public static bool Eligible(MediaProbeResult probe, int minDurationSec) =>
        minDurationSec > 0 && probe.DurationSec >= minDurationSec && Math.Min(probe.Width, probe.Height) > MinShortSide;

    public static long EstimatedBytes(double durationSec) =>
        (long)Math.Ceiling(Math.Max(0, durationSec) * EstimatedBytesPerSecond);

    /// <summary>A source above this frame rate is thinned to it first; every decoded frame after that is cheaper.</summary>
    public const int MaxFps = 30;

    /// <summary>
    /// Thin to <see cref="MaxFps"/> (only when <paramref name="sourceFps"/> is above it), scale, then a cheap HDR
    /// conversion (analysis colour only, see <see cref="HdrToneMap.AnalysisFilter"/>). Times found on the copy are
    /// still times on the source; a frame rate taken from the copy must never replace the source's.
    /// </summary>
    public static string[] Build(string inputPath, string outputPath, int threads, string? hdrKind, double sourceFps = 0, string? cameraProfileKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string side = FfmpegArgvFormat.Number(ShortSide);
        string filter = (sourceFps > MaxFps + 0.5 ? $"fps={FfmpegArgvFormat.Number(MaxFps)}," : "") +
                        $"scale=w='if(gt(iw,ih),-2,{side})':h='if(gt(iw,ih),{side},-2)':flags=bilinear" +
                        (hdrKind is null ? "" : "," + HdrToneMap.AnalysisFilter(hdrKind)) +
                        (cameraProfileKind is null ? "" : "," + SourceCameraProfile.Filter(cameraProfileKind));
        return
        [
            "-y", "-nostdin", "-hide_banner", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", UserMediaInputFormats.Whitelist,
            "-threads", FfmpegArgvFormat.Number(Math.Max(1, threads)),
            "-i", inputPath,
            "-an", "-sn", "-dn",
            "-vf", filter,
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "26", "-g", "30",
            "-pix_fmt", "yuv420p",
            // The encoder gets the same cores as the decoder: this is the one pass that must finish inside the deadline.
            "-threads", FfmpegArgvFormat.Number(Math.Max(1, threads)),
            outputPath
        ];
    }
}
