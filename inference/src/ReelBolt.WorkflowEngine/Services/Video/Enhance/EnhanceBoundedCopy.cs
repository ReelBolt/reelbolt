using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>
/// The bounded stream copy a hosted enhancement uploads INSTEAD of the customer's original. A container's length (a Matroska or WebM
/// segment duration, an MP4 header) can be patched to claim less than the file holds, and a stream may carry no duration or frame count to
/// contradict it; uploading the original would let the vendor bill the true length. So the engine writes its own copy first: the first
/// video stream and any audio, stream-copied (no re-encode), cut at the checked length by <c>-t</c> and, when a frame count is known, by
/// <c>-frames:v</c>, with both per-input whitelists before <c>-i</c>; the copy is then measured again by the whitelisted probe and refused
/// when it is longer than the checked length by more than a frame. Shared by every hosted path (upscale, interpolation, extend).
/// </summary>
internal static class EnhanceBoundedCopy
{
    /// <summary>The video codecs that can be stream-copied into MP4 (anything else keeps Matroska).</summary>
    private static readonly IReadOnlySet<string> Mp4VideoCodecs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "mpeg4", "av1", "vp9" };

    /// <summary>True when both the picture and the sound (if any) can be stream-copied into MP4.</summary>
    public static bool FitsMp4(MediaProbeResult source) =>
        source.VideoCodec is { } video && Mp4VideoCodecs.Contains(video)
        && (string.IsNullOrWhiteSpace(source.AudioCodec) || EnhanceAudioPolicy.CanCopy(source.AudioCodec));

    /// <summary>The file extension of the copy (<c>.mp4</c> or, when the codecs cannot go in MP4, <c>.mkv</c>).</summary>
    public static string ExtensionFor(MediaProbeResult source) => FitsMp4(source) ? ".mp4" : ".mkv";

    /// <summary>The content type of the copy.</summary>
    public static string ContentTypeFor(string extension) => extension == ".mkv" ? "video/x-matroska" : "video/mp4";

    /// <summary>
    /// The ffmpeg argv: whitelists before the one <c>-i</c>, the first video stream plus any audio, stream copy, and the bounds. The
    /// frame bound is the known frame count plus one (null when no frame count is known: the time bound alone applies).
    /// </summary>
    public static string[] BuildArgs(string inputPath, string outputPath, double checkedSeconds, long? checkedFrames, bool mp4)
    {
        List<string> args = ["-hide_banner", "-nostdin", "-y", "-v", "error"];
        args.AddRange(EnhanceInputGuards.InputOptions());
        args.AddRange(["-i", inputPath, "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-c", "copy"]);
        args.AddRange(["-t", checkedSeconds.ToString("F6", CultureInfo.InvariantCulture)]);
        if (checkedFrames is { } frames && frames > 0)
        {
            args.AddRange(["-frames:v", (frames + 1).ToString(CultureInfo.InvariantCulture)]);
        }

        if (mp4)
        {
            args.AddRange(["-movflags", "+faststart"]);
        }

        args.AddRange(["-f", mp4 ? "mp4" : "matroska", outputPath]);
        return [.. args];
    }

    /// <summary>
    /// True when the copy is longer than the checked length by more than one frame at <paramref name="fps"/> (a small floor covers a
    /// source whose rate is unknown): the copy was supposed to be cut at that length, so a longer one is not the source that was checked.
    /// </summary>
    public static bool ExceedsChecked(double copySeconds, double checkedSeconds, double fps)
    {
        double frame = fps > 0 ? 1d / fps : 0.04;
        return copySeconds > checkedSeconds + Math.Max(frame, 0.04);
    }
}
