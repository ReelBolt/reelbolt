using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Marketplace;

/// <summary>
/// The exact ffmpeg argv of a marketplace preview and its poster. First-party only: every number comes from options or a probe
/// of the file itself, never from a message or a model.
/// </summary>
public static class MarketplacePreviewArgvBuilder
{
    public const int MaxWidth = 1280;
    public const int MaxHeight = 720;
    public const int VideoCrf = 23;
    public const string AudioBitrate = "128k";
    public const int PosterQuality = 3;

    /// <summary>The long side box: fit inside 1280x720, never enlarge, keep the aspect ratio, even sides.</summary>
    public static string ScaleFilter =>
        $"scale=w='min({FfmpegArgvFormat.Number(MaxWidth)},iw)':h='min({FfmpegArgvFormat.Number(MaxHeight)},ih)'" +
        ":force_original_aspect_ratio=decrease:force_divisible_by=2";

    public static string[] BuildPreview(string inputPath, string outputPath, double seconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", inputPath,
            "-t", FfmpegArgvFormat.Number(seconds),
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-sn",
            "-dn",
            "-map_metadata", "-1",
            "-map_chapters", "-1",
            "-vf", ScaleFilter + ",format=yuv420p",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", FfmpegArgvFormat.Number(VideoCrf),
            "-c:a", "aac",
            "-b:a", AudioBitrate,
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath
        ];
    }

    /// <summary>One JPEG from the finished preview at <paramref name="atSeconds"/>.</summary>
    public static string[] BuildPoster(string previewPath, string outputPath, double atSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-ss", FfmpegArgvFormat.Number(Math.Max(0, atSeconds)),
            "-i", previewPath,
            "-an",
            "-frames:v", "1",
            "-vf", ScaleFilter,
            "-q:v", FfmpegArgvFormat.Number(PosterQuality),
            "-update", "1",
            "-f", "image2",
            outputPath
        ];
    }

    /// <summary>About one second in, or the middle of a shorter preview.</summary>
    public static double PosterAt(double previewSeconds) =>
        previewSeconds > 2 ? 1.0 : Math.Round(Math.Max(0, previewSeconds) / 2, 3);
}
