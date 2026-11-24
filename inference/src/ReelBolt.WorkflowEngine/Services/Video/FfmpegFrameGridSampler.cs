using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

public sealed class FfmpegFrameGridSampler : IFrameGridSampler
{
    /// <summary>
    /// "Use the compiled-in default" fallback for <c>maxSampleFrames &lt;= 0</c> — mirrors
    /// <see cref="ReelBolt.Shared.Workflows.VideoAnalyzeStepConfig.MaxVisualSampleFrames"/>'s own
    /// default, so an explicit 0/negative value behaves the same as leaving the field unset rather
    /// than disabling the sample-count cap entirely (see docs/video-editing.md, Item D cleanup).
    /// </summary>
    private const int DefaultMaxVisualSampleFrames = 4000;

    /// <summary>Session-relative name of the RGB grid buffer — the same name the scratch path always used.</summary>
    private const string GridVirtualPath = "grid.rgb";

    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfmpegFrameGridSampler> _logger;
    private readonly IScratchDiskGuard? _scratchGuard;

    public FfmpegFrameGridSampler(
        IVideoToolRunner runner, IOptions<VideoEditingOptions> options, ILogger<FfmpegFrameGridSampler> logger,
        IScratchDiskGuard? scratchGuard = null)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
        _scratchGuard = scratchGuard;
    }

    public Task<FrameGridResult> SampleAsync(
        string localFilePath,
        VideoScratchSpace scratch,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct) =>
        SampleAsync(
            LocalMediaWorkspace.Create(scratch, _runner, logger: _logger),
            LocalMediaWorkspace.VirtualPathOf(localFilePath, scratch.DirectoryPath),
            sampleFps,
            gridWidth,
            gridHeight,
            totalDurationSec,
            maxSampleFrames,
            ct);

    public Task<FrameGridResult> SampleGrayAsync(
        string localFilePath,
        VideoScratchSpace scratch,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct) =>
        SampleGrayAsync(
            LocalMediaWorkspace.Create(scratch, _runner, logger: _logger),
            LocalMediaWorkspace.VirtualPathOf(localFilePath, scratch.DirectoryPath),
            sampleFps,
            gridWidth,
            gridHeight,
            totalDurationSec,
            maxSampleFrames,
            ct);

    public Task<FrameGridResult> SampleAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct) =>
        SampleAsync(
            workspace, inputVirtualPath, sampleFps, gridWidth, gridHeight, totalDurationSec,
            maxSampleFrames, GridChroma.Default, ct);

    public async Task<FrameGridResult> SampleAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        GridChroma chroma,
        CancellationToken ct)
    {
        double effectiveFps = ComputeEffectiveFps(sampleFps, totalDurationSec, maxSampleFrames);

        // Goes through the workspace's own path resolution, like every other session-file path in
        // this codebase, rather than deriving a sibling path from the input's directory manually —
        // see docs/video-editing.md, Item J cleanup. Safe either way in practice (the source is
        // already in the session directory), but this keeps the containment assertion consistent
        // everywhere.
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(GridVirtualPath, ct).ConfigureAwait(false);

        // The same frame cap as the gray pass: the rate above is worked out from the duration the caller was told, so a
        // file that runs longer than that (or has no length) would otherwise write frames until the timeout.
        string[] args = FfmpegArgvBuilder.BuildGridSampleArgs(
            inputPath, outputPath, effectiveFps, gridWidth, gridHeight, _options.FfmpegThreads, chroma, PlanMaxGrayFrames(maxSampleFrames));
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg grid sampling failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw VideoToolTimedOutException.For(
                $"ffmpeg grid sampling failed for '{inputVirtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).", result.TimedOut, _options.AnalysisProxyMinDurationSeconds);
        }

        byte[] pixelData = await workspace.ReadAsync(GridVirtualPath, ct).ConfigureAwait(false);
        int frameSize = gridWidth * gridHeight * 3;
        int frameCount = frameSize > 0 ? pixelData.Length / frameSize : 0;

        return new FrameGridResult(pixelData, gridWidth, gridHeight, effectiveFps, frameCount);
    }

    /// <summary>
    /// Same pass, decoded to 8-bit grayscale. Written as a separate method rather than sharing a
    /// private helper with <see cref="SampleAsync(IMediaWorkspace, string, double, int, int, double, int, CancellationToken)"/>
    /// so each of the two existing call shapes stays exactly as it was.
    /// <para>
    /// Bounded like the events pass (<see cref="Events.FrameSignalSampler"/>): the output is capped at
    /// <see cref="PlanMaxGrayFrames"/> frames, the scratch space for it is reserved before ffmpeg runs
    /// (local workspace only), and the scratch file is deleted once it has been read.
    /// </para>
    /// </summary>
    public async Task<FrameGridResult> SampleGrayAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        double sampleFps,
        int gridWidth,
        int gridHeight,
        double totalDurationSec,
        int maxSampleFrames,
        CancellationToken ct)
    {
        double effectiveFps = ComputeEffectiveFps(sampleFps, totalDurationSec, maxSampleFrames);
        long maxFrames = PlanMaxGrayFrames(maxSampleFrames);
        long frameBytes = Math.Max(1L, (long)gridWidth * gridHeight);
        bool local = workspace is LocalMediaWorkspace;

        using ScratchLeases leases = new();
        if (_scratchGuard is not null && local)
            leases.Add(await _scratchGuard.ReserveAsync(maxFrames * frameBytes, ct).ConfigureAwait(false));

        string outputVirtualPath = $"gray-{Guid.NewGuid():N}.raw";

        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildGraySampleArgs(inputPath, outputPath, effectiveFps, gridWidth, gridHeight, _options.FfmpegThreads, maxFrames);
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        try
        {
            VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "ffmpeg gray sampling failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                    result.ExitCode, result.TimedOut, result.StdErr);
                throw VideoToolTimedOutException.For(
                    $"ffmpeg gray sampling failed for '{inputVirtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).", result.TimedOut, _options.AnalysisProxyMinDurationSeconds);
            }

            byte[] pixelData = await workspace.ReadAsync(outputVirtualPath, ct).ConfigureAwait(false);
            int frameSize = gridWidth * gridHeight;
            int frameCount = frameSize > 0 ? pixelData.Length / frameSize : 0;
            return new FrameGridResult(pixelData, gridWidth, gridHeight, effectiveFps, frameCount);
        }
        finally
        {
            // The frames are in memory now (or the pass failed): the file is only weight on the scratch volume.
            if (local)
                TryDelete(outputPath);
        }
    }

    /// <summary>
    /// The most frames the gray pass (and the colour grid pass) may write: what the caller asked for (<paramref name="maxSampleFrames"/>,
    /// or the default when it is not positive) plus a small margin, so an honest file never loses a frame and a
    /// file that lies about its length cannot grow the buffer past what the caller sized it for.
    /// </summary>
    internal static long PlanMaxGrayFrames(int maxSampleFrames)
    {
        int effectiveMax = maxSampleFrames > 0 ? maxSampleFrames : DefaultMaxVisualSampleFrames;
        return (long)Math.Ceiling(effectiveMax * 1.02) + 8;
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

    /// <summary>
    /// <c>effectiveFps = min(sampleFps, effectiveMax / totalDurationSec)</c>, where
    /// <paramref name="maxSampleFrames"/> &lt;= 0 falls back to
    /// <see cref="DefaultMaxVisualSampleFrames"/> rather than disabling the cap entirely (Item D
    /// cleanup — previously, <c>MaxVisualSampleFrames: 0</c>/negative skipped the clamp altogether
    /// instead of being treated as "use the default"). Always clamped to a minimum of 0.01 so a
    /// pathological config can never reach ffmpeg as <c>fps&lt;=0</c>. Factored out as a pure,
    /// directly-unit-testable method — the rest of <see cref="SampleAsync(IMediaWorkspace, string, double, int, int, double, int, CancellationToken)"/>
    /// invokes ffmpeg and isn't independently testable without it.
    /// </summary>
    internal static double ComputeEffectiveFps(double sampleFps, double totalDurationSec, int maxSampleFrames)
    {
        int effectiveMax = maxSampleFrames > 0 ? maxSampleFrames : DefaultMaxVisualSampleFrames;

        double effectiveFps = sampleFps;
        if (totalDurationSec > 0)
        {
            double capFps = effectiveMax / totalDurationSec;
            effectiveFps = Math.Min(effectiveFps, capFps);
        }

        return Math.Max(effectiveFps, 0.01); // never let a pathological config reach ffmpeg as fps<=0
    }
}
