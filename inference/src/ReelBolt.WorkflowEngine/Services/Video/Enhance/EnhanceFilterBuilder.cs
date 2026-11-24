using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.Enhance;

/// <summary>The two denoise tables (see <see cref="EnhanceFilterBuilder"/>).</summary>
public enum DenoiseStrength
{
    Light,
    Strong
}

/// <summary>
/// One local enhancement request. Every field is a closed choice or a bounded integer; nothing here
/// is free text and nothing reaches argv without being checked by <see cref="EnhanceFilterBuilder"/>.
/// Exactly one of <see cref="UpscaleFactor"/> and <see cref="TargetHeight"/> is set for
/// <see cref="VideoEnhanceOp.Upscale"/>; <see cref="FrameMultiplier"/> is required for Interpolate;
/// <see cref="DenoiseStrength"/> is required for Denoise; <see cref="ShutterAngle"/> applies to
/// MotionBlur only (default 180).
/// </summary>
public sealed record LocalEnhanceRequest
{
    public required VideoEnhanceOp Op { get; init; }

    /// <summary>Upscale multiplier: 2 or 4.</summary>
    public int? UpscaleFactor { get; init; }

    /// <summary>Upscale target height: 720, 1080, 1440 or 2160.</summary>
    public int? TargetHeight { get; init; }

    /// <summary>Interpolate multiplier: 2, 4 or 8.</summary>
    public int? FrameMultiplier { get; init; }

    public DenoiseStrength? DenoiseStrength { get; init; }

    /// <summary>Append a light temporal grain after denoising.</summary>
    public bool KeepGrain { get; init; }

    /// <summary>Motion-blur shutter angle in degrees, 90..360; null means 180.</summary>
    public int? ShutterAngle { get; init; }
}

/// <summary>
/// What the source itself said. Filled only from an <see cref="IMediaProbe"/> result; frame count is
/// derived from the probed duration and exact rational rate because the shared probe result does
/// not carry <c>nb_frames</c>.
/// </summary>
public sealed record SourceFacts(
    int Width,
    int Height,
    int FpsNum,
    int FpsDen,
    int FrameCount,
    string? PixFmt,
    string? ColorTransfer,
    string? ColorPrimaries,
    int? BitDepth,
    double DurationSec,
    bool HasAudio)
{
    /// <summary>Exact rational rate as a double; 30 when the probe could not report one.</summary>
    public double Fps => FpsNum > 0 && FpsDen > 0 ? FpsNum / (double)FpsDen : 30.0;

    public int ShortSide => Math.Min(Width, Height);

    public int LongSide => Math.Max(Width, Height);

    /// <summary>True for a stream that is HDR-tagged or carries more than 8 bits per component.</summary>
    public bool NeedsSdr => (BitDepth ?? 8) > 8 || TimelineRender.HdrToneMap.KindOf(ColorTransfer) is not null;

    public static SourceFacts FromProbe(MediaProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        int num = probe.FpsNum > 0 ? probe.FpsNum : 30;
        int den = probe.FpsDen > 0 ? probe.FpsDen : 1;
        double fps = num / (double)den;
        int frames = probe.DurationSec > 0 && fps > 0
            ? (int)Math.Round(probe.DurationSec * fps, MidpointRounding.AwayFromZero)
            : 0;
        return new SourceFacts(
            probe.Width,
            probe.Height,
            num,
            den,
            frames,
            probe.PixFmt,
            probe.ColorTransfer,
            probe.ColorPrimaries,
            probe.BitDepth,
            probe.DurationSec,
            !string.IsNullOrEmpty(probe.AudioCodec));
    }
}

/// <summary>The builder's answer: the complete <c>-vf</c> chain and the geometry it produces.</summary>
public sealed record EnhanceFilterPlan(
    string VideoFilter,
    int OutWidth,
    int OutHeight,
    double OutFps,
    string Method,
    IReadOnlyList<string> Warnings);

/// <summary>A typed, stable refusal. <see cref="Field"/> names the request field at fault.</summary>
public sealed record EnhanceRefusal(string Code, string Field, string Message);

/// <summary>Plan-or-refusal; exactly one side is non-null.</summary>
public sealed record EnhanceBuildResult(EnhanceFilterPlan? Plan, EnhanceRefusal? Refusal)
{
    public bool Succeeded => Plan is not null;

    public static EnhanceBuildResult Ok(EnhanceFilterPlan plan) => new(plan, null);

    public static EnhanceBuildResult Refuse(string code, string field, string message) =>
        new(null, new EnhanceRefusal(code, field, message));
}

/// <summary>
/// Pure, I/O-free mapping from a closed <see cref="LocalEnhanceRequest"/> + probed
/// <see cref="SourceFacts"/> to one ffmpeg <c>-vf</c> chain. Every number is a constant in this
/// file or comes from the probe; nothing is free input. The chain always ends in
/// <c>format=yuv420p</c>, and the output is always CFR at the plan's <see cref="EnhanceFilterPlan.OutFps"/>.
/// </summary>
public static class EnhanceFilterBuilder
{
    public const string InvalidRequest = "ENHANCE_INVALID_REQUEST";
    public const string NotLarger = "ENHANCE_NOT_LARGER";
    public const string OutputTooLarge = "ENHANCE_OUTPUT_TOO_LARGE";
    public const string FpsTooHigh = "ENHANCE_FPS_TOO_HIGH";

