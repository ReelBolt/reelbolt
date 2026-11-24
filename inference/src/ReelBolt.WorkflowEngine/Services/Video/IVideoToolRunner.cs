namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>Result of one ffmpeg/ffprobe process invocation.</summary>
public sealed record VideoToolResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Scheduling class of one ffmpeg/ffprobe invocation. Editor media derivatives (proxy, filmstrip,
/// waveform peaks, keyframe index) are <see cref="Low"/>: they are background convenience work,
/// and they must never make a compile, a shot-detection pass or a render wait behind them.
/// </summary>
public enum VideoToolPriority
{
    /// <summary>The default. A compile, an analysis pass, a render — anything a user is waiting on.</summary>
    Normal,

    /// <summary>
    /// Editor derivatives. Admitted only when no <see cref="Normal"/> waiter is queued, and
    /// additionally capped by <see cref="VideoEditingOptions.DerivativeMaxConcurrentJobs"/> so
    /// derivatives cannot consume the whole budget even when nothing else is running.
    /// </summary>
    Low
}

/// <summary>
/// The single seam every ffmpeg/ffprobe invocation in this feature goes through (plan §2). All
/// implementations must: build argv as a <c>string[]</c> (never a joined command-line string);
/// enforce the caller-supplied timeout on top of, but independent from, the caller's own
/// <see cref="CancellationToken"/>; kill the entire process tree on cancellation or timeout
/// (R16); gate concurrency through a single process-wide gate sized by
/// <see cref="VideoEditingOptions.MaxConcurrentJobs"/> (R13), with the gate wait itself
/// cancellable and excluded from the timeout; schedule <see cref="VideoToolPriority.Low"/> work
/// behind <see cref="VideoToolPriority.Normal"/> work; and never buffer unbounded stdout/stderr.
/// </summary>
public interface IVideoToolRunner
{
    Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Same as <see cref="RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/>,
    /// with <paramref name="onStdOutLine"/> invoked synchronously once per stdout line as ffmpeg
    /// produces it (in addition to — not instead of — the line still being captured into
    /// <see cref="VideoToolResult.StdOut"/> as always). A genuine overload rather than an optional
    /// parameter on the 3-arg method, deliberately: existing callers/mocks of the 3-arg overload
    /// are completely unaffected by this addition. Used by <c>VideoCompileStepExecutor</c> to
    /// parse <c>-progress pipe:1</c> key=value lines into a real encode percentage; the callback
    /// must stay cheap and non-throwing, since it runs on the process's async I/O callback thread.
    /// </summary>
    Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine);

    Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="args"/> against <paramref name="workspace"/> rather than against this
    /// host — the same argv, executed by whoever holds the bytes.
    /// <para>
    /// <b>Why this exists.</b> A caller that holds an <see cref="IMediaWorkspace"/> must never reach
    /// for the ffmpeg process on THIS machine, because for a remote session (D11) there is no local
    /// process that could see the media: the argv names session-relative paths that only the worker
    /// can resolve. Passing the workspace in keeps that decision in one place instead of leaving a
    /// bare <c>IVideoToolRunner</c> call to silently run locally.
    /// </para>
    /// <para>
    /// The implementation is a delegation and nothing else — every argv handed to this member goes
    /// straight to <see cref="IMediaWorkspace.RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/>,
    /// which is the one place that decides what "run ffmpeg" means for a given workspace. A local
    /// workspace runs it through the very runner this member is declared on (same singleton, same
    /// process-wide concurrency gate, same timeout/kill behaviour, byte-identical result); a remote
    /// one sends the op to the runner and returns its result.
    /// </para>
    /// </summary>
    Task<VideoToolResult> RunFfmpegAsync(
        IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

    /// <inheritdoc cref="RunFfmpegAsync(IMediaWorkspace, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>
    Task<VideoToolResult> RunFfmpegAsync(
        IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string> onStdOutLine);
    // ---------------------------------------------------------------------------------------
    // Priority-carrying overloads. Same deliberate "a genuine overload, not an optional parameter"
    // rule as the 4-arg RunFfmpegAsync above: the three pre-existing methods keep their EXACT
    // current signatures, so every existing caller and every existing Moq setup of
    // IVideoToolRunner keeps compiling and keeps working untouched. Those methods are themselves
    // specified to schedule at VideoToolPriority.Normal.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/> at an
    /// explicit <paramref name="priority"/>.
    /// </summary>
    Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority);

    /// <summary>
    /// <see cref="RunFfmpegAsync(IReadOnlyList{string}, TimeSpan, CancellationToken, Action{string})"/>
    /// at an explicit <paramref name="priority"/>.
    /// </summary>
    Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string> onStdOutLine,
        VideoToolPriority priority);

    /// <summary>
    /// <see cref="RunFfprobeAsync(IReadOnlyList{string}, TimeSpan, CancellationToken)"/> at an
    /// explicit <paramref name="priority"/>.
    /// </summary>
    Task<VideoToolResult> RunFfprobeAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority);
}
