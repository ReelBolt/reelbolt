using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// Serialises one social account's token refresh across replicas. A refresh token that rotates is lost
/// when two replicas refresh at once, so the whole refresh (reload, call the platform, store) runs inside
/// one transaction holding <c>pg_advisory_xact_lock(hashtext(account_id))</c>. The per-process semaphore is
/// only the cheap local fast path (and the only serialisation on a provider without advisory locks: the
/// in-memory store cannot open a transaction at all). Same shape as <c>OrganizationStorageLock</c>.
/// </summary>
public class SocialRefreshLock
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Takes the account's guard. Dispose it; call <see cref="SocialRefreshGuard.CommitAsync"/> to keep the writes.</summary>
    public virtual async Task<SocialRefreshGuard> AcquireAsync(InferenceApiDbContext db, Guid accountId, CancellationToken ct)
    {
        SemaphoreSlim gate = _locks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            IDbContextTransaction? owned = db.Database.CurrentTransaction is null && db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;

            try
            {
                if (db.Database.ProviderName == NpgsqlProvider)
                {
                    string lockKey = "social-refresh:" + accountId;
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);
                }
            }
            catch
            {
                if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return new SocialRefreshGuard(gate, owned);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }
}

/// <summary>One account's refresh critical section. Disposing rolls an uncommitted transaction back and always releases the semaphore.</summary>
public sealed class SocialRefreshGuard : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly IDbContextTransaction? _owned;
    private int _disposed;

    internal SocialRefreshGuard(SemaphoreSlim gate, IDbContextTransaction? owned)
    {
        _gate = gate;
        _owned = owned;
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (_owned is not null) await _owned.CommitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_owned is not null) await _owned.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: a failed rollback must not mask the exception that got us here.
        }
        finally
        {
            _gate.Release();
        }
    }
}
