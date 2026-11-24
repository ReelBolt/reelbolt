using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Builds the argv for the editor's stabilised preview proxy: the plain proxy's encode (same codec,
/// size and GOP, audio dropped) with the render's two-pass <c>vidstab</c> chain. The <c>vidstab</c>
/// filters come from <see cref="StabilizeFilter"/>, the same definition the timeline render uses.
/// </summary>
/// <remarks>
/// Both passes scale first and stabilise the small frame, so the motion file is measured at the size it
/// is applied at. The whole source is stabilised (the render stabilises the clip's span), so a short
/// clip's edges may differ slightly from the render; the viewer marks the preview approximate.
/// Nothing here is caller text: tokens are first-party constants or typed values from the source's probe.
/// </remarks>
public static class StabilizedProxyArgvBuilder
{
    /// <summary>Pass 1: analyse the scaled picture into <paramref name="trfPath"/>; no output file.</summary>
    public static string[] BuildDetect(string inputPath, string trfPath, int targetHeight, bool scaleWidth, int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trfPath);

        return
        [
            "-y", "-nostdin", "-hide_banner",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", inputPath,
            "-an",
            "-vf", $"{Scale(targetHeight, scaleWidth)},{StabilizeFilter.Detect(trfPath)}",
            "-threads", FfmpegArgvFormat.Number(Math.Max(1, threads)),
            "-f", "null", "-"
        ];
    }

    /// <summary>Pass 2: apply the motion file and encode the proxy (audio dropped).</summary>
    public static string[] BuildEncode(
        string inputPath,
        string trfPath,
        string outputPath,
        int targetHeight,
        int gopFrames,
        int threads,
        bool scaleWidth = false,
        string? hdrKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        int gop = Math.Max(1, gopFrames);
        List<string> args =
        [
            "-y", "-nostdin", "-hide_banner",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", inputPath,
            "-an",
            "-vf", Scale(targetHeight, scaleWidth) + HdrToneMap.Suffix(hdrKind) + "," + StabilizeFilter.Transform(trfPath),
            "-c:v", MediaDerivativeSpec.ProxyVideoCodec,
            "-preset", MediaDerivativeSpec.ProxyPreset,
            "-crf", FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyCrf),
            "-profile:v", MediaDerivativeSpec.ProxyProfile,
            "-pix_fmt", MediaDerivativeSpec.ProxyPixFmt,
            "-g", FfmpegArgvFormat.Number(gop),
            "-keyint_min", FfmpegArgvFormat.Number(gop),
            "-sc_threshold", FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyScThreshold)
        ];

        if (hdrKind is not null)
            args.AddRange(HdrToneMap.Bt709OutputTags);

        args.AddRange(
        [
            "-movflags", "+faststart",
            "-threads", FfmpegArgvFormat.Number(Math.Max(1, threads)),
            "-progress", "pipe:1",
            "-nostats",
            "-y",
            outputPath
        ]);

        return args.ToArray();
    }

    private static string Scale(int targetHeight, bool scaleWidth)
    {
        int side = Math.Max(2, targetHeight);
        string sentinel = FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyWidthSentinel);
        string target = FfmpegArgvFormat.Number(side);
        return (scaleWidth ? $"scale={target}:{sentinel}" : $"scale={sentinel}:{target}")
            + $":flags={MediaDerivativeSpec.ProxyScaleFlags}";
    }
}
