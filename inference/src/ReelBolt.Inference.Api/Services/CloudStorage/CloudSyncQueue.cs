using System.Threading.Channels;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>What asking for a sync did.</summary>
public enum CloudSyncEnqueueResult
{
    /// <summary>A sync of the link will run (newly queued, or folded into one already queued).</summary>
    Queued,

    /// <summary>A sync of the link is already queued or running; this request adds nothing.</summary>
    AlreadySyncing,

    /// <summary>A manual sync of the link was asked for less than <see cref="CloudSyncQueue.ManualCooldown"/> ago.</summary>
    TooSoon
}

/// <summary>
/// The in-process queue of cloud syncs (docs/design/cloud-drive-sync.md "Core services"). Every sync —
/// linking, a changed selection, "Sync now", the sweep — is queued here and run by
/// <see cref="CloudSyncWorker"/>; no sync ever runs inside an HTTP request.
///
/// <para>
/// One entry per link: a link already queued or running is not queued twice (the API reports
/// <c>already_syncing</c>). A change that needs a sync while one is running (a new selection) is
/// remembered and runs once more after it, so the project ends at the new selection. A manual sync has
/// a <see cref="ManualCooldown"/> per link. Per process: several replicas would each run their own,
/// which is idempotent wasted work, never wrong work.
/// </para>
/// </summary>
public sealed class CloudSyncQueue
{
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromSeconds(20);

    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false });
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastManual = [];
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public CloudSyncQueue(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    private sealed class Entry
    {
        public bool Running;
        public bool Force;
        public bool Rerun;
        public bool RerunForce;
    }

    /// <summary>The reader the worker drains.</summary>
    public ChannelReader<Guid> Reader => _channel.Reader;

    /// <summary>True while a sync of the link is queued or running (the API's <c>syncing</c>).</summary>
    public bool IsSyncing(Guid linkId)
    {
        lock (_gate) return _entries.ContainsKey(linkId);
    }

    /// <summary>
    /// Asks for a sync. <paramref name="manual"/> is "Sync now": it is subject to the cooldown, and while a
    /// sync is running it adds nothing (that sync is the answer). A non-manual request (link, selection
    /// change, sweep) during a running sync makes it run once more afterwards.
    /// </summary>
    public CloudSyncEnqueueResult Enqueue(Guid linkId, bool force, bool manual, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        lock (_gate)
        {
            if (_entries.TryGetValue(linkId, out Entry? entry))
            {
                if (!entry.Running)
                {
                    entry.Force |= force;
                    return manual ? CloudSyncEnqueueResult.AlreadySyncing : CloudSyncEnqueueResult.Queued;
                }

                if (manual) return CloudSyncEnqueueResult.AlreadySyncing;
                entry.Rerun = true;
                entry.RerunForce |= force;
                return CloudSyncEnqueueResult.Queued;
            }

            DateTimeOffset now = _clock.GetUtcNow();
            if (manual)
            {
                if (_lastManual.TryGetValue(linkId, out DateTimeOffset last) && now - last < ManualCooldown)
                {
                    retryAfter = ManualCooldown - (now - last);
                    return CloudSyncEnqueueResult.TooSoon;
                }

                _lastManual[linkId] = now;
                PruneCooldowns(now);
            }

            _entries[linkId] = new Entry { Force = force };
            _channel.Writer.TryWrite(linkId);
            return CloudSyncEnqueueResult.Queued;
        }
    }

    /// <summary>The worker took <paramref name="linkId"/> off the channel: marks it running and hands back its force flag. False when it is not queued (stale).</summary>
    public bool TryBegin(Guid linkId, out bool force)
    {
        lock (_gate)
        {
            force = false;
            if (!_entries.TryGetValue(linkId, out Entry? entry) || entry.Running) return false;
            entry.Running = true;
            force = entry.Force;
            return true;
        }
    }

    /// <summary>The sync of <paramref name="linkId"/> finished (any outcome). Re-queues it when a rerun was asked for meanwhile.</summary>
    public void Complete(Guid linkId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(linkId, out Entry? entry)) return;
            if (entry.Rerun)
            {
                entry.Running = false;
                entry.Force = entry.RerunForce;
                entry.Rerun = false;
                entry.RerunForce = false;
                _channel.Writer.TryWrite(linkId);
                return;
            }

            _entries.Remove(linkId);
        }
    }

    /// <summary>Drops cooldown records old enough to be irrelevant, so the map stays the size of recent activity.</summary>
    private void PruneCooldowns(DateTimeOffset now)
    {
        if (_lastManual.Count < 1024) return;
        foreach (Guid stale in _lastManual.Where(p => now - p.Value >= ManualCooldown).Select(p => p.Key).ToList())
        {
            _lastManual.Remove(stale);
        }
    }
}
