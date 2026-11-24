using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Serialises the count-then-insert of the publish caps per organization, so parallel POSTs cannot all pass the same count.
/// Dispose the handle after the rows are saved.
/// </summary>
public interface IMarketplaceCapLock
{
    Task<IAsyncDisposable> AcquireAsync(InferenceApiDbContext db, Guid organizationId, CancellationToken ct);
}

/// <summary>
/// PostgreSQL: a transaction holding <c>pg_advisory_xact_lock</c> keyed by the organization, committed on dispose (so the saved
/// rows and the lock end together). Other providers (InMemory, SQLite tests) take a semaphore keyed by organization instead.
/// </summary>
public sealed class MarketplaceCapLock : IMarketplaceCapLock
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Semaphores = new();

    public async Task<IAsyncDisposable> AcquireAsync(InferenceApiDbContext db, Guid organizationId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
            string key = $"marketplace-cap:{organizationId:N}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            return new TransactionHandle(transaction);
        }

        SemaphoreSlim gate = Semaphores.GetOrAdd(organizationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new SemaphoreHandle(gate);
    }

    private sealed class TransactionHandle(IDbContextTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.CommitAsync();
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private sealed class SemaphoreHandle(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
