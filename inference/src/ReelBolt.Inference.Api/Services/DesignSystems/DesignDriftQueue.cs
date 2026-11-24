using System.Runtime.CompilerServices;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>What asking for a drift check did.</summary>
public enum DesignDriftEnqueueResult
{
    /// <summary>The project was queued (there was no live entry and the debounce window had passed).</summary>
    Queued,

    /// <summary>The project already has a check queued or running; this request was folded into it.</summary>
    Coalesced,

    /// <summary>
    /// The same project was asked to be checked inside <see cref="DesignDriftQueue.DebounceWindow"/>;
    /// nothing was queued now, but a source-triggered request is remembered and re-runs once after the
    /// window (trailing edge). A manual check is never debounced.
    /// </summary>
    Debounced,

    /// <summary>Retained for compatibility; the per-organization lanes no longer share a capacity, so it is not returned.</summary>
    Dropped,

    /// <summary>
    /// The organization's lane already holds <see cref="DesignDriftQueue.PerOrganizationLimit"/> live
    /// checks (or, for a sweep, its half share). Nothing was queued now, but a source-triggered request
    /// is remembered and re-enqueued as the lane drains.
    /// </summary>
    OrganizationBusy,
}

/// <summary>One queued drift check.</summary>
public sealed record DesignDriftRequest(
    Guid ProjectId,
    DesignDriftTrigger Trigger,
    IReadOnlyCollection<string>? ChangedPaths);

/// <summary>The consumer side of <see cref="DesignDriftQueue"/>: round-robin across organization lanes.</summary>
public sealed class DesignDriftReader(DesignDriftQueue queue)
{
    /// <summary>Takes the next request (one per non-empty lane per turn) without waiting.</summary>
    public bool TryRead(out DesignDriftRequest request) => queue.TryDequeue(out request);

    /// <summary>Waits for and yields requests until <paramref name="ct"/> is cancelled.</summary>
    public async IAsyncEnumerable<DesignDriftRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            await queue.WaitForWorkAsync(ct);
            if (queue.TryDequeue(out DesignDriftRequest request))
                yield return request;
        }
    }
}

/// <summary>
/// The in-process scheduler of design-system drift checks (SPEC 13.2). Every sync path — a GitHub push,
/// a cloud-drive sync, a computer folder upload/delete, the periodic sweep — enqueues here, and
/// <see cref="DesignSystemDriftWorker"/> runs it; drift never runs inside an HTTP request or inside the
/// sync that noticed the change.
///
/// <para>
/// <b>One lane per organization</b> (an unknown organization shares a single lane with the same caps).
/// A lane holds at most <see cref="PerOrganizationLimit"/> live projects, a pending (trailing re-run)
/// set capped at <see cref="PendingLimit"/> and a dropped-memory capped at
/// <see cref="DroppedMemoryLimit"/>; sweeps may use at most half the lane. Nothing is shared between
/// lanes except the worker, which serves the lanes round-robin — one project per non-empty lane per
/// turn — so a tenant with five thousand projects takes one turn per rotation like everyone else, and
/// no tenant can fill, starve or postpone another's work. Empty lanes are dropped, and the number of
/// lanes is bounded by <see cref="MaxLanes"/> (overflow organizations share the unknown lane).
/// </para>
///
/// <para>
/// One live entry per project: a project already queued or running is not queued twice (a change made
/// while its check runs makes it run once more afterwards). A source-triggered request within
/// <see cref="DebounceWindow"/> of the project's last accepted request is remembered in its lane's
/// pending set and runs once when its own window has passed (trailing edge); a single timer is always
/// armed to the EARLIEST due time over every lane. A request refused because the lane is full is
/// remembered and re-enqueued as the lane drains. <see cref="DesignDriftTrigger.Manual"/> bypasses the
/// window and goes to the front of its own lane, but never past the lane's cap. A sweep request is
/// never remembered: it is periodic, not a change. Per process, like the cloud and GitHub queues.
/// </para>
/// </summary>
public class DesignDriftQueue : IDisposable
{
    /// <summary>Source-triggered requests for the same project closer together than this re-run after it.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(60);

