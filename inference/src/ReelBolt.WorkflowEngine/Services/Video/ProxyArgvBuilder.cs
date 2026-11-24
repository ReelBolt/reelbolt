using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Builds the exact argv for the editor's proxy encode: an H.264/AAC mezzanine small enough for a
/// browser scrub loop.
/// </summary>
/// <remarks>
/// <para>
/// Every token is either a first-party constant from <see cref="MediaDerivativeSpec"/> or a typed,
/// clamped value the caller derived from the source's own ffprobe result — nothing here is
/// caller-supplied text, and the filtergraph is a single <c>scale</c> assembled from two integers,
/// so there is no filter-name or filter-string surface to inject into.
/// </para>
/// <para>
/// <c>-g</c> and <c>-keyint_min</c> are the same value: <c>Math.Max(1, round(fps * 0.5))</c>, i.e.
/// "≈ 0.5 s" derived from the source's real frame rate. The pair plus <c>-sc_threshold 0</c>
/// (never emit a scene-cut keyframe) is what makes a proxy seek land on a known frame instead of
/// wherever a scene change happened to be, which is the whole point of the proxy.
/// </para>
/// </remarks>
public static class ProxyArgvBuilder
{
    /// <summary>
    /// <paramref name="hasAudio"/> must come from the source's probe. When the source carries no
    /// audio stream the AAC arguments are <b>dropped entirely</b> rather than emitted against a
    /// stream that does not exist: ffmpeg would otherwise fail outright, and a caller-side
    /// "always add -c:a" would produce either a hard error or an empty silence track that a player
    /// then renders as a dead audio control.
    /// <para>
    /// <paramref name="scaleWidth"/> (portrait): <paramref name="targetHeight"/> is then the
    /// target WIDTH and the height takes the -2 sentinel. Default false is byte-identical to before.
    /// </para>
    /// </summary>
    public static string[] Build(
        string inputPath,
        string outputPath,
        int targetHeight,
        int gopFrames,
        bool hasAudio,
        int threads,
        bool scaleWidth = false,
        string? hdrKind = null,
        string? cameraProfileId = null,
        string? cubePath = null,
        bool zscaleAvailable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string conversionSuffix = "";
        if (cameraProfileId is not null)
        {
            if (hdrKind is not null)
                throw new ArgumentException("An HDR source never gets a converted proxy; it keeps its own tone-map.", nameof(cameraProfileId));
            ArgumentException.ThrowIfNullOrWhiteSpace(cubePath);
            // One definition of the stage: the timeline render and this proxy encode must agree token for
            // token, or a clip graded in the editor and its proxy would start from different pixels.
            // It runs AFTER the scale like HdrToneMap.Suffix: float conversion at full source resolution is what made
            // an HDR proxy take an hour. The scale keeps the source bit depth (no format= before the stage).
            conversionSuffix = "," + CameraProfileStage.Filter(cameraProfileId, cubePath, zscaleAvailable);
        }

        int height = Math.Max(2, targetHeight);
        int gop = Math.Max(1, gopFrames);
        int threadCount = Math.Max(1, threads);

        List<string> args =
        [
            "-y",
            "-nostdin",
            "-hide_banner",
            // The feature-wide containment discipline (see FfmpegArgvBuilder): lock the demuxer to
            // local files, so a storage key that somehow reached this path cannot select http://,
            // concat: or subfile: instead of the scratch file it was resolved to.
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", inputPath,
            // Scale FIRST, tone-map the small frame: float zscale at 4K is what made the proxy take an hour.
            "-vf", (scaleWidth
                ? $"scale={FfmpegArgvFormat.Number(height)}:{FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyWidthSentinel)}"
                : $"scale={FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyWidthSentinel)}:{FfmpegArgvFormat.Number(height)}")
                + $":flags={MediaDerivativeSpec.ProxyScaleFlags}" + HdrToneMap.Suffix(hdrKind) + conversionSuffix,
            "-c:v", MediaDerivativeSpec.ProxyVideoCodec,
            "-preset", MediaDerivativeSpec.ProxyPreset,
            "-crf", FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyCrf),
            "-profile:v", MediaDerivativeSpec.ProxyProfile,
            "-pix_fmt", MediaDerivativeSpec.ProxyPixFmt,
            "-g", FfmpegArgvFormat.Number(gop),
            "-keyint_min", FfmpegArgvFormat.Number(gop),
            "-sc_threshold", FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyScThreshold)
        ];

        if (hasAudio)
        {
            args.Add("-c:a");
            args.Add(MediaDerivativeSpec.ProxyAudioCodec);
            args.Add("-b:a");
            args.Add($"{FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyAudioBitrateKbps)}k");
            args.Add("-ar");
            args.Add(FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyAudioSampleRate));
            args.Add("-ac");
            args.Add(FfmpegArgvFormat.Number(MediaDerivativeSpec.ProxyAudioChannels));
        }

        if (hdrKind is not null || cameraProfileId is not null)
            args.AddRange(HdrToneMap.Bt709OutputTags);

        args.Add("-movflags");
        args.Add("+faststart");
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threadCount));
        args.Add("-progress");
        args.Add("pipe:1");
        args.Add("-nostats");
        args.Add("-y");
        args.Add(outputPath);

        return args.ToArray();
    }
}
