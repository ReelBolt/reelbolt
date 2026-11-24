using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

public sealed class FfmpegSilenceDetector : ISilenceDetector
{
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfmpegSilenceDetector> _logger;

    public FfmpegSilenceDetector(
        IVideoToolRunner runner, IOptions<VideoEditingOptions> options, ILogger<FfmpegSilenceDetector> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectAsync(
        string localFilePath,
        double thresholdDb,
        double minSilenceSeconds,
        double? totalDurationSec,
        CancellationToken ct) =>
        DetectAsync(
            LocalMediaWorkspace.ForLocalFile(localFilePath, _runner, _logger),
            Path.GetFileName(localFilePath),
            thresholdDb,
            minSilenceSeconds,
            totalDurationSec,
            ct);

    public async Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectAsync(
        IMediaWorkspace workspace,
        string virtualPath,
        double thresholdDb,
        double minSilenceSeconds,
        double? totalDurationSec,
        CancellationToken ct)
    {
        string mediaPath = await workspace.MaterializeAsync(virtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildSilenceDetectArgs(mediaPath, thresholdDb, minSilenceSeconds);
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg silencedetect failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new InvalidOperationException(
                $"ffmpeg silencedetect failed for '{virtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }

        return SilenceDetectOutputParser.Parse(result.StdErr, totalDurationSec);
    }
}
