using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

public sealed class FfprobeMediaProbe : IMediaProbe
{
    private readonly IVideoToolRunner _runner;
    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfprobeMediaProbe> _logger;

    public FfprobeMediaProbe(IVideoToolRunner runner, IOptions<VideoEditingOptions> options, ILogger<FfprobeMediaProbe> logger)
    {
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public Task<MediaProbeResult> ProbeAsync(string localFilePath, CancellationToken ct) =>
        ProbeAsync(
            LocalMediaWorkspace.ForLocalFile(localFilePath, _runner, _logger),
            Path.GetFileName(localFilePath),
            ct);

    public async Task<MediaProbeResult> ProbeAsync(
        IMediaWorkspace workspace, string virtualPath, CancellationToken ct)
    {
        // The only place a virtual path becomes something ffprobe can be handed. In a local
        // workspace this is the same absolute path the old code built with scratch.GetPath; in a
        // remote one it is the virtual path itself, resolved by the worker inside its session.
        string mediaPath = await workspace.MaterializeAsync(virtualPath, ct).ConfigureAwait(false);

        string[] args = FfmpegArgvBuilder.BuildProbeArgs(mediaPath);
        TimeSpan timeout = SourceTimeoutPolicy.Analyze(_options);

        VideoToolResult result = await workspace.RunFfprobeAsync(args, timeout, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("ffprobe failed (exitCode={ExitCode}, timedOut={TimedOut}): {StdErr}",
                result.ExitCode, result.TimedOut, result.StdErr);

            // A playlist, a list file or another format that is not media: the caller's own input, told apart from a
            // file ffprobe simply could not read (security ENG-3). A file no whitelisted demuxer recognises (a TGA or PAM
            // picture, a playlist under a media name) is the same case.
            if (UserMediaInputFormats.IsWhitelistRefusal(result.StdErr))
                throw new MediaFormatNotAllowedException(virtualPath);
            if (UserMediaInputFormats.IsUnrecognisedContent(result.StdErr))
                throw new MediaFormatNotAllowedException(virtualPath, unrecognised: true);

            throw new InvalidOperationException(
                $"ffprobe failed for '{virtualPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}). stderr: {Truncate(result.StdErr)}");
        }

        return FfprobeOutputParser.Parse(result.StdOut);
    }

    private static string Truncate(string value, int max = 2000) =>
        value.Length <= max ? value : value[^max..];
}
