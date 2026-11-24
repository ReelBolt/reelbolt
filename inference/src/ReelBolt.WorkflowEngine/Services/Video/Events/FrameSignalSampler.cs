using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>Receives the sampled frames one at a time, in order. The span is only valid during the call.</summary>
public interface IFrameConsumer
{
    void OnFrame(int index, ReadOnlySpan<byte> gray);
}

/// <summary>What one sampling pass produced. <see cref="Divisor"/> is 1 unless the source ran above 60 fps.</summary>
/// <param name="RateUnknown">True when the check for a variable frame rate failed, so the pass read the file as constant-rate without knowing it is.</param>
public sealed record FrameSignalInfo(double Fps, int Divisor, int FrameCount, int Width, int Height, bool RateUnknown = false);

/// <summary>
/// One ffmpeg pass that decodes every source frame (effective rate capped at 60 fps) to
/// <see cref="Width"/>x<see cref="Height"/> 8-bit gray with area scaling (no frame is duplicated across timestamp gaps; the output is capped by the probed duration), and streams the frames to a consumer one
/// at a time. Goes through the workspace's ffmpeg gate like every other pass, and reads the result back in
/// frame-sized chunks, so memory stays at a few frames however long the video is. Pass either the source or its
/// analysis proxy: the pass only needs the frame rate of the file it decodes.
/// </summary>
public sealed class FrameSignalSampler
{
    public const int Width = 160;
    public const int Height = 90;
    public const int MaxFps = 60;

    private readonly VideoEditingOptions _options;
    private readonly ILogger<FrameSignalSampler> _logger;
    private readonly IScratchDiskGuard? _scratchGuard;

