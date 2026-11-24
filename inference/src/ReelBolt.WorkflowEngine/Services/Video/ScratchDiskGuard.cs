using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>Raised when the scratch disk cannot take a stage in time, or a stage ran out of space. Platform-class, refunded.</summary>
public sealed class ScratchBusyException() : IOException(
    $"{ScratchDiskGuard.BusyCode}: the editing machines are busy; nothing was charged.")
{
}

/// <summary>A source over the platform limit; the message carries the code so the failure classifier maps it.</summary>
public sealed class SourceTooLargeException() : IOException($"{VideoSourceLimits.TooLargeCode}: this source is larger than the platform can edit.")
{
}

public interface IScratchDiskGuard
{
    /// <summary>
    /// Waits (bounded, cancellable) until the scratch volume, minus what other stages in this process
    /// already reserved, can hold <paramref name="sourceBytes"/> × 1.2 + 2 GiB, then returns a lease
    /// that holds that reservation until disposed. Throws <see cref="ScratchBusyException"/> on timeout.
    /// </summary>
    Task<IDisposable> ReserveAsync(long sourceBytes, CancellationToken ct);

    /// <summary>The same check without waiting: false when the space is not there right now.</summary>
    bool TryReserve(long sourceBytes, out IDisposable lease);

    /// <summary>
    /// True while at least one stage waits in <see cref="ReserveAsync"/> for space it did not get at once. Background
    /// work that holds a reservation reads it to give way. A guard that cannot tell answers false.
    /// </summary>
    bool HasWaiters => false;

    /// <summary>The platform's source limit (<c>VideoSources:MaxBytes</c>), checked before any staging.</summary>
    long MaxSourceBytes { get; }

    /// <summary>The platform's source length limit (<c>VideoSources:MaxSeconds</c>).</summary>
    int MaxSourceSeconds { get; }
}

/// <summary>
/// One guard in front of every stage that stages or decodes a whole source, so a 20 GB upload cannot fill
/// the scratch volume under the stages already running.
/// </summary>
public sealed class ScratchDiskGuard : IScratchDiskGuard
{
    public const string BusyCode = "SCRATCH_BUSY";
    public const long HeadroomBytes = 2L * 1024 * 1024 * 1024;
    public const double SourceMultiplier = 1.2;

    private static readonly object Gate = new();
    private static long reservedBytes;

    /// <summary>Set inside <see cref="QuietWaits"/>: a wait for space made there is not counted.</summary>
    private static readonly AsyncLocal<bool> QuietWaiter = new();

    private int _waiters;

    private readonly Func<long> _freeBytes;
    private readonly TimeSpan _maxWait;
    private readonly TimeSpan _poll;
    private readonly ILogger? _logger;

    public ScratchDiskGuard(
        IOptions<VideoEditingOptions> options, ILogger<ScratchDiskGuard> logger, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
        : this(() => FreeBytes(options.Value.ScratchPath), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5), logger,
            configuration is null ? VideoSourceLimits.Default : VideoSourceLimits.From(configuration))
    {
    }

    public long MaxSourceBytes { get; }

    public int MaxSourceSeconds { get; }

    public ScratchDiskGuard(Func<long> freeBytes, TimeSpan maxWait, TimeSpan poll, ILogger? logger = null, VideoSourceLimits? limits = null)
    {
        MaxSourceBytes = (limits ?? VideoSourceLimits.Default).MaxBytes;
        MaxSourceSeconds = (limits ?? VideoSourceLimits.Default).MaxSeconds;
        _freeBytes = freeBytes;
        _maxWait = maxWait;
        _poll = poll;
        _logger = logger;
    }

    public static long Needed(long sourceBytes) =>
        (long)Math.Ceiling(Math.Max(0, sourceBytes) * SourceMultiplier) + HeadroomBytes;

    public bool TryReserve(long sourceBytes, out IDisposable lease)
    {
        long need = Needed(sourceBytes);
        lock (Gate)
        {
            if (_freeBytes() - reservedBytes >= need)
            {
                reservedBytes += need;
                lease = new Lease(need);
                return true;
            }
        }

        lease = default!;
        return false;
    }

    /// <inheritdoc />
    public bool HasWaiters => Volatile.Read(ref _waiters) > 0;

