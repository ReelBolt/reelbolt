using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The delivery copy of an oversized video staged into the Remotion sandbox (StageProjectFile,
/// StageGeneratedClips). Pure: the decision, the ffmpeg argv and the cache key; no I/O.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Remotion's <c>&lt;OffthreadVideo&gt;</c> decodes every source frame into an RGB(A) frame
/// cache that is split between the render's browser tabs. A 2160x3840 frame is about 25 MB, so with
/// several 4K clips on screen at once the cache holds only a few frames per clip, every frame is
/// decoded again from the last keyframe, and the render stalls until Remotion gives up ("Could not
/// extract frame from compositor", or a <c>delayRender()</c> timeout on <c>/proxy?src=</c>). Reproduced
/// on 2026-11-05 with the six clips of runs 36df4287 / 9bf15ff8 (five 2160x3840 and one 1440x2560 in a
/// 1080x1920 grid): the originals fail at concurrency 2 even with a 2 GiB cache; these copies render
/// every frame (frame-for-frame against the source) once the cache is sized for them.
/// </para>
/// <para>
/// <b>What the copy keeps.</b> The same frames at the same times (<c>-fps_mode passthrough</c>, never
/// resampled), the long side fitted to <see cref="DefaultMaxLongSide"/> with lanczos, 8-bit 4:2:0 BT.709
/// limited range (a full-range <c>yuvj420p</c> source is converted, HLG/PQ is tone-mapped with the same
/// <see cref="TimelineRender.HdrToneMap"/> the timeline renderer uses when this host can), a one-second
/// GOP so seeks stay cheap, AAC stereo audio and <c>+faststart</c>. The bytes are an ISO-BMFF MP4, so a
/// copy is only made for a destination named <c>.mp4</c>, <c>.m4v</c> or <c>.mov</c>; it keeps that name.
/// A transparent source is never copied (the copy would lose its alpha).
/// </para>
/// <para>
/// Every argv token is a constant or a number this code owns, plus the two scratch paths; the input is
/// held to the derivative demuxer whitelist (<see cref="MediaDerivativeInputFormats.Whitelist"/>).
/// </para>
/// </remarks>
public static class StagedVideoProxy
{
    /// <summary>Folded into the cache key: bump it when the recipe changes so old copies are not reused.</summary>
    public const string RecipeVersion = "staged-video-proxy-v1";

    /// <summary>Configuration key of the longest side a staged video keeps before a copy is made.</summary>
    public const string MaxLongSideKey = "Compute:Render:StagedVideoMaxLongSide";

    /// <summary>A 1080x1920 (or 1920x1080) composition's own long side.</summary>
    public const int DefaultMaxLongSide = 1920;

    /// <summary>Under this a configured value is ignored (a copy smaller than 720p would be visibly soft).</summary>
    public const int MinMaxLongSide = 1280;

    public const int Crf = 18;
    public const string Preset = "veryfast";
    public const int AudioBitrateKbps = 160;
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;

    /// <summary>Where the copies and their facts live, under <c>projects/{projectId}/</c>.</summary>
    public const string KeyPrefix = "agentFiles/render-proxies/";

    private static readonly HashSet<string> ProxyableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov",
    };

    /// <summary>Why a staged video was, or was not, copied.</summary>
    public static class Reasons
    {
        public const string Oversized = "oversized";
        public const string WithinSize = "within_size";
        public const string HasAlpha = "has_alpha";
        public const string Container = "container_not_mp4_family";
        public const string NoVideo = "no_video_stream";
    }

    public sealed record Decision(bool MakeCopy, string Reason);

    /// <summary>
    /// The longest side a staged video may keep: <paramref name="configured"/> (at least
    /// <see cref="MinMaxLongSide"/>), raised to a 16:9 frame of the run's chosen output short side when
    /// the run asked for more (a run rendered at 2160p keeps its 3840-wide footage).
    /// </summary>
    public static int MaxLongSideFor(int configured, int? chosenShortSide)
    {
        int max = configured >= MinMaxLongSide ? configured : DefaultMaxLongSide;
        if (chosenShortSide is > 0)
            max = Math.Max(max, (int)Math.Ceiling(chosenShortSide.Value * 16.0 / 9.0));
        return max;
    }

    /// <summary>Whether a copy can be staged at <paramref name="destinationPath"/> at all (an MP4-family name).</summary>
    public static bool IsProxyableDestination(string? destinationPath) =>
        ProxyableExtensions.Contains(Path.GetExtension(destinationPath ?? string.Empty));

    /// <summary>
    /// The decisions that hold for a file whatever the size limit (transparent, not an MP4-family container,
    /// no picture): a restored workspace never looks at such a recorded video again.
    /// </summary>
    public static bool IsFinal(string? reason) =>
        reason is Reasons.HasAlpha or Reasons.Container or Reasons.NoVideo;

    /// <summary>Whether the video at <paramref name="destinationPath"/> with these probed facts gets a copy.</summary>
    public static Decision Decide(MediaProbeResult probe, string destinationPath, int maxLongSide)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.Width <= 0 || probe.Height <= 0)
            return new Decision(false, Reasons.NoVideo);
        if (Math.Max(probe.Width, probe.Height) <= maxLongSide)
            return new Decision(false, Reasons.WithinSize);
        if (AlphaPixelFormats.HasAlpha(probe.PixFmt, probe.AlphaModeTag))
            return new Decision(false, Reasons.HasAlpha);
        if (!ProxyableExtensions.Contains(Path.GetExtension(destinationPath ?? string.Empty)))
            return new Decision(false, Reasons.Container);
        return new Decision(true, Reasons.Oversized);
    }

    /// <summary>One keyframe a second (at least 1, at most 60 frames); 30 when the rate is unknown.</summary>
    public static int GopFrames(int fpsNum, int fpsDen)
    {
        if (fpsNum <= 0 || fpsDen <= 0)
            return 30;
        return Math.Clamp((int)Math.Round((double)fpsNum / fpsDen, MidpointRounding.AwayFromZero), 1, 60);
    }

    /// <summary>
    /// The scale filter: the long side (after ffmpeg's autorotate) fitted to <paramref name="maxLongSide"/>,
    /// even dimensions, never upscaled, lanczos, limited range out.
    /// </summary>
    public static string ScaleFilter(int maxLongSide)
    {
        string side = FfmpegArgvFormat.Number(maxLongSide);
        return $"scale=w='if(gte(iw,ih),min(iw,{side}),-2)':h='if(gte(iw,ih),-2,min(ih,{side}))':flags=lanczos:out_range=tv";
    }

    /// <summary>The full video filter chain: tone-map first when <paramref name="hdrKind"/> is set, then scale, then 8-bit 4:2:0.</summary>
    public static string VideoFilter(int maxLongSide, string? hdrKind) =>
        TimelineRender.HdrToneMap.Prefix(hdrKind) + ScaleFilter(maxLongSide) + ",format=yuv420p";

    /// <summary>The ffmpeg argv that writes the copy of <paramref name="inputPath"/> to <paramref name="outputPath"/>.</summary>
    public static string[] BuildArgs(
        string inputPath, string outputPath, MediaProbeResult probe, int maxLongSide, string? hdrKind, int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(probe);
        string gop = FfmpegArgvFormat.Number(GopFrames(probe.FpsNum, probe.FpsDen));
        List<string> args =
        [
            "-y", "-nostdin", "-hide_banner", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-map_metadata", "-1", "-map_chapters", "-1",
            "-sn", "-dn",
            "-vf", VideoFilter(maxLongSide, hdrKind),
            // The same frames at the same times: never a resampled frame rate.
            "-fps_mode", "passthrough",
            "-c:v", "libx264", "-preset", Preset, "-crf", FfmpegArgvFormat.Number(Crf),
            "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-color_range", "tv",
        ];
        if (hdrKind is not null)
        {
            args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
        }
        args.AddRange(["-g", gop, "-keyint_min", gop, "-sc_threshold", "0"]);
        if (threads > 0)
            args.AddRange(["-threads", FfmpegArgvFormat.Number(threads)]);
        args.AddRange(
        [
            "-c:a", "aac", "-b:a", FfmpegArgvFormat.Number(AudioBitrateKbps) + "k",
            "-ac", FfmpegArgvFormat.Number(AudioChannels), "-ar", FfmpegArgvFormat.Number(AudioSampleRate),
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath
        ]);
        return [.. args];
    }

    /// <summary>
    /// The storage key (without extension) of the copy and facts of one source object:
    /// <c>projects/{projectId}/agentFiles/render-proxies/{sha256}</c> over the recipe, the source key,
    /// its size, its ETag (when known) and the long side. A changed object or recipe misses.
    /// </summary>
    public static string CacheKeyStem(Guid projectId, string sourceKey, long sourceSize, string? sourceEtag, int maxLongSide)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        StringBuilder canonical = new();
        foreach (string part in new[]
                 {
                     RecipeVersion, sourceKey, FfmpegArgvFormat.Number(sourceSize), sourceEtag?.Trim('"') ?? "-",
                     FfmpegArgvFormat.Number(maxLongSide)
                 })
        {
            canonical.Append(part.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(part).Append('|');
        }
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return $"projects/{projectId}/{KeyPrefix}{hash}";
    }
}
