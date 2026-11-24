using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Sizes a Remotion render's deadline from what it will actually render — frame count and pixels per
/// frame — instead of one fixed number. A fixed 600 s killed a real 3591-frame 1080p render (2 min at
/// 30 fps, ~4.5 frames/s on a 2-CPU sandbox) at frame ~2740; a fixed larger number would either still
/// kill a long film or let a hung 10-second render sit for hours.
/// </summary>
/// <remarks>
/// <c>deadline = StartupSeconds + frames × SecondsPerFrame1080p × max(MinPixelFactor, pixels / 1920×1080)</c>,
/// clamped to <c>[MinSeconds, MaxSeconds]</c>. The default per-frame figure (0.5 s at 1080p) is about
/// 2× the measured rate, so a busy box still finishes. All four are configurable under
/// <c>Compute:Render:Timeout:*</c>; <c>MaxSeconds</c> must stay at or below the sandbox's
/// <c>SANDBOX_EXEC_MAX_TIMEOUT</c> (6 h by default), which otherwise clamps it.
/// </remarks>
public sealed record RenderTimeoutPolicy(
    double SecondsPerFrame1080p,
    int StartupSeconds,
    int MinSeconds,
    int MaxSeconds)
{
    public const double DefaultSecondsPerFrame1080p = 0.5;
    public const int DefaultStartupSeconds = 300;
    public const int DefaultMinSeconds = 600;

    /// <summary>6 hours — the sandbox's default exec ceiling.</summary>
    public const int DefaultMaxSeconds = 6 * 3600;

    /// <summary>Used when the composition's size is unknown; a long film is not killed for that.</summary>
    public const int UnknownCompositionSeconds = 2 * 3600;

    private const double MinPixelFactor = 0.25;
    private const double ReferencePixels = 1920.0 * 1080.0;

    public static readonly RenderTimeoutPolicy Default =
        new(DefaultSecondsPerFrame1080p, DefaultStartupSeconds, DefaultMinSeconds, DefaultMaxSeconds);

    public static RenderTimeoutPolicy From(IConfiguration configuration)
    {
        double perFrame = double.TryParse(configuration["Compute:Render:Timeout:SecondsPerFrame1080p"],
            NumberStyles.Float, CultureInfo.InvariantCulture, out double p) && p > 0 ? p : DefaultSecondsPerFrame1080p;
        int startup = ReadInt(configuration, "Compute:Render:Timeout:StartupSeconds", DefaultStartupSeconds);
        int max = ReadInt(configuration, "Compute:Render:Timeout:MaxSeconds", DefaultMaxSeconds);
        int min = Math.Min(ReadInt(configuration, "Compute:Render:Timeout:MinSeconds", DefaultMinSeconds), max);
        return new RenderTimeoutPolicy(perFrame, startup, min, max);
    }

    /// <summary>
    /// The deadline for rendering <paramref name="composition"/> with the caller's extra CLI
    /// <paramref name="remotionArgs"/> (<c>--frames=a-b</c> narrows the frame count, <c>--scale=s</c>
    /// scales the pixels). A null composition gets <see cref="UnknownCompositionSeconds"/>.
    /// </summary>
    public int Compute(CompositionInfo? composition, IReadOnlyList<string>? remotionArgs = null)
    {
        if (composition is null)
            return Math.Clamp(UnknownCompositionSeconds, MinSeconds, MaxSeconds);

        int frames = Math.Max(1, FrameCount(composition, remotionArgs));
        double scale = Scale(remotionArgs);
        double pixels = Math.Max(1, composition.Width) * Math.Max(1, composition.Height) * scale * scale;
        double pixelFactor = Math.Max(MinPixelFactor, pixels / ReferencePixels);

        double seconds = StartupSeconds + frames * SecondsPerFrame1080p * pixelFactor;
        return (int)Math.Clamp(Math.Ceiling(seconds), MinSeconds, MaxSeconds);
    }

    /// <summary>
    /// <paramref name="seconds"/> stretched by <paramref name="factor"/> and held to
    /// <c>[MinSeconds, MaxSeconds]</c>: the deadline of the out-of-memory retry, which renders one
    /// frame at a time and so runs slower than the render it repeats.
    /// </summary>
    public int Stretch(int seconds, double factor) =>
        (int)Math.Clamp(Math.Ceiling(Math.Max(1, seconds) * Math.Max(1.0, factor)), MinSeconds, MaxSeconds);

    private static int FrameCount(CompositionInfo composition, IReadOnlyList<string>? args)
    {
        if (composition.IsStill)
            return 1;
        string? range = Value(args, "--frames");
        if (range is null)
            return composition.DurationInFrames;

        string[] parts = range.Split('-', 2);
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start))
            return composition.DurationInFrames;
        if (parts.Length == 1)
            return 1;
        int end = parts[1].Length == 0
            ? composition.DurationInFrames - 1
            : int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) ? e : composition.DurationInFrames - 1;
        return Math.Clamp(end - start + 1, 1, composition.DurationInFrames);
    }

    private static double Scale(IReadOnlyList<string>? args)
    {
        string? value = Value(args, "--scale");
        return value is not null
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
               && s > 0
            ? Math.Min(s, 16)
            : 1.0;
    }

    private static string? Value(IReadOnlyList<string>? args, string flag)
    {
        if (args is null)
            return null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a.StartsWith(flag + "=", StringComparison.Ordinal))
                return a[(flag.Length + 1)..];
            if (a == flag && i + 1 < args.Count)
                return args[i + 1];
        }
        return null;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : fallback;
}
