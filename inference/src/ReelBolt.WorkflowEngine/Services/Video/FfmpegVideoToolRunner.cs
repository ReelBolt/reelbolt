using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Runs ffmpeg/ffprobe as child processes. Must be registered as a <b>singleton</b> — the
/// concurrency-limiting gate (R13) is only process-wide if exactly one instance exists for
/// the lifetime of the WorkflowEngine process.
/// </summary>
/// <remarks>
/// <para>
/// The gate is three independent FIFO lanes: the normal lane (<see cref="VideoEditingOptions.MaxConcurrentJobs"/>)
/// for interactive passes, a derivative lane (<see cref="VideoEditingOptions.DerivativeMaxConcurrentJobs"/>) for
/// <see cref="VideoToolPriority.Low"/> editor derivatives, and a heavy lane of one slot for interactive passes on
/// a source above the analysis-proxy threshold (see <see cref="SourceTimeoutPolicy.IsHeavy"/>). Derivatives never
/// take an interactive slot, and a big source never holds the slots short jobs need; at most one heavy, one
/// normal and one derivative invocation run at once. Derivative processes start under `nice -n 10`.
/// The wait is cancellable via the caller's token and is NOT counted against the ffmpeg timeout.
/// </para>
/// <para>
/// The gate is acquired and released around ONE invocation, never held across a whole multi-step
/// derivative run — a derivative yields between invocations, which is what makes the priority
/// rule observable rather than decorative.
/// </para>
/// </remarks>
public sealed class FfmpegVideoToolRunner : IVideoToolRunner
{
    /// <summary>
    /// Bound on captured stdout/stderr per invocation, so a pathological input (e.g. thousands
    /// of silencedetect/showinfo lines) cannot grow this process's memory unboundedly. Only the
    /// tail is kept — the most recent output is what matters for diagnosing a failure.
    /// </summary>
    private const int MaxCapturedChars = 1_000_000;

    private readonly VideoEditingOptions _options;
    private readonly ILogger<FfmpegVideoToolRunner> _logger;

    private readonly Lane _normalLane;
    private readonly Lane _derivativeLane;
    private readonly Lane _heavyLane;
    private readonly int _heavyMinDurationSec;

    public FfmpegVideoToolRunner(IOptions<VideoEditingOptions> options, ILogger<FfmpegVideoToolRunner> logger)
    {
        _options = options.Value;
        _logger = logger;
        _normalLane = new Lane(Math.Max(1, _options.MaxConcurrentJobs));
        _derivativeLane = new Lane(Math.Max(1, _options.DerivativeMaxConcurrentJobs));
        _heavyLane = new Lane(1);
        _heavyMinDurationSec = _options.AnalysisProxyMinDurationSeconds;
    }

    public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(_options.FfmpegPath, args, timeout, ct, onStdOutLine: null, VideoToolPriority.Normal);

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
        RunAsync(_options.FfmpegPath, args, timeout, ct, onStdOutLine, VideoToolPriority.Normal);

