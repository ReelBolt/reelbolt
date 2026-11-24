using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>R12.2: limits of the hand-off package (config section <c>Editing:HandOff</c>).</summary>
public sealed class HandOffOptions
{
    public const string SectionName = "Editing:HandOff";

    /// <summary><c>Editing:HandOff:MaxMediaBytes</c>: the most media bytes one package carries (default 2 GiB). 0 = manifest only.</summary>
    public long MaxMediaBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary><c>Editing:HandOff:MaxFiles</c>: the most media files one package carries (default 200).</summary>
    public int MaxFiles { get; set; } = 200;

    /// <summary><c>Editing:HandOff:MaxConcurrentPerUser</c>: hand-off streams one user may have open at once (default 2).</summary>
    public int MaxConcurrentPerUser { get; set; } = 2;

    public long CapBytes => Math.Max(0, MaxMediaBytes);

    public int CapFiles => Math.Max(0, MaxFiles);

    public int CapStreams => Math.Max(1, MaxConcurrentPerUser);
}

/// <summary>
/// At most <see cref="HandOffOptions.MaxConcurrentPerUser"/> hand-off streams per user (process-local, like
/// <see cref="EditProposalRateLimiter"/>): a package reads a lot of storage, so one user cannot open them without bound.
/// </summary>
public sealed class HandOffLimiter(IOptions<HandOffOptions>? options = null)
{
    private readonly ConcurrentDictionary<Guid, int> _open = new();
    private readonly HandOffOptions _options = options?.Value ?? new HandOffOptions();

    /// <summary>A lease on one stream slot, or null when the user already has the maximum open. Dispose to release it.</summary>
    public IDisposable? TryEnter(Guid userId)
    {
        int limit = _options.CapStreams;
        bool entered = false;
        _open.AddOrUpdate(
            userId,
            _ => { entered = true; return 1; },
            (_, current) => { if (current < limit) { entered = true; return current + 1; } entered = false; return current; });
        return entered ? new Lease(this, userId) : null;
    }

    public int Open(Guid userId) => _open.TryGetValue(userId, out int n) ? n : 0;

    private void Release(Guid userId)
    {
        while (true)
        {
            if (!_open.TryGetValue(userId, out int current)) return;
            if (current <= 1)
            {
                if (_open.TryRemove(new KeyValuePair<Guid, int>(userId, current))) return;
            }
            else if (_open.TryUpdate(userId, current - 1, current)) return;
        }
    }

    private sealed class Lease(HandOffLimiter owner, Guid userId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(userId);
        }
    }
}
