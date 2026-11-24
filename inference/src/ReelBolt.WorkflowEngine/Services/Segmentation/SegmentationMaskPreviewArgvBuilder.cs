using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Segmentation;

/// <summary>
/// The ffmpeg argv that turns one track's FFV1 grey8 mask into the browser-decodable preview the editor
/// draws its outline from. Pure: a first-party recipe, no client value reaches argv.
/// </summary>
/// <remarks>
/// Recipe (version <see cref="ReelBolt.Shared.IntegrationEvents.SegmentationMaskPreviewIdentity.RecipeVersion"/>):
/// H.264 in MP4, <c>yuv420p</c> with the FULL range flag (<c>-color_range pc</c>, x264 <c>fullrange=on</c>) so
/// luma 0..255 is the mask itself and a decoder must not squeeze it into 16..235; long side at most
/// <see cref="LongSide"/> (never upscaled, even dimensions); the mask's own frame rate and exactly its
/// frame count (the viewer maps source time to a frame index); a keyframe at least once a second;
/// <c>+faststart</c> so the file seeks and plays over ranged reads. The demuxer is pinned with
/// <c>-format_whitelist</c> like every derivative input.
/// </remarks>
public static class SegmentationMaskPreviewArgvBuilder
{
    /// <summary>Longest side of the preview, in pixels.</summary>
    public const int LongSide = 640;

    public static string[] Build(string maskPath, string outputPath, double fps, int frameCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maskPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!double.IsFinite(fps) || fps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fps), "The mask's frame rate must be positive.");
        }

        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "The mask must hold at least one frame.");
        }

        string side = FfmpegArgvFormat.Number(LongSide);

        // min(side, iw/ih) never upscales; decrease keeps the aspect inside the side x side box; an even
        // size is what yuv420p needs. The mask is full range in and out, so no range squeeze happens.
        string filter =
            $"scale=w='min({side},iw)':h='min({side},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear:in_range=full:out_range=full,format=yuv420p";

        // At most one second between keyframes at the mask's own rate.
        int gop = Math.Max(1, (int)Math.Ceiling(fps));

        return
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", maskPath,
            "-an",
            "-sn",
            "-map", "0:v:0",
            "-vf", filter,
            "-fps_mode", "cfr",
            "-r", FfmpegArgvFormat.Number(fps),
            "-frames:v", FfmpegArgvFormat.Number(frameCount),
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-color_range", "pc",
            "-x264-params", "fullrange=on",
            "-g", FfmpegArgvFormat.Number(gop),
            "-keyint_min", FfmpegArgvFormat.Number(gop),
            "-sc_threshold", "0",
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath
        ];
    }
}