    /// <summary>Output caps: short side 2160, long side 4096 (plan section 10).</summary>
    public const int MaxShortSide = 2160;

    public const int MaxLongSide = 4096;

    /// <summary>Interpolation output cap, frames per second.</summary>
    public const double MaxOutputFps = 240.0;

    // Denoise Light: hqdn3d's four taps are luma spatial, chroma spatial, luma temporal, chroma
    // temporal. The light table removes sensor/upload grain without softening skin or edges much.
    public const string DenoiseLight = "hqdn3d=3:2:6:4.5";

    // Denoise Strong below 1080p: nlmeans (non-local means) is the better-quality denoiser, but it
    // is O(patch window) per pixel and far too slow above ~1080p short side, so bigger sources take
    // the stronger hqdn3d table instead (see DenoiseStrongLarge).
    public const string DenoiseStrongSmall = "nlmeans=s=4:p=7:r=15";

    public const string DenoiseStrongLarge = "hqdn3d=4:3:8:6";

    /// <summary>Strong denoise switches from nlmeans to hqdn3d above this short side.</summary>
    public const int NlmeansMaxShortSide = 1080;

    // KeepGrain: a light temporal+uniform noise re-added after denoising, so a cleaned shot does not
    // look plastic. It is synthetic grain, never the source's own.
    public const string KeepGrainNoise = "noise=alls=5:allf=t";

    // MotionBlur: interpolate to 8x the source rate (capped at 480 internal fps), average a shutter
    // window with tmix, then return to the source rate. tmix frames = round(multiplier * angle/360).
    public const int MotionBlurMultiplier = 8;

    public const double MaxInternalFps = 480.0;

    public const int DefaultShutterAngle = 180;

    public const int MinShutterAngle = 90;

    public const int MaxShutterAngle = 360;

    // Upscale: Lanczos scale then a light CAS (contrast-adaptive sharpen) to restore a little of the
    // detail lanczos softened. cas strength is 0..1.
    public const string UpscaleCas = "cas=strength=0.3";

    // The minterpolate argument set copied from RegionFilterGraphBuilder (motion-compensated
    // interpolation: mci with bidirectional overlap block matching). Do not diverge from it.
    private const string MinterpolateArgs = "mi_mode=mci:mc_mode=aobmc:vsbmc=1";