    public FrameSignalSampler(IOptions<VideoEditingOptions> options, ILogger<FrameSignalSampler> logger, IScratchDiskGuard? scratchGuard = null)
    {
        _scratchGuard = scratchGuard;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// The rate to decode at: the source rate when it is at most <see cref="MaxFps"/>, otherwise the rate divided
    /// by the smallest integer that brings it to the cap (so frames are dropped on a regular grid, never resampled).
    /// </summary>
    public static (double Fps, int Divisor) PlanRate(double sourceFps)
    {
        if (sourceFps <= 0)
            return (30.0, 1);
        int divisor = 1;
        while (sourceFps / divisor > MaxFps + 0.5)
            divisor++;
        return (sourceFps / divisor, divisor);
    }

    /// <summary>Bytes of one decoded frame in the scratch file.</summary>
    public const int FrameBytes = Width * Height;

    /// <summary>Byte cap of the one workspace read that brings a remote session's gray frames back (256 MiB).</summary>
    public const int MaxRemoteGrayBytes = 256 * 1024 * 1024;

    /// <summary>Most frames one remote-session pass samples, so its output always fits <see cref="MaxRemoteGrayBytes"/>.</summary>
    public const long MaxRemoteFrames = MaxRemoteGrayBytes / FrameBytes;

    /// <summary>Duration assumed when the probe gave none.</summary>
    public const double FallbackDurationSec = 600;

    /// <summary>
    /// The most frames the pass may write: the duration (clamped to <paramref name="maxSeconds"/>, so a forged
    /// container duration cannot overflow) times the planned rate, plus a small margin. Always positive and bounded.
    /// </summary>
    public static long PlanMaxFrames(double durationSec, double plannedFps, double maxSeconds = SourceTimeoutPolicy.MaxSeconds)
    {
        double limit = maxSeconds > 0 && double.IsFinite(maxSeconds) ? maxSeconds : SourceTimeoutPolicy.MaxSeconds;
        double seconds = durationSec > 0 && double.IsFinite(durationSec) ? Math.Min(durationSec, limit) : Math.Min(FallbackDurationSec, limit);
        double fps = double.IsFinite(plannedFps) ? Math.Clamp(plannedFps, 1.0, MaxFps) : MaxFps;
        return (long)Math.Ceiling(seconds * fps * 1.02) + 8;
    }

    /// <summary>True when the stream's average rate departs from its declared rate by more than 0.2%: frame index then drifts from time.</summary>
    public static bool IsVariableFrameRate(double avgFps, double declaredFps) =>
        declaredFps > 0 && (avgFps <= 0 || Math.Abs(avgFps - declaredFps) / declaredFps > 0.002);

    /// <summary>
    /// The argv of the sampling pass. A constant-rate source is passed through (every <paramref name="divisor"/>-th
    /// decoded frame kept), so the frame index is the sampling grid. A variable-rate source (pass its rational in
    /// <paramref name="retimeNum"/>/<paramref name="retimeDen"/>) is retimed with the fps filter so the index follows
    /// time. Either way the output is capped by <paramref name="maxFrames"/> frames and the matching byte count.
    /// </summary>
    public static string[] BuildArgs(string inputPath, string outputPath, int divisor, int threads, long maxFrames, int retimeNum = 0, int retimeDen = 1)
    {
        string head;
        string mode;
        if (retimeNum > 0 && retimeDen > 0)
        {
            head = $"fps={FfmpegArgvFormat.Number(retimeNum)}/{FfmpegArgvFormat.Number(retimeDen * Math.Max(divisor, 1))},";
            mode = "cfr";
        }
        else
        {
            head = divisor > 1 ? $"select='not(mod(n,{FfmpegArgvFormat.Number(divisor)}))'," : "";
            mode = "passthrough";
        }

        return
        [
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-threads", FfmpegArgvFormat.Number(threads),
            "-i", inputPath,
            "-an", "-sn",
            "-fps_mode", mode,
            "-vf", $"{head}scale={Width}:{Height}:flags=area,format=gray",
            "-frames:v", maxFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-fs", (maxFrames * FrameBytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "rawvideo", "-pix_fmt", "gray",
            outputPath
        ];
    }

    private static double Rate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        string[] parts = text.Split('/');
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n))
            return 0;
        double d = 1;
        if (parts.Length > 1 && !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
            return 0;
        return d > 0 && double.IsFinite(n / d) ? n / d : 0;
    }

    /// <summary>True for a variable frame rate, false for a constant one, null when the probe failed and nothing is known.</summary>
    private async Task<bool?> IsVfrAsync(IMediaWorkspace workspace, string inputPath, CancellationToken ct)
    {
        try
        {
            VideoToolResult r = await workspace.RunFfprobeAsync(
                ["-v", "error", "-select_streams", "v:0", "-protocol_whitelist", "file", "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
                 "-show_entries", "stream=avg_frame_rate,r_frame_rate", "-of", "default=nw=1", inputPath],
                TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
            if (!r.Succeeded)
                return null;
            double avg = 0, declared = 0;
            foreach (string line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.StartsWith("avg_frame_rate=", StringComparison.Ordinal))
                    avg = Rate(t["avg_frame_rate=".Length..]);
                else if (t.StartsWith("r_frame_rate=", StringComparison.Ordinal))
                    declared = Rate(t["r_frame_rate=".Length..]);
            }

            return IsVariableFrameRate(avg, declared);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<FrameSignalInfo> SampleAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        int fpsNum,
        int fpsDen,
        double durationSec,
        IFrameConsumer consumer,
        CancellationToken ct)
    {
        double sourceFps = SourceTimeoutPolicy.FpsOf(fpsNum, fpsDen);
        (double fps, int divisor) = PlanRate(sourceFps);
        long maxFrames = PlanMaxFrames(durationSec, fps, _scratchGuard?.MaxSourceSeconds ?? SourceTimeoutPolicy.MaxSeconds);
        bool remote = workspace.IsRemoteSession;
        if (remote && maxFrames > MaxRemoteFrames)
        {
            // #1206: a remote session's frames come back through one bounded workspace read, so the pass is held to
            // what that read may carry — loudly, never as a silent short read.
            _logger.LogWarning(
                "events_frames_capped: remote session frame-signal pass limited to {Kept} of {Planned} frames ({Bytes}-byte read cap)",
                MaxRemoteFrames, maxFrames, MaxRemoteGrayBytes);
            maxFrames = MaxRemoteFrames;
        }

        using ScratchLeases scratch = new();
        if (_scratchGuard is not null && workspace is LocalMediaWorkspace)
            scratch.Add(await _scratchGuard.ReserveAsync(maxFrames * FrameBytes, ct).ConfigureAwait(false));

        string outputVirtualPath = $"events-gray-{Guid.NewGuid():N}.raw";
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        int count = 0;
        bool rateUnknown = false;
        try
        {
            // A probe that failed reads the file as constant-rate, as it always did, and says so in the result.
            bool? probedVfr = await IsVfrAsync(workspace, inputPath, ct).ConfigureAwait(false);
            rateUnknown = probedVfr is null;
            bool vfr = probedVfr ?? false;
            string[] args = vfr
                ? BuildArgs(inputPath, outputPath, divisor, _options.FfmpegThreads, maxFrames, fpsNum > 0 && fpsDen > 0 ? fpsNum : 30, fpsNum > 0 && fpsDen > 0 ? fpsDen : 1)
                : BuildArgs(inputPath, outputPath, divisor, _options.FfmpegThreads, maxFrames);
            VideoToolResult result = await workspace.RunFfmpegAsync(args, SourceTimeoutPolicy.Analyze(_options), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "ffmpeg frame-signal sampling failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                    result.ExitCode, result.TimedOut, result.StdErr);
                throw VideoToolTimedOutException.For(
                    $"ffmpeg frame-signal sampling failed for '{inputVirtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).",
                    result.TimedOut, _options.AnalysisProxyMinDurationSeconds);
            }

            byte[] buffer = new byte[FrameBytes];
            // #1206: on a remote session outputPath is the session-relative NAME, not a host file; read it through the seam.
            await using Stream stream = remote
                ? new MemoryStream(await workspace.ReadAsync(outputVirtualPath, (int)(maxFrames * FrameBytes), ct).ConfigureAwait(false), writable: false)
                : new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            while (count < maxFrames)
            {
                int read = 0;
                while (read < FrameBytes)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read, FrameBytes - read), ct).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    read += n;
                }

                if (read < FrameBytes)
                    break;
                consumer.OnFrame(count++, buffer);
            }
        }
        finally
        {
            // The media-session op set has no delete: a remote session's scratch file stays in the session
            // directory and goes with it. A host File.Delete on a session NAME would hit the engine's cwd.
            if (!remote)
                TryDelete(outputPath);
        }

        return new FrameSignalInfo(fps, divisor, count, Width, Height, rateUnknown);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the scratch space is removed with the session anyway.
        }
    }
}