    /// <summary>At most this many live (queued or running) projects per organization lane.</summary>
    public const int PerOrganizationLimit = 100;

    /// <summary>At most this many of a lane's live projects may come from the periodic sweep (half the lane).</summary>
    public const int SweepLimitPerOrganization = PerOrganizationLimit / 2;

    /// <summary>At most this many pending (trailing re-run) projects per lane.</summary>
    public const int PendingLimit = 100;

    /// <summary>At most this many refused projects remembered per lane.</summary>
    public const int DroppedMemoryLimit = 100;

    /// <summary>The generous global safety bound on lanes; further organizations share the unknown lane.</summary>
    public const int MaxLanes = 10_000;

    /// <summary>At most this many changed paths are remembered per project.</summary>
    public const int MaxTrackedPaths = 500;

    /// <summary>The marker kept once <see cref="MaxTrackedPaths"/> is exceeded.</summary>
    public const string ManyPaths = "many";

    private const int LastRequestPruneThreshold = 256;
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    private readonly Dictionary<Guid, Lane> _lanes = [];
    private readonly Dictionary<Guid, Entry> _live = [];
    private readonly Queue<Lane> _ring = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private ITimer? _timer;
    private DateTimeOffset? _timerDue;
    private bool _disposed;
    private long _forgotten;

    public DesignDriftQueue(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Reader = new DesignDriftReader(this);
    }

    private sealed class Lane(Guid id)
    {
        public Guid Id { get; } = id;
        public LinkedList<Guid> Ready { get; } = new();
        public Dictionary<Guid, Entry> Live { get; } = [];
        public Dictionary<Guid, Pending> Pending { get; } = [];
        public List<Dropped> Dropped { get; } = [];
        public Dictionary<Guid, DateTimeOffset> LastRequest { get; } = [];
        public int SweepLive;
        public bool InRing;
    }

    private sealed class Entry
    {
        public Lane Lane = null!;
        public bool Running;
        public DesignDriftTrigger Trigger;
        public HashSet<string>? Paths;
        public bool Rerun;
        public DesignDriftTrigger RerunTrigger;
        public HashSet<string>? RerunPaths;
        public bool CountsAsSweep;
        public LinkedListNode<Guid>? Node;
    }

    private sealed class Pending
    {
        public DesignDriftTrigger Trigger;
        public HashSet<string>? Paths;
        public DateTimeOffset DueAt;
    }

    private sealed class Dropped
    {
        public Guid ProjectId;
        public DesignDriftTrigger Trigger;
        public HashSet<string>? Paths;
    }

    /// <summary>The reader the worker drains.</summary>
    public DesignDriftReader Reader { get; }

    /// <summary>Lanes currently held (an empty lane with an unexpired debounce memory still counts).</summary>
    internal int LaneCount
    {
        get { lock (_gate) return _lanes.Count; }
    }

    /// <summary>Requests forgotten because a lane's pending and dropped memories were both full (a counter, no content).</summary>
    public long ForgottenRequests => Interlocked.Read(ref _forgotten);

    /// <summary>True while a check of the project is queued or running.</summary>
    public bool IsQueued(Guid projectId)
    {
        lock (_gate) return _live.ContainsKey(projectId);
    }

    /// <summary>Asks for a check of a project whose organization is unknown: it uses the shared unknown lane.</summary>
    public virtual DesignDriftEnqueueResult Enqueue(
        Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths) =>
        EnqueueCore(projectId, trigger, changedPaths, Guid.Empty);

    /// <summary>Asks for a check of <paramref name="projectId"/>, charged to <paramref name="organizationId"/>'s lane.</summary>
    public virtual DesignDriftEnqueueResult Enqueue(
        Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, Guid organizationId) =>
        EnqueueCore(projectId, trigger, changedPaths, organizationId);

