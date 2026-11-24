using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// One sync somebody asked for: "Sync now", a new link, or a changed branch or selection.
/// <see cref="Lease"/> is the project's slot in <see cref="GitHubSyncThrottle"/>, taken by the request
/// and owned from then on by the queue — <see cref="GitHubSyncWorker"/> disposes it when the pull ends.
/// </summary>
public sealed record GitHubSyncRequest(Guid ProjectId, bool Force, GitHubSyncThrottle.Lease Lease);

/// <summary>
/// The syncs waiting for <see cref="GitHubSyncWorker"/>. Registered as a singleton.
///
/// <para>
/// A pull downloads every changed file of the selection one blob at a time, which for a few hundred
/// files takes minutes — longer than nginx (60 s), the browser and Cloudflare will hold a request open.
/// Run inside the request it was cut off (499) and, because it ran on the request's cancellation token,
/// cancelled with it. So the request only checks, takes the project's sync slot and enqueues; the pull
/// runs here, on the host's own lifetime, and its outcome lands on the link where the dashboard reads it.
/// </para>
///
/// <para>
/// Unbounded on purpose: at most one entry per project can exist (the throttle lease is taken before
/// enqueueing), so the queue is bounded by the number of projects, not by how often a button is
/// pressed. In memory and per process, like the throttle: a restart drops queued syncs, and the next
/// "Sync now" or push repairs the project because a pull reconciles by content.
/// </para>
/// </summary>
public sealed class GitHubSyncQueue
{
    private readonly Channel<GitHubSyncRequest> _channel =
        Channel.CreateUnbounded<GitHubSyncRequest>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    /// <summary>
    /// Queues a sync of <paramref name="projectId"/>. The queue takes ownership of
    /// <paramref name="lease"/>; it is released if the sync cannot be queued at all.
    /// </summary>
    public void Enqueue(Guid projectId, bool force, GitHubSyncThrottle.Lease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_channel.Writer.TryWrite(new GitHubSyncRequest(projectId, force, lease)))
        {
            lease.NoSyncRan();
            lease.Dispose();
            throw new InvalidOperationException("The GitHub sync queue is closed.");
        }
    }

    /// <summary>Waits for the next queued sync. Used by <see cref="GitHubSyncWorker"/>.</summary>
    public IAsyncEnumerable<GitHubSyncRequest> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Takes the next queued sync without waiting, or false when there is none.</summary>
    public bool TryDequeue([NotNullWhen(true)] out GitHubSyncRequest? request) => _channel.Reader.TryRead(out request);
}