    public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(_options.FfprobePath, args, timeout, ct, onStdOutLine: null, VideoToolPriority.Normal);

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
        RunAsync(_options.FfmpegPath, args, timeout, ct, onStdOutLine: null, priority);

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string> onStdOutLine,
        VideoToolPriority priority) =>
        RunAsync(_options.FfmpegPath, args, timeout, ct, onStdOutLine, priority);

    public Task<VideoToolResult> RunFfprobeAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
        RunAsync(_options.FfprobePath, args, timeout, ct, onStdOutLine: null, priority);

    /// <summary>
    /// D10a/D11: the workspace-aware arm. Deliberately pure delegation — the workspace already owns
    /// the decision of what "run ffmpeg" means for it, and a local one routes straight back into the
    /// three-argument members above through the same singleton instance. Nothing is duplicated here:
    /// no second semaphore, no second timeout, no argv rewriting.
    /// </summary>
    public Task<VideoToolResult> RunFfmpegAsync(
        IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.RunFfmpegAsync(args, timeout, ct);
    }

    /// <inheritdoc cref="RunFfmpegAsync(IMediaWorkspace, IReadOnlyList{string}, TimeSpan, CancellationToken)"/>
    public Task<VideoToolResult> RunFfmpegAsync(
        IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string> onStdOutLine)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
    }

    private async Task<VideoToolResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onStdOutLine,
        VideoToolPriority priority)
    {
        // The wait is cancellable via the caller's own token but is NOT counted
        // against the ffmpeg timeout below — a job queued behind other jobs
        // must not time out just for having waited its turn (R13).
        Lane lane = priority == VideoToolPriority.Low
            ? _derivativeLane
            : SourceTimeoutPolicy.IsHeavy(_heavyMinDurationSec) ? _heavyLane : _normalLane;
        await AcquireAsync(lane, ct).ConfigureAwait(false);
        try
        {
            return await RunProcessAsync(executablePath, args, timeout, ct, onStdOutLine, priority == VideoToolPriority.Low)
                .ConfigureAwait(false);
        }
        finally
        {
            Release(lane);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Three independent FIFO lanes. A slot is handed out only by Pump(), under the lane's monitor, to the
    // waiter at the head of the queue, and is deducted at the same instant, so an authorised waiter can
    // never be overtaken. Lanes: normal (MaxConcurrentJobs, interactive passes), derivative
    // (DerivativeMaxConcurrentJobs, editor derivatives) and heavy (1 slot, interactive passes on a source
    // above the analysis-proxy threshold). Up to one heavy + normal + derivative ffmpeg run at once; a
    // derivative never waits for or blocks an interactive pass, and a big source never blocks a short one.
    // ---------------------------------------------------------------------------------------

    private static async Task AcquireAsync(Lane lane, CancellationToken ct)
    {
        Waiter waiter = new();
        lock (lane)
        {
            lane.Waiters.Enqueue(waiter);
            lane.Pump();
        }

        try
        {
            await waiter.Granted.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while queued. If Pump had already granted this waiter the slot is given back.
            lock (lane)
            {
                waiter.Cancelled = true;
                if (waiter.WasGranted)
                {
                    lane.Available++;
                    lane.Pump();
                }
            }

            throw;
        }
    }

    private static void Release(Lane lane)
    {
        lock (lane)
        {
            lane.Available++;
            lane.Pump();
        }
    }

    private sealed class Lane(int capacity)
    {
        public int Available { get; set; } = capacity;

        public Queue<Waiter> Waiters { get; } = new();

        /// <summary>Call under <c>lock (lane)</c>.</summary>
        public void Pump()
        {
            while (Available > 0 && Waiters.Count > 0)
            {
                Waiter next = Waiters.Dequeue();
                if (next.Cancelled)
                    continue;
                Available--;
                next.WasGranted = true;
                next.Granted.SetResult(true);
            }
        }
    }

    /// <summary>One queued caller's admission state. Mutated only under its lane's monitor.</summary>
    private sealed class Waiter
    {
        public TaskCompletionSource<bool> Granted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasGranted { get; set; }

        public bool Cancelled { get; set; }
    }

    private static readonly Lazy<string?> NicePath = new(() =>
    {
        foreach (string dir in new[] { "/usr/bin", "/bin" })
        {
            string candidate = Path.Combine(dir, "nice");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    });

    private async Task<VideoToolResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string>? onStdOutLine,
        bool belowNormal)
    {
        // Derivatives run at a lower OS priority. `nice` execs the tool (same pid, so kill still works);
        // without it, fall back to setting the priority class right after start.
        string? nice = belowNormal && OperatingSystem.IsLinux() ? NicePath.Value : null;
        ProcessStartInfo startInfo = new()
        {
            FileName = nice ?? executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        if (nice is not null)
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("10");
            startInfo.ArgumentList.Add(executablePath);
        }

        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };

        BoundedTail stdout = new(MaxCapturedChars);
        BoundedTail stderr = new(MaxCapturedChars);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            stdout.AppendLine(e.Data);

            // Best-effort only (see IVideoToolRunner's doc comment) — a throwing/slow callback
            // must never be able to break the ffmpeg invocation itself.
            try
            {
                onStdOutLine?.Invoke(e.Data);
            }
            catch
            {
                // Swallowed deliberately.
            }
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        bool killedByTimeout = false;

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{executablePath}'.");

        if (belowNormal && nice is null)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Best effort only.
            }
        }

        // -nostdin already tells ffmpeg not to read stdin, but we also close our own end of the
        // redirected pipe immediately so nothing this process does can ever block on a prompt.
        process.StandardInput.Close();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using CancellationTokenRegistration registration = timeoutCts.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    // If the external token itself was cancelled, this is a real cancellation,
                    // not a timeout — record which one actually happened for the caller.
                    killedByTimeout = !ct.IsCancellationRequested;
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Kill — benign race.
            }
        });

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine external cancellation (not our internal timeout) — the process has already
            // been killed by the registration above; propagate so the caller sees a real cancel.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own CancelAfter fired. WaitForExitAsync's own cancellation callback can run BEFORE ours
            // (registrations run last-first), so we cannot rely on the flag our callback sets: this IS the
            // timeout. Make sure the whole tree is dead and reaped, then report it as one.
            killedByTimeout = true;
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }

            using CancellationTokenSource reap = new(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(reap.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Could not be reaped in time; the exit code stays -1 and timedOut is still true.
            }
        }

        int exitCode = process.HasExited ? process.ExitCode : -1;
        if (killedByTimeout)
        {
            _logger.LogWarning(
                "ffmpeg/ffprobe invocation timed out after {TimeoutSeconds}s and was killed: {Executable} {Args}",
                timeout.TotalSeconds,
                executablePath,
                string.Join(' ', args));
        }

        return new VideoToolResult(exitCode, stdout.ToString(), stderr.ToString(), killedByTimeout);
    }

    /// <summary>
    /// Keeps only the last <c>maxChars</c> characters appended, dropping the oldest content
    /// first — a bounded tail rather than an unbounded in-memory buffer.
    /// </summary>
    private sealed class BoundedTail
    {
        private readonly int _maxChars;
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();

        public BoundedTail(int maxChars) => _maxChars = maxChars;

        public void AppendLine(string line)
        {
            lock (_gate)
            {
                _builder.Append(line).Append('\n');
                if (_builder.Length > _maxChars)
                    _builder.Remove(0, _builder.Length - _maxChars);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _builder.ToString();
            }
        }
    }
}
