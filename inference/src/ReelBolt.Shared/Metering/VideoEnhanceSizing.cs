using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The output size, file size and processing time of a video enhancement, as a small pure helper so the estimate route (and the
/// assistant) can show them before anything is queued. Every figure is an ESTIMATE, never a promise: the geometry follows the same
/// rules as the engines (even dimensions, the rate times the multiplier), the bytes come from a bitrate table by output tier, and
/// the processing time from a per-operation speed table (local) or a hosted constant plus a per-second share.
/// </summary>
public static class VideoEnhanceSizing
{
    /// <summary>The output picture and rate an operation produces from a source.</summary>
    public sealed record Output(int Width, int Height, double Fps);

    /// <summary>The geometry of the result: an upscale scales the picture (factor, or a target short side), an interpolation multiplies the rate, the rest keep both.</summary>
    public static Output OutputFor(VideoEnhanceStepConfig config, int sourceWidth, int sourceHeight, double sourceFps)
    {
        ArgumentNullException.ThrowIfNull(config);
        switch (config.Op)
        {
            case VideoEnhanceOp.Upscale:
                {
                    int shortSide = Math.Max(1, Math.Min(sourceWidth, sourceHeight));
                    double factor = config.UpscaleFactor is { } f ? f : config.TargetHeight is { } h ? (double)h / shortSide : 1d;
                    return new Output(EvenAtLeast2(sourceWidth * factor), EvenAtLeast2(sourceHeight * factor), sourceFps);
                }

            case VideoEnhanceOp.Interpolate:
                return new Output(sourceWidth, sourceHeight, sourceFps * (config.FrameMultiplier ?? 2));

            case VideoEnhanceOp.Extend:
                return config.ExtendCanvas(sourceWidth, sourceHeight) is { } canvas
                    ? new Output(canvas.Width, canvas.Height, sourceFps)
                    : new Output(sourceWidth, sourceHeight, sourceFps);

            default:
                return new Output(sourceWidth, sourceHeight, sourceFps);
        }
    }

    /// <summary>Delivery bitrate in bits per second by the output's short side (720p, 1080p, 1440p, above), x1.5 above 30 fps.</summary>
    public static long BitsPerSecond(int outputWidth, int outputHeight, double outputFps)
    {
        int shortSide = Math.Min(outputWidth, outputHeight);
        long bps = shortSide switch { <= 720 => 4_000_000, <= 1080 => 8_000_000, <= 1440 => 16_000_000, _ => 35_000_000 };
        return outputFps > 30 ? bps * 3 / 2 : bps;
    }

    /// <summary>Estimated bytes of the MP4: the bitrate for the whole duration, plus the source's audio at 192 kbit/s.</summary>
    public static long EstimatedBytes(Output output, double durationSec) =>
        (long)Math.Ceiling((BitsPerSecond(output.Width, output.Height, output.Fps) + 192_000) * Math.Max(0, durationSec) / 8d);

    /// <summary>Hosted: a fixed queue and download share plus a per-source-second share (the vendors run faster than real time).</summary>
    public const int HostedBaseSeconds = 45;

    /// <summary>Hosted: processing seconds per source second.</summary>
    public const double HostedSecondsPerSourceSecond = 3;

    /// <summary>
    /// Estimated processing seconds. Local: the engine's measured-order speed per operation (seconds of work per source second at 1080p,
    /// scaled by the output pixel count); hosted: a constant plus a per-second share.
    /// </summary>
    public static int EstimatedProcessingSeconds(VideoEnhanceStepConfig config, double durationSec, Output output)
    {
        ArgumentNullException.ThrowIfNull(config);
        double seconds = Math.Max(0, durationSec);
        if (config.IsHosted)
            return (int)Math.Ceiling(HostedBaseSeconds + seconds * HostedSecondsPerSourceSecond);

        double pixelScale = Math.Max(0.25, (double)output.Width * output.Height / (1920d * 1080d));
        double perSecond = config.Op switch
        {
            VideoEnhanceOp.Denoise => config.DenoiseStrength == VideoEnhanceDenoiseStrength.Strong ? 3.0 : 0.4,
            VideoEnhanceOp.MotionBlur => 2.0,
            VideoEnhanceOp.Upscale => 0.6,
            _ => 1.5 * (config.FrameMultiplier ?? 2) / 2d
        };
        return (int)Math.Ceiling(5 + seconds * perSecond * pixelScale);
    }

    private static int EvenAtLeast2(double value) => Math.Max(2, (int)Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2);
}
