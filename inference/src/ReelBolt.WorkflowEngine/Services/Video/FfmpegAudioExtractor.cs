using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

public sealed class FfmpegAudioExtractor : IAudioExtractor
{
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfmpegAudioExtractor> _logger;

    public FfmpegAudioExtractor(
        IVideoToolRunner runner, IOptions<VideoEditingOptions> options, ILogger<FfmpegAudioExtractor> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public Task ExtractWavAsync(string inputVideoPath, string outputWavPath, CancellationToken ct) =>
        RunLocalAsync(inputVideoPath, outputWavPath, startSeconds: null, durationSeconds: null, ct);

    public Task ExtractWavRangeAsync(
        string inputVideoPath, string outputWavPath, double startSec, double endSec, CancellationToken ct)
    {
        if (endSec <= startSec)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endSec), endSec, $"endSec ({endSec}) must be greater than startSec ({startSec}).");
        }

        return RunLocalAsync(inputVideoPath, outputWavPath, startSec, endSec - startSec, ct);
    }

    public Task ExtractWavAsync(
        IMediaWorkspace workspace, string inputVirtualPath, string outputVirtualPath, CancellationToken ct) =>
        RunAsync(workspace, inputVirtualPath, outputVirtualPath, startSeconds: null, durationSeconds: null, ct);

    public Task ExtractWavRangeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double startSec,
        double endSec,
        CancellationToken ct)
    {
        if (endSec <= startSec)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endSec), endSec, $"endSec ({endSec}) must be greater than startSec ({startSec}).");
        }

        return RunAsync(workspace, inputVirtualPath, outputVirtualPath, startSec, endSec - startSec, ct);
    }

    /// <summary>
    /// Adapts the two-absolute-path overloads onto the workspace seam. Input and output sit in the
    /// same scratch directory in every current caller, so one workspace rooted at the input's
    /// directory covers both; <see cref="LocalMediaWorkspace.VirtualPathOf"/> fails loudly rather
    /// than silently pointing at a same-named file elsewhere if that ever stops being true.
    /// </summary>
    private Task RunLocalAsync(
        string inputVideoPath,
        string outputWavPath,
        double? startSeconds,
        double? durationSeconds,
        CancellationToken ct)
    {
        LocalMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(inputVideoPath, _runner, _logger);
        return RunAsync(
            workspace,
            Path.GetFileName(inputVideoPath),
            LocalMediaWorkspace.VirtualPathOf(outputWavPath, workspace.RootPath),
            startSeconds,
            durationSeconds,
            ct);
    }

    private async Task RunAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double? startSeconds,
        double? durationSeconds,
        CancellationToken ct)
    {
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildExtractAudioArgs(
            inputPath, outputPath, startSeconds, durationSeconds);
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg audio extraction failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new InvalidOperationException(
                $"ffmpeg audio extraction failed for '{inputVirtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }
    }
}
