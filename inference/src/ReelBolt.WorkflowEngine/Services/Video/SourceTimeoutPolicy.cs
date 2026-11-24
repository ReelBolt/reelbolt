using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Lets a per-process ffmpeg/ffprobe deadline follow the source it is working on. The configured
/// <c>AnalyzeTimeoutSeconds</c>/<c>CompileTimeoutSeconds</c> are fine for a phone clip but kill a
/// one-hour 4K recording mid-decode. The deadline becomes
/// <c>max(configured, sourceSeconds × min(4, pixels / 1920×1080) × fps factor (fps/30, 1..4) × bitrate factor (bits per second / 33 Mbps, 1..8) × Factor)</c>, capped at six hours, at configured +
/// stagedBytes / 300 KB/s, and never below the configured value. A short or small source therefore keeps today's deadline unchanged.
/// </summary>
/// <remarks>
/// The source is announced once with <see cref="Begin"/> (after the probe, or from the analysis the
/// compile reads); every deadline computed below that point in the same async flow sees it. With no
/// scope the configured value is returned untouched.
/// </remarks>
public static class SourceTimeoutPolicy
{
    /// <summary>
    /// Wall-clock seconds budgeted per source second at 1080p. Measured: 960 s of 4K HEVC took 1310 s on a
    /// loaded 8-CPU limit, about 0.34 s per source second per 1080p-ratio, so 1.0 is roughly a 3x margin.
    /// </summary>
    public const double Factor = 1.0;

    /// <summary>Six hours, the ceiling the sandbox exec limit imposes.</summary>
    public const int MaxSeconds = 6 * 3600;

    /// <summary>The pixel ratio never counts above 4K (4.0 x 1080p), whatever the header claims.</summary>
    public const double MaxPixelRatio = 4.0;

    /// <summary>
    /// A deadline may not exceed configured + stagedBytes / this rate. Real footage runs ~3 MB/s; a tiny file
    /// that claims a long 4K duration therefore gets almost no extra time.
    /// </summary>
    public const double MinBytesPerSecond = 300_000;

    /// <summary>Frame rates count relative to this; the fps factor is fps / this, clamped to 1..<see cref="MaxFpsFactor"/>.</summary>
    public const double ReferenceFps = 30.0;

    public const double MaxFpsFactor = 4.0;

    /// <summary>Bits per second a typical phone clip runs at; the bitrate factor is bitrate / this, clamped to 1..<see cref="MaxBitrateFactor"/>.</summary>
    public const double ReferenceBitsPerSecond = 33_000_000;

    public const double MaxBitrateFactor = 8.0;

    private const double ReferencePixels = 1920.0 * 1080.0;

    private sealed record Scope(double DurationSec, double Ratio, double FpsFactor, double BitrateFactor, long Bytes, int ShortSide, Scope? Parent);

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>
    /// Announces the source the deadlines below should follow. Header values come from the uploader, so they
    /// are clamped (duration to <paramref name="maxSeconds"/>, pixel ratio to 4K) and the extra time is bounded
    /// by the staged size. Call it only AFTER the source passed its size and duration limits. Unknown size
    /// (<paramref name="stagedBytes"/> &lt;= 0) means no scaling.
    /// </summary>
    public static IDisposable Begin(
        double durationSec, int width, int height, long stagedBytes, int maxSeconds = VideoSourceLimits.DefaultMaxSeconds, double fps = 0)
    {
        Scope? previous = Current.Value;
        double pixels = Math.Max(0, width) * (double)Math.Max(0, height);
        double ratio = pixels > 0 ? Math.Min(MaxPixelRatio, pixels / ReferencePixels) : 1.0;
        double duration = Math.Clamp(durationSec, 0, Math.Max(1, maxSeconds));
        double fpsFactor = double.IsFinite(fps) && fps > 0 ? Math.Clamp(fps / ReferenceFps, 1.0, MaxFpsFactor) : 1.0;
        // Real staged bytes over the header duration (clamped to the source limit): a header cannot claim a bitrate the bytes do not have.
        double bitsPerSecond = duration > 0 ? Math.Max(0, stagedBytes) * 8.0 / duration : 0;
        double bitrateFactor = Math.Clamp(bitsPerSecond / ReferenceBitsPerSecond, 1.0, MaxBitrateFactor);
        Current.Value = new Scope(duration, ratio, fpsFactor, bitrateFactor, Math.Max(0, stagedBytes), Math.Min(Math.Max(0, width), Math.Max(0, height)), previous);
        return new Restore(previous);
    }

    /// <summary>Frames per second from a probe's rational rate; 0 when unknown.</summary>
    public static double FpsOf(int fpsNum, int fpsDen) => fpsNum > 0 && fpsDen > 0 ? fpsNum / (double)fpsDen : 0;

    /// <summary>The length of a local file, or 0 (no scaling) when it cannot be read; never throws.</summary>
    public static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>The scaled deadline in seconds for <paramref name="configuredSeconds"/>.</summary>
    public static int Seconds(int configuredSeconds)
    {
        double best = configuredSeconds;
        for (Scope? scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (scope.DurationSec <= 0 || scope.Bytes <= 0)
                continue;
            double scaled = Math.Ceiling(scope.DurationSec * scope.Ratio * scope.FpsFactor * scope.BitrateFactor * Factor);
            double byteCap = configuredSeconds + scope.Bytes / MinBytesPerSecond;
            best = Math.Max(best, Math.Min(Math.Min(scaled, byteCap), MaxSeconds));
        }

        return (int)Math.Max(configuredSeconds, Math.Floor(best));
    }

    /// <summary>
    /// True when the announced source (or any enclosing one) is above the analysis-proxy threshold: short side
    /// above <see cref="AnalysisProxy.MinShortSide"/> and at least <paramref name="minDurationSec"/> seconds.
    /// Such passes take the runner's single heavy lane. 0 disables the lane.
    /// </summary>
    public static bool IsHeavy(int minDurationSec)
    {
        if (minDurationSec <= 0)
            return false;
        for (Scope? scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (scope.ShortSide > AnalysisProxy.MinShortSide && scope.DurationSec >= minDurationSec)
                return true;
        }

        return false;
    }

    public static int AnalyzeSeconds(VideoEditingOptions options) => Seconds(options.AnalyzeTimeoutSeconds);

    public static int CompileSeconds(VideoEditingOptions options) => Seconds(options.CompileTimeoutSeconds);

    public static TimeSpan Analyze(VideoEditingOptions options) => TimeSpan.FromSeconds(AnalyzeSeconds(options));

    public static TimeSpan Compile(VideoEditingOptions options) => TimeSpan.FromSeconds(CompileSeconds(options));

    private sealed class Restore(Scope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