    /// <summary>
    /// Until the returned scope is disposed, a wait for space made by this async flow is not counted by
    /// <see cref="HasWaiters"/>. For background work that gives way to waiters: its own wait must not make it give way.
    /// </summary>
    public static IDisposable QuietWaits()
    {
        bool before = QuietWaiter.Value;
        QuietWaiter.Value = true;
        return new QuietScope(before);
    }

    public async Task<IDisposable> ReserveAsync(long sourceBytes, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + _maxWait;
        bool logged = false;
        bool counted = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (TryReserve(sourceBytes, out IDisposable lease))
                    return lease;
                if (!logged)
                {
                    _logger?.LogWarning("Scratch disk is full for a {Bytes}-byte source; waiting for space", sourceBytes);
                    logged = true;
                }

                if (DateTime.UtcNow + _poll > deadline)
                    throw new ScratchBusyException();
                if (!counted && !QuietWaiter.Value)
                {
                    Interlocked.Increment(ref _waiters);
                    counted = true;
                }

                await Task.Delay(_poll, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (counted)
                Interlocked.Decrement(ref _waiters);
        }
    }

    private sealed class QuietScope(bool before) : IDisposable
    {
        public void Dispose() => QuietWaiter.Value = before;
    }

    /// <summary>Whether <paramref name="exception"/> is the OS saying the disk is full.</summary>
    public static bool IsDiskFull(Exception? exception) =>
        exception is IOException io && (io.HResult is 28 or unchecked((int)0x80070070) || io.HResult == 0x70 ||
                                       io.Message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase));

    private static long FreeBytes(string path)
    {
        try
        {
            string probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } parent)
                probe = parent;
            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Cannot read the volume: fail closed (the caller waits, then SCRATCH_BUSY), never allow on a guess.
            return 0;
        }
    }

    private sealed class Lease(long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                lock (Gate)
                    reservedBytes = Math.Max(0, reservedBytes - bytes);
        }
    }
}

/// <summary>Timeout scopes of one stage, disposed newest first so the previous scope is restored.</summary>
public sealed class ScopeSet : IDisposable
{
    private readonly List<IDisposable> _scopes = [];

    public void Add(IDisposable scope) => _scopes.Add(scope);

    public void Dispose()
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            _scopes[i].Dispose();
        _scopes.Clear();
    }
}

/// <summary>Holds the scratch reservations of one stage; disposing releases them all.</summary>
public sealed class ScratchLeases : IDisposable
{
    private readonly List<IDisposable> _leases = [];

    public void Add(IDisposable lease) => _leases.Add(lease);

    public void Dispose()
    {
        foreach (IDisposable lease in _leases)
            lease.Dispose();
        _leases.Clear();
    }

    /// <summary>
    /// Checks the project file's stored size against the platform source limit (throws
    /// <see cref="SourceTooLargeException"/>), then reserves scratch space for it and keeps the lease here.
    /// Returns the stored size (0 when unknown or no guard is configured, which means no timeout scaling).
    /// Only for a LOCAL workspace: a remote runner stages on its own disk and is bounded by the worker's own
    /// MaxStageBytes (which follows the same limit), so callers on a remote workspace skip this.
    /// </summary>
    public async Task<long> ReserveAsync(
        IScratchDiskGuard? guard,
        Storage.IProjectFileWorkspace files,
        Guid projectId,
        string storageKey,
        CancellationToken ct,
        bool localWorkspace = true)
    {
        if (guard is null)
            return 0;
        long bytes = await SourceBytesAsync(files, projectId, storageKey, ct).ConfigureAwait(false);
        if (bytes > guard.MaxSourceBytes)
            throw new SourceTooLargeException();
        if (localWorkspace)
            _leases.Add(await guard.ReserveAsync(bytes, ct).ConfigureAwait(false));
        return bytes;
    }

    public static async Task<long> SourceBytesAsync(
        Storage.IProjectFileWorkspace files, Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<Storage.ProjectWorkspaceFile> rows = await files.ListFilesAsync(projectId, ct).ConfigureAwait(false);
            return rows.FirstOrDefault(f => string.Equals(f.StorageKey, storageKey, StringComparison.Ordinal))?.SizeBytes ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }
}
