using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

public sealed class FfmpegShotDetector : IShotDetector
{
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfmpegShotDetector> _logger;

    public FfmpegShotDetector(
        IVideoToolRunner runner, IOptions<VideoEditingOptions> options, ILogger<FfmpegShotDetector> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectShotsAsync(
        string localFilePath,
        double sceneThreshold,
        double totalDurationSec,
        CancellationToken ct) =>
        DetectShotsAsync(
            LocalMediaWorkspace.ForLocalFile(localFilePath, _runner, _logger),
            Path.GetFileName(localFilePath),
            sceneThreshold,
            totalDurationSec,
            ct);

    public async Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectShotsAsync(
        IMediaWorkspace workspace,
        string virtualPath,
        double sceneThreshold,
        double totalDurationSec,
        CancellationToken ct)
    {
        string mediaPath = await workspace.MaterializeAsync(virtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildShotDetectArgs(mediaPath, sceneThreshold, _options.FfmpegThreads);
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg shot detection failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw VideoToolTimedOutException.For(
                $"ffmpeg shot detection failed for '{virtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).", result.TimedOut, _options.AnalysisProxyMinDurationSeconds);
        }

        IReadOnlyList<double> boundaries = ShowinfoOutputParser.ParsePtsTimes(result.StdErr);
        return ShowinfoOutputParser.BuildShotSpans(boundaries, totalDurationSec);
    }
}
