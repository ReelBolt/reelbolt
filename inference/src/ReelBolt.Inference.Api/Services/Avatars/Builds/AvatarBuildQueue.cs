using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>
/// How the avatar build worker is run (section <c>Avatars:Builds</c>). Every value has a safe default; none is a
/// product switch (the product switch is <c>Avatars:Objects:Enabled</c>).
/// </summary>
public sealed class AvatarBuildOptions
{
    public const string SectionName = "Avatars:Builds";

    /// <summary>The longest one picture edit may take before that picture is skipped (the picture editor's bound).</summary>
    public int PictureTimeoutSeconds { get; set; } = 90;

    /// <summary>How often the sweeper looks for jobs to run or to pick up again.</summary>
    public int SweepIntervalSeconds { get; set; } = 20;

    /// <summary>
    /// A job in a working state whose heartbeat (<c>updated_at</c>) is older than this was left by a worker that
    /// stopped: the sweeper hands it to a worker again. Must be longer than one picture edit.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 300;

    /// <summary>How long a job waits for the new files' safety checks before it gives up (<c>AVATAR_CHECKS_TIMED_OUT</c>).</summary>
    public int ChecksTimeoutMinutes { get; set; } = 30;

    /// <summary>How long a job waits for a hidden run or for stills before it gives up.</summary>
    public int WaitTimeoutMinutes { get; set; } = 60;

    /// <summary>How many jobs one API process works on at once.</summary>
    public int Concurrency { get; set; } = 2;

    public TimeSpan PictureTimeout => TimeSpan.FromSeconds(Math.Clamp(PictureTimeoutSeconds, 5, 600));

    public TimeSpan SweepInterval => TimeSpan.FromSeconds(Math.Clamp(SweepIntervalSeconds, 1, 3600));

    public TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Max(StaleAfterSeconds, PictureTimeoutSeconds + 30));

    public TimeSpan ChecksTimeout => TimeSpan.FromMinutes(Math.Clamp(ChecksTimeoutMinutes, 1, 24 * 60));

    public TimeSpan WaitTimeout => TimeSpan.FromMinutes(Math.Clamp(WaitTimeoutMinutes, 1, 24 * 60));
}

/// <summary>
/// The in-process hand-over from "a job needs a worker" (the request that queued it, or the sweeper) to the worker.
/// Per process and lossy on purpose: the job row is the truth, and the sweeper offers again whatever a stopped
/// process dropped. A job id is in the queue at most once and is never run twice at once in one process.
/// </summary>
public sealed class AvatarBuildQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _known = new();

    public ChannelReader<Guid> Reader => _channel.Reader;

    /// <summary>Offers a job to the worker. False when it is already queued or running in this process.</summary>
    public bool Enqueue(Guid jobId)
    {
        if (!_known.TryAdd(jobId, 0)) return false;
        if (_channel.Writer.TryWrite(jobId)) return true;
        _known.TryRemove(jobId, out _);
        return false;
    }

    /// <summary>Whether this process holds the job (queued or running).</summary>
    public bool Holds(Guid jobId) => _known.ContainsKey(jobId);

    /// <summary>The worker finished with the job (it ended, or it waits and the sweeper will offer it again).</summary>
    public void Complete(Guid jobId) => _known.TryRemove(jobId, out _);
}
