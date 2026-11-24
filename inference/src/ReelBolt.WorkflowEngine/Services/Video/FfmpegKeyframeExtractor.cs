using Microsoft.Extensions.Options;

using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

public sealed class FfmpegKeyframeExtractor : IKeyframeExtractor
{
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfmpegKeyframeExtractor> _logger;

    public FfmpegKeyframeExtractor(
        IVideoToolRunner runner, IOptions<VideoEditingOptions> options, ILogger<FfmpegKeyframeExtractor> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public Task ExtractKeyframeAsync(
        string inputVideoPath, string outputJpgPath, double atSec, int maxWidth, CancellationToken ct) =>
        ExtractKeyframeAsync(Local(inputVideoPath, outputJpgPath, out string outputVirtualPath),
            Path.GetFileName(inputVideoPath), outputVirtualPath, atSec, maxWidth, ct);

    public Task ExtractContactSheetAsync(
        string inputVideoPath, string outputJpgPath, IReadOnlyList<double> atSecs, int maxWidth, CancellationToken ct) =>
        ExtractContactSheetAsync(Local(inputVideoPath, outputJpgPath, out string outputVirtualPath),
            Path.GetFileName(inputVideoPath), outputVirtualPath, atSecs, maxWidth, ct);

    public Task ExtractCroppedKeyframeAsync(
        string inputVideoPath, string outputJpgPath, double atSec,
        double x, double y, double width, double height, int maxWidth, CancellationToken ct) =>
        ExtractCroppedKeyframeAsync(Local(inputVideoPath, outputJpgPath, out string outputVirtualPath),
            Path.GetFileName(inputVideoPath), outputVirtualPath, atSec, x, y, width, height, maxWidth, ct);

    public async Task ExtractKeyframeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double atSec,
        int maxWidth,
        CancellationToken ct)
    {
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildKeyframeArgs(inputPath, outputPath, atSec, maxWidth,
            await HdrToneMap.ResolveKindForFileAsync(workspace, inputPath, ct).ConfigureAwait(false));
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg keyframe extraction failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new InvalidOperationException(
                $"ffmpeg keyframe extraction failed for '{inputVirtualPath}' at {atSec}s " +
                $"(exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }
    }

    public Task ExtractContactSheetAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        IReadOnlyList<double> atSecs,
        int maxWidth,
        CancellationToken ct) =>
        ExtractContactSheetAsync(workspace, inputVirtualPath, outputVirtualPath, atSecs, maxWidth, Timeout.InfiniteTimeSpan, ct);

    public async Task ExtractContactSheetAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        IReadOnlyList<double> atSecs,
        int maxWidth,
        TimeSpan maxRunTime,
        CancellationToken ct)
    {
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildContactSheetArgs(inputPath, outputPath, atSecs, maxWidth,
            await HdrToneMap.ResolveKindForFileAsync(workspace, inputPath, ct).ConfigureAwait(false));
        // The caller's run time only ever shortens the usual one.
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);
        if (maxRunTime > TimeSpan.Zero && maxRunTime < timeout)
            timeout = maxRunTime;

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg contact-sheet extraction failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new InvalidOperationException(
                $"ffmpeg contact-sheet extraction failed for '{inputVirtualPath}' " +
                $"(exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }
    }

    public async Task ExtractCroppedKeyframeAsync(
        IMediaWorkspace workspace,
        string inputVirtualPath,
        string outputVirtualPath,
        double atSec,
        double x,
        double y,
        double width,
        double height,
        int maxWidth,
        CancellationToken ct)
    {
        string inputPath = await workspace.MaterializeAsync(inputVirtualPath, ct).ConfigureAwait(false);
        string outputPath = await workspace.MaterializeAsync(outputVirtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildCroppedKeyframeArgs(inputPath, outputPath, atSec, x, y, width, height, maxWidth,
            await HdrToneMap.ResolveKindForFileAsync(workspace, inputPath, ct).ConfigureAwait(false));
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfmpegAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "ffmpeg cropped keyframe extraction failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);
            throw new InvalidOperationException(
                $"ffmpeg cropped keyframe extraction failed for '{inputVirtualPath}' at {atSec}s " +
                $"(exitCode={result.ExitCode}, timedOut={result.TimedOut}).");
        }
    }

    /// <summary>
    /// Adapts the absolute-path overloads onto the workspace seam: one workspace rooted at the
    /// input's directory (the session directory both paths live in), with the output named relative
    /// to it. <see cref="LocalMediaWorkspace.VirtualPathOf"/> refuses an output outside that
    /// directory rather than silently addressing a same-named file inside it.
    /// </summary>
    private LocalMediaWorkspace Local(string inputVideoPath, string outputJpgPath, out string outputVirtualPath)
    {
        LocalMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(inputVideoPath, _runner, _logger);
        outputVirtualPath = LocalMediaWorkspace.VirtualPathOf(outputJpgPath, workspace.RootPath);
        return workspace;
    }
}