    public static EnhanceBuildResult Build(LocalEnhanceRequest request, SourceFacts source)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0 || source.Fps <= 0 || source.DurationSec <= 0)
        {
            return EnhanceBuildResult.Refuse(InvalidRequest, "source", "The source has no usable video geometry.");
        }

        return request.Op switch
        {
            VideoEnhanceOp.Denoise => BuildDenoise(request, source),
            VideoEnhanceOp.MotionBlur => BuildMotionBlur(request, source),
            VideoEnhanceOp.Upscale => BuildUpscale(request, source),
            VideoEnhanceOp.Interpolate => BuildInterpolate(request, source),
            _ => EnhanceBuildResult.Refuse(InvalidRequest, "op", "Unknown local enhancement operation.")
        };
    }

    private static EnhanceBuildResult BuildDenoise(LocalEnhanceRequest request, SourceFacts source)
    {
        if (request.DenoiseStrength is not { } strength)
        {
            return EnhanceBuildResult.Refuse(InvalidRequest, "denoiseStrength", "Denoise needs denoiseStrength Light or Strong.");
        }

        string method = strength switch
        {
            DenoiseStrength.Light => DenoiseLight,
            _ => source.ShortSide <= NlmeansMaxShortSide ? DenoiseStrongSmall : DenoiseStrongLarge
        };
        string chain = request.KeepGrain ? $"{method},{KeepGrainNoise}" : method;
        return Finish(request, source, chain, source.Width, source.Height, source.Fps,
            source.NeedsSdr ? "hdr_converted_to_sdr" : null,
            strength == DenoiseStrength.Light ? "hqdn3d"
                : source.ShortSide <= NlmeansMaxShortSide ? "nlmeans" : "hqdn3d-strong");
    }

    private static EnhanceBuildResult BuildMotionBlur(LocalEnhanceRequest request, SourceFacts source)
    {
        int angle = request.ShutterAngle ?? DefaultShutterAngle;
        if (angle is < MinShutterAngle or > MaxShutterAngle)
        {
            return EnhanceBuildResult.Refuse(
                InvalidRequest, "shutterAngle", "shutterAngle must be between 90 and 360 degrees.");
        }

        // The internal multiplier is 8x, reduced only when 8x would exceed the 480 fps internal cap.
        int multiplier = MotionBlurMultiplier;
        while (multiplier > 1 && (source.FpsNum * (double)multiplier) / source.FpsDen > MaxInternalFps)
        {
            multiplier--;
        }

        int internalNum = source.FpsNum * multiplier;
        int window = Math.Max(1, (int)Math.Round(multiplier * angle / 360.0, MidpointRounding.AwayFromZero));
        string chain =
            $"minterpolate=fps={internalNum}/{source.FpsDen}:{MinterpolateArgs},"
            + $"tmix=frames={window},"
            + $"fps={source.FpsNum}/{source.FpsDen}";
        return Finish(
            request, source, chain, source.Width, source.Height, source.Fps,
            source.NeedsSdr ? "hdr_converted_to_sdr" : null, "minterpolate+tmix");
    }

    private static EnhanceBuildResult BuildUpscale(LocalEnhanceRequest request, SourceFacts source)
    {
        bool byFactor = request.UpscaleFactor is not null;
        bool byHeight = request.TargetHeight is not null;
        if (byFactor == byHeight)
        {
            return EnhanceBuildResult.Refuse(
                InvalidRequest,
                "upscaleFactor",
                "Upscale needs exactly one of upscaleFactor (2 or 4) and targetHeight (720, 1080, 1440, 2160).");
        }

        int outHeight;
        string method;
        if (byFactor)
        {
            int factor = request.UpscaleFactor!.Value;
            if (factor is not (2 or 4))
            {
                return EnhanceBuildResult.Refuse(InvalidRequest, "upscaleFactor", "upscaleFactor must be 2 or 4.");
            }

            outHeight = source.Height * factor;
            method = $"lanczos{factor}x";
        }
        else
        {
            int target = request.TargetHeight!.Value;
            if (target is not (720 or 1080 or 1440 or 2160))
            {
                return EnhanceBuildResult.Refuse(
                    InvalidRequest, "targetHeight", "targetHeight must be 720, 1080, 1440 or 2160.");
            }

            outHeight = target;
            method = $"lanczos-{target}p";
        }

        if (outHeight <= source.Height)
        {
            return EnhanceBuildResult.Refuse(NotLarger, "targetHeight", "Upscale never makes the picture smaller.");
        }

        // scale=-2:H truncates w*H/h and then rounds UP to the next even number (measured against
        // ffmpeg: 1067x600 -> 1080 gives 1920, 1001x600 -> 1080 gives 1802); mirror that exactly.
        int outWidth = Even((int)((long)source.Width * outHeight / source.Height));
        if (Math.Min(outWidth, outHeight) > MaxShortSide || Math.Max(outWidth, outHeight) > MaxLongSide)
        {
            return EnhanceBuildResult.Refuse(
                OutputTooLarge, "targetHeight", "The output exceeds the 2160 short-side / 4096 long-side cap.");
        }

        string chain = $"scale=-2:{outHeight}:flags=lanczos,{UpscaleCas}";
        double outFps = source.Fps;
        return Finish(request, source, chain, outWidth, outHeight, outFps,
            source.NeedsSdr ? "hdr_converted_to_sdr" : null, method);
    }

    private static EnhanceBuildResult BuildInterpolate(LocalEnhanceRequest request, SourceFacts source)
    {
        if (request.FrameMultiplier is not { } multiplier || multiplier is not (2 or 4 or 8))
        {
            return EnhanceBuildResult.Refuse(
                InvalidRequest, "frameMultiplier", "Interpolate needs frameMultiplier 2, 4 or 8.");
        }

        int outNum = checked(source.FpsNum * multiplier);
        double outFps = outNum / (double)source.FpsDen;
        if (outFps > MaxOutputFps)
        {
            return EnhanceBuildResult.Refuse(
                FpsTooHigh, "frameMultiplier", "The interpolated output would exceed 240 fps.");
        }

        string chain = $"minterpolate=fps={outNum}/{source.FpsDen}:{MinterpolateArgs}";
        return Finish(request, source, chain, source.Width, source.Height, outFps,
            source.NeedsSdr ? "hdr_converted_to_sdr" : null, "minterpolate");
    }

    /// <summary>
    /// Prepends the shared SDR tone-map chain for HDR-tagged sources, appends <c>format=yuv420p</c>,
    /// and returns the plan. A &gt;8-bit SDR source needs only the pixel-format conversion (the final
    /// <c>format=yuv420p</c>), plus the same warning, because tone-mapping an SDR signal would
    /// distort it.
    /// </summary>
    private static EnhanceBuildResult Finish(
        LocalEnhanceRequest request,
        SourceFacts source,
        string chain,
        int outWidth,
        int outHeight,
        double outFps,
        string? warning,
        string method)
    {
        string prefix = TimelineRender.HdrToneMap.KindOf(source.ColorTransfer) is { } kind
            ? TimelineRender.HdrToneMap.Filter(kind) + ","
            : string.Empty;
        string filter = $"{prefix}{chain},format=yuv420p";
        var warnings = warning is null ? Array.Empty<string>() : new[] { warning };
        return EnhanceBuildResult.Ok(
            new EnhanceFilterPlan(filter, outWidth, outHeight, outFps, method, warnings));
    }

    /// <summary>Rounds up to the next even width (libx264/yuv420p need even dimensions).</summary>
    internal static int Even(int value) => value + (value & 1);
}
