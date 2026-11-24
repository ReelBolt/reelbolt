namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// S-06: the process-wide bookkeeping of running URL imports — how many run (in all and per
/// organization), how many quota bytes each organization's running imports have reserved, and how many
/// spool bytes all of them may still write — plus a per-organization async lock under which a reservation
/// is computed (the commit's quota re-check and row write hold the cross-replica storage guard, not this
/// limiter). Non-blocking: a full slot is a refusal (<c>IMPORT_BUSY</c>), never a queue, so a burst of imports
/// cannot pile up requests waiting on the network.
/// </summary>
/// <remarks>
/// One instance per process (<see cref="Shared"/>), like the import's pooled HttpClient: the import service
/// itself is scoped. Several API replicas each apply the limits on their own. The reservation bounds one
/// replica's concurrent imports; the cross-replica quota guarantee is the commit's advisory lock
/// (<see cref="UrlImportService"/>), not this limiter.
/// </remarks>
public sealed class UrlImportLimiter
{
    public static UrlImportLimiter Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<Guid, OrgState> _orgs = new();
    private int _running;
    private long _spoolReserved;

    /// <summary>
    /// Takes a slot, or returns null when <paramref name="maxConcurrent"/> imports already run, or the organization
    /// already runs its share: <paramref name="maxPerOrg"/>, but never more than half the global slots (S-32), so one
    /// tenant's slow upstreams can never refuse every other tenant.
    /// </summary>
    public Lease? TryEnter(Guid organizationId, int maxConcurrent, int maxPerOrg)
    {
        int global = Math.Max(1, maxConcurrent);
        int perOrg = Math.Min(Math.Max(1, maxPerOrg), Math.Max(1, global / 2));
        lock (_gate)
        {
            _orgs.TryGetValue(organizationId, out OrgState? org);
            if (_running >= global || (org?.Running ?? 0) >= perOrg)
                return null;

            if (org is null)
            {
                org = new OrgState();
                _orgs[organizationId] = org;
            }

            org.Running++;
            _running++;
            return new Lease(this, organizationId, org);
        }
    }

    /// <summary>The quota bytes the organization's running imports hold (0 when none runs).</summary>
    public long ReservedBytes(Guid organizationId)
    {
        lock (_gate)
            return _orgs.TryGetValue(organizationId, out OrgState? org) ? org.Reserved : 0;
    }

    internal sealed class OrgState
    {
        public int Running;
        public long Reserved;
        public readonly SemaphoreSlim Lock = new(1, 1);
    }

    /// <summary>One running import's slot and reservations; disposing returns all of them.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly UrlImportLimiter _owner;
        private readonly Guid _organizationId;
        private readonly OrgState _org;
        private long _reserved;
        private long _spool;
        private bool _disposed;

        internal Lease(UrlImportLimiter owner, Guid organizationId, OrgState org)
        {
            _owner = owner;
            _organizationId = organizationId;
            _org = org;
        }

        /// <summary>Quota bytes the organization's OTHER running imports hold.</summary>
        public long ReservedByOthers
        {
            get
            {
                lock (_owner._gate) return _org.Reserved - _reserved;
            }
        }

        /// <summary>Spool bytes every OTHER running import may still write.</summary>
        public long SpoolReservedByOthers
        {
            get
            {
                lock (_owner._gate) return _owner._spoolReserved - _spool;
            }
        }

        /// <summary>Holds <paramref name="quotaBytes"/> of the organization's quota and <paramref name="spoolBytes"/> of the spool for this import.</summary>
        public void Reserve(long quotaBytes, long spoolBytes)
        {
            lock (_owner._gate)
            {
                _org.Reserved += quotaBytes - _reserved;
                _owner._spoolReserved += spoolBytes - _spool;
                _reserved = quotaBytes;
                _spool = spoolBytes;
            }
        }

        /// <summary>The organization's reservation lock: a reservation is computed under it.</summary>
        public async Task<IDisposable> LockOrganizationAsync(CancellationToken ct)
        {
            await _org.Lock.WaitAsync(ct);
            return new Releaser(_org.Lock);
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                _org.Reserved -= _reserved;
                _owner._spoolReserved -= _spool;
                _org.Running--;
                _owner._running--;
                if (_org.Running == 0) _owner._orgs.Remove(_organizationId);
            }
        }

        private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
            }
        }
    }
}
