using System.Collections.Concurrent;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// Keeps on-demand syncs of one project apart: at most one runs at a time, and a sync the person asks
/// for — "Sync now", or a change of branch or files, which always re-syncs — is refused for
/// <see cref="Cooldown"/> after the previous one started. A sync downloads the selected files from
/// GitHub and writes them to storage, so a button pressed repeatedly, or a script alternating two
/// selections, must not turn into a queue of identical pulls against GitHub's rate limit.
///
/// <para>
/// The lease is taken by the request that asks for the sync and handed to <see cref="GitHubSyncQueue"/>;
/// <see cref="GitHubSyncWorker"/> disposes it when the pull ends. So the slot covers the whole time the
/// sync is queued or running, which is also what <see cref="IsRunning"/> reports.
/// </para>
///
/// <para>
/// Per process, deliberately: a second API replica has its own window, which at worst lets one more
/// sync through. The pull itself is idempotent (keyed on blob SHAs), so a sync that slips through is
/// wasted work, never wrong work. Registered as a singleton.
/// </para>
/// </summary>
public sealed class GitHubSyncThrottle
{
    /// <summary>The minimum gap between two requested syncs of one project.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastStarted = new();
    private readonly ConcurrentDictionary<Guid, byte> _running = new();
    private readonly TimeProvider _clock;

    public GitHubSyncThrottle(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Starts a sync of <paramref name="projectId"/>, or says how long to wait. A non-null lease must be
    /// disposed when the sync ends. <paramref name="enforceCooldown"/> is false only for the first sync
    /// of a brand-new link, where only the concurrency rule applies.
    /// </summary>
    public Lease? TryBegin(Guid projectId, bool enforceCooldown, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        DateTimeOffset now = _clock.GetUtcNow();
        bool hadPrevious = _lastStarted.TryGetValue(projectId, out DateTimeOffset last);

        if (enforceCooldown && hadPrevious && now - last < Cooldown)
        {
            retryAfter = Cooldown - (now - last);
            return null;
        }

        if (!_running.TryAdd(projectId, 0))
        {
            retryAfter = TimeSpan.FromSeconds(5);
            return null;
        }

        _lastStarted[projectId] = now;
        return new Lease(this, projectId, hadPrevious ? last : null);
    }

    /// <summary>
    /// True while a lease on <paramref name="projectId"/> is held: a sync is queued or running. This is
    /// the <c>syncing</c> flag the dashboard polls.
    /// </summary>
    public bool IsRunning(Guid projectId) => _running.ContainsKey(projectId);

    /// <summary>One project's sync slot. Disposing it frees the slot; the cooldown keeps running.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly GitHubSyncThrottle _owner;
        private readonly Guid _projectId;
        private readonly DateTimeOffset? _previous;
        private int _disposed;

        internal Lease(GitHubSyncThrottle owner, Guid projectId, DateTimeOffset? previous)
        {
            _owner = owner;
            _projectId = projectId;
            _previous = previous;
        }

        /// <summary>
        /// The request was refused or changed nothing, so no sync ran: the cooldown it started is
        /// withdrawn, and a corrected request can go through at once.
        /// </summary>
        public void NoSyncRan()
        {
            if (_previous is { } previous) _owner._lastStarted[_projectId] = previous;
            else _owner._lastStarted.TryRemove(_projectId, out _);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner._running.TryRemove(_projectId, out _);
        }
    }
}