    private DesignDriftEnqueueResult EnqueueCore(
        Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, Guid organizationId)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(projectId, out Entry? entry))
            {
                Fold(entry, trigger, changedPaths);
                return DesignDriftEnqueueResult.Coalesced;
            }

            Lane lane = LaneFor(organizationId);
            DateTimeOffset now = _clock.GetUtcNow();
            bool manual = trigger == DesignDriftTrigger.Manual;
            if (!manual && lane.LastRequest.TryGetValue(projectId, out DateTimeOffset last) && now - last < DebounceWindow)
            {
                // Trailing edge: the check that ran (or is queued) may not have seen this change.
                if (trigger != DesignDriftTrigger.Sweep)
                    Remember(lane, projectId, trigger, changedPaths, last + DebounceWindow, now);
                return DesignDriftEnqueueResult.Debounced;
            }

            return TryQueue(lane, projectId, trigger, changedPaths, now);
        }
    }

    private Lane LaneFor(Guid organizationId)
    {
        if (_lanes.TryGetValue(organizationId, out Lane? lane))
            return lane;

        if (_lanes.Count >= MaxLanes && organizationId != Guid.Empty)
            return LaneFor(Guid.Empty);

        lane = new Lane(organizationId);
        _lanes[organizationId] = lane;
        return lane;
    }

    private DesignDriftEnqueueResult TryQueue(
        Lane lane, Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths, DateTimeOffset now)
    {
        bool sweep = trigger == DesignDriftTrigger.Sweep;
        if (lane.Live.Count >= PerOrganizationLimit || (sweep && lane.SweepLive >= SweepLimitPerOrganization))
        {
            if (!sweep)
                RememberDropped(lane, projectId, trigger, changedPaths);
            return DesignDriftEnqueueResult.OrganizationBusy;
        }

        lane.LastRequest[projectId] = now;
        PruneLastRequests(lane, now);

        var entry = new Entry
        {
            Lane = lane,
            Trigger = trigger,
            Paths = MergePaths(null, changedPaths),
            CountsAsSweep = sweep,
        };
        lane.Live[projectId] = entry;
        _live[projectId] = entry;
        if (sweep)
            lane.SweepLive++;

        AddReady(lane, projectId, entry, front: trigger == DesignDriftTrigger.Manual);

        // Whatever was waiting for a later run is covered by this one, which reads the whole project.
        lane.Pending.Remove(projectId);
        lane.Dropped.RemoveAll(d => d.ProjectId == projectId);
        return DesignDriftEnqueueResult.Queued;
    }

    private void AddReady(Lane lane, Guid projectId, Entry entry, bool front)
    {
        entry.Node = front ? lane.Ready.AddFirst(projectId) : lane.Ready.AddLast(projectId);
        if (!lane.InRing)
        {
            lane.InRing = true;
            _ring.Enqueue(lane);
        }

        if (!_disposed)
            _signal.Release();
    }

    private void Fold(Entry entry, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths)
    {
        if (!entry.Running)
        {
            entry.Trigger = PreferTrigger(entry.Trigger, trigger);
            entry.Paths = MergePaths(entry.Paths, changedPaths);
            ReleaseSweepShareIfSourceTriggered(entry, trigger);

            // A manual check jumps the front of its own lane.
            if (trigger == DesignDriftTrigger.Manual && entry.Node is { List: not null } node)
            {
                entry.Lane.Ready.Remove(node);
                entry.Lane.Ready.AddFirst(node);
            }

            return;
        }

        entry.RerunTrigger = entry.Rerun ? PreferTrigger(entry.RerunTrigger, trigger) : trigger;
        entry.Rerun = true;
        entry.RerunPaths = MergePaths(entry.RerunPaths, changedPaths);
        ReleaseSweepShareIfSourceTriggered(entry, trigger);
    }

    private static void ReleaseSweepShareIfSourceTriggered(Entry entry, DesignDriftTrigger trigger)
    {
        if (entry.CountsAsSweep && trigger != DesignDriftTrigger.Sweep)
        {
            entry.CountsAsSweep = false;
            entry.Lane.SweepLive--;
        }
    }

    private void Remember(
        Lane lane, Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths,
        DateTimeOffset dueAt, DateTimeOffset now)
    {
        if (!lane.Pending.TryGetValue(projectId, out Pending? pending))
        {
            if (lane.Pending.Count >= PendingLimit)
            {
                // This lane's pending set is full: fall back to its dropped-memory, which re-enqueues
                // as the lane drains. Another lane's pending set is never touched.
                RememberDropped(lane, projectId, trigger, changedPaths);
                return;
            }

            pending = new Pending { Trigger = trigger, DueAt = dueAt };
            lane.Pending[projectId] = pending;
        }
        else
        {
            pending.Trigger = PreferTrigger(pending.Trigger, trigger);
        }

        pending.Paths = MergePaths(pending.Paths, changedPaths);
        ArmTimerNoLaterThan(pending.DueAt, now);
    }

    private void RememberDropped(Lane lane, Guid projectId, DesignDriftTrigger trigger, IReadOnlyCollection<string>? changedPaths)
    {
        Dropped? existing = lane.Dropped.Find(d => d.ProjectId == projectId);
        if (existing is not null)
        {
            existing.Trigger = PreferTrigger(existing.Trigger, trigger);
            existing.Paths = MergePaths(existing.Paths, changedPaths);
            return;
        }

        if (lane.Dropped.Count >= DroppedMemoryLimit)
        {
            Interlocked.Increment(ref _forgotten);
            return;
        }

        lane.Dropped.Add(new Dropped { ProjectId = projectId, Trigger = trigger, Paths = MergePaths(null, changedPaths) });
    }

    /// <summary>Arms the one timer for <paramref name="due"/> unless it is already armed for an earlier time.</summary>
    private void ArmTimerNoLaterThan(DateTimeOffset due, DateTimeOffset now)
    {
        if (_disposed || (_timerDue is { } armed && armed <= due))
            return;

        _timerDue = due;
        _timer ??= _clock.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        TimeSpan wait = due - now;
        _timer.Change((wait < TimeSpan.Zero ? TimeSpan.Zero : wait) + TimerSlack, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Queues the trailing re-run of every pending project whose window has passed, in every lane, then
    /// re-arms the timer to the earliest remaining due time. Called by the queue's own timer; public so
    /// a test (or a host tick) can drive it.
    /// </summary>
    public void FlushDue()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            DateTimeOffset now = _clock.GetUtcNow();
            _timerDue = null;
            DateTimeOffset? earliest = null;

            foreach (Lane lane in _lanes.Values.ToList())
            {
                foreach ((Guid projectId, Pending pending) in lane.Pending.OrderBy(p => p.Value.DueAt).ToList())
                {
                    if (pending.DueAt > now)
                    {
                        earliest = earliest is { } e && e <= pending.DueAt ? e : pending.DueAt;
                        continue;
                    }

                    lane.Pending.Remove(projectId);
                    if (_live.TryGetValue(projectId, out Entry? entry))
                        Fold(entry, pending.Trigger, pending.Paths);
                    else
                        TryQueue(lane, projectId, pending.Trigger, pending.Paths, now);
                }

                CleanupLane(lane, now);
            }

            if (earliest is { } next)
                ArmTimerNoLaterThan(next, now);
        }
    }

    /// <summary>The timer callback: never lets an exception escape onto a timer thread (a tick can race shutdown).</summary>
    private void OnTimer()
    {
        try
        {
            FlushDue();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _forgotten);
        }
    }

    /// <summary>Takes the next request off the lanes round-robin: one project per non-empty lane per turn.</summary>
    internal bool TryDequeue(out DesignDriftRequest request)
    {
        lock (_gate)
        {
            while (_ring.Count > 0)
            {
                Lane lane = _ring.Dequeue();
                lane.InRing = false;
                if (lane.Ready.First is not { } head)
                    continue;

                Guid projectId = head.Value;
                lane.Ready.RemoveFirst();
                if (!lane.Live.TryGetValue(projectId, out Entry? entry))
                {
                    // Defensive: an entry released while still queued leaves nothing to run.
                    if (lane.Ready.Count > 0 && !lane.InRing)
                    {
                        lane.InRing = true;
                        _ring.Enqueue(lane);
                    }

                    continue;
                }

                entry.Node = null;

                if (lane.Ready.Count > 0)
                {
                    lane.InRing = true;
                    _ring.Enqueue(lane);
                }

                request = new DesignDriftRequest(projectId, entry.Trigger, entry.Paths?.ToArray());
                return true;
            }
        }

        request = null!;
        return false;
    }

    internal Task WaitForWorkAsync(CancellationToken ct) => _disposed ? Task.Delay(Timeout.Infinite, ct) : _signal.WaitAsync(ct);

    /// <summary>
    /// The worker took a request for <paramref name="projectId"/> off the queue: marks it running and
    /// hands back the request it should run (which may have been widened meanwhile). False when there is
    /// no live entry (stale).
    /// </summary>
    public bool TryBegin(Guid projectId, out DesignDriftRequest request)
    {
        lock (_gate)
        {
            request = null!;
            if (!_live.TryGetValue(projectId, out Entry? entry) || entry.Running)
                return false;

            entry.Running = true;
            request = new DesignDriftRequest(projectId, entry.Trigger, entry.Paths?.ToArray());
            return true;
        }
    }

    /// <summary>The check finished (any outcome). Runs it once more when a change was asked for meanwhile.</summary>
    public void Complete(Guid projectId)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(projectId, out Entry? entry))
                return;

            Lane lane = entry.Lane;
            if (entry.Rerun)
            {
                entry.Running = false;
                entry.Trigger = entry.RerunTrigger;
                entry.Paths = entry.RerunPaths;
                entry.Rerun = false;
                entry.RerunTrigger = default;
                entry.RerunPaths = null;
                AddReady(lane, projectId, entry, front: entry.Trigger == DesignDriftTrigger.Manual);
                return;
            }

            if (entry.Node is { List: not null } stillReady)
                lane.Ready.Remove(stillReady);
            lane.Live.Remove(projectId);
            _live.Remove(projectId);
            if (entry.CountsAsSweep)
                lane.SweepLive--;

            // Space freed: re-enqueue what this lane had to refuse.
            DateTimeOffset now = _clock.GetUtcNow();
            while (lane.Dropped.Count > 0 && lane.Live.Count < PerOrganizationLimit)
            {
                Dropped next = lane.Dropped[0];
                lane.Dropped.RemoveAt(0);
                if (_live.ContainsKey(next.ProjectId))
                    continue;
                TryQueue(lane, next.ProjectId, next.Trigger, next.Paths, now);
            }

            CleanupLane(lane, now);
        }
    }

    private void CleanupLane(Lane lane, DateTimeOffset now)
    {
        if (lane.Live.Count > 0 || lane.Pending.Count > 0 || lane.Dropped.Count > 0)
            return;

        foreach (Guid stale in lane.LastRequest.Where(p => now - p.Value >= DebounceWindow).Select(p => p.Key).ToList())
            lane.LastRequest.Remove(stale);

        if (lane.LastRequest.Count == 0 && _lanes.TryGetValue(lane.Id, out Lane? held) && ReferenceEquals(held, lane))
            _lanes.Remove(lane.Id);
    }

    private static void PruneLastRequests(Lane lane, DateTimeOffset now)
    {
        if (lane.LastRequest.Count < LastRequestPruneThreshold)
            return;

        foreach (Guid stale in lane.LastRequest
            .Where(p => now - p.Value >= DebounceWindow && !lane.Pending.ContainsKey(p.Key))
            .Select(p => p.Key)
            .ToList())
        {
            lane.LastRequest.Remove(stale);
        }
    }

    /// <summary>A manual check outranks the source trigger it was folded into; otherwise the first wins.</summary>
    private static DesignDriftTrigger PreferTrigger(DesignDriftTrigger current, DesignDriftTrigger next) =>
        next == DesignDriftTrigger.Manual ? DesignDriftTrigger.Manual : current;

    private static HashSet<string>? MergePaths(HashSet<string>? target, IReadOnlyCollection<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return target;

        target ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            // A burst of changes must not grow the set without bound; past the cap the paths only matter
            // as "many" (the drift service re-reads the whole project anyway).
            if (target.Count >= MaxTrackedPaths)
            {
                target.Add(ManyPaths);
                break;
            }

            target.Add(path);
        }

        return target;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _signal.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
