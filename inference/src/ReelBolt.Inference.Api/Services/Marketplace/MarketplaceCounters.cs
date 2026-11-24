using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// A per-key critical section. PostgreSQL: a transaction holding <c>pg_advisory_xact_lock</c> for the key, so every replica queues
/// behind it and the work either commits with <see cref="CommitAsync"/> or rolls back on dispose. Other providers (InMemory, SQLite
/// tests): a process-wide semaphore per key (reference counted, so the table does not grow).
/// </summary>
public sealed class MarketplaceKeyLock : IAsyncDisposable
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Refs;
    }

    private static readonly Dictionary<string, Entry> Entries = [];

    private readonly IDbContextTransaction? _transaction;
    private readonly string? _key;
    private readonly Entry? _entry;

    private MarketplaceKeyLock(IDbContextTransaction? transaction, string? key, Entry? entry)
    {
        _transaction = transaction;
        _key = key;
        _entry = entry;
    }

    public static async Task<MarketplaceKeyLock> AcquireAsync(InferenceApiDbContext db, string key, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }

            return new MarketplaceKeyLock(transaction, null, null);
        }

        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!))
                Entries[key] = entry = new Entry();
            entry.Refs++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new MarketplaceKeyLock(null, key, entry);
    }

    /// <summary>Commits the transaction (PostgreSQL); a no-op elsewhere. Without it, disposing rolls the work back.</summary>
    public Task CommitAsync(CancellationToken ct = default) => _transaction is null ? Task.CompletedTask : _transaction.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
            await _transaction.DisposeAsync();
        if (_entry is not null && _key is not null)
            Release(_key, _entry, held: true);
    }

    private static void Release(string key, Entry entry, bool held)
    {
        if (held)
            entry.Gate.Release();
        lock (Entries)
        {
            if (--entry.Refs == 0)
                Entries.Remove(key);
        }
    }
}

/// <summary>
/// The denormalised counters of <c>marketplace_recipes</c>. PostgreSQL (any relational provider): one atomic
/// <c>UPDATE ... SET x = x + n</c>, so concurrent writers never lose an increment. The InMemory test provider has no
/// <c>ExecuteUpdate</c>: a reload-modify-save under one process-wide gate gives the same arithmetic there.
/// </summary>
public static class MarketplaceCounters
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A new organization copied the recipe.</summary>
    public static Task AddCopyOrgAsync(InferenceApiDbContext db, Guid recipeId, CancellationToken ct) =>
        db.Database.IsRelational()
            ? db.MarketplaceRecipes.Where(r => r.Id == recipeId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CopyOrgs, r => r.CopyOrgs + 1), ct)
            : Local(db, recipeId, r => r.CopyOrgs++, ct);

    /// <summary>A rating was added (+stars, +1), changed (+delta, 0) or removed.</summary>
    public static Task AddRatingAsync(InferenceApiDbContext db, Guid recipeId, int sumDelta, int countDelta, CancellationToken ct) =>
        db.Database.IsRelational()
            ? db.MarketplaceRecipes.Where(r => r.Id == recipeId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.RatingSum, r => r.RatingSum + sumDelta)
                    .SetProperty(r => r.RatingCount, r => r.RatingCount + countDelta), ct)
            : Local(db, recipeId, r =>
            {
                r.RatingSum += sumDelta;
                r.RatingCount += countDelta;
            }, ct);

    /// <summary>Sets the run count to an absolute value (the sweeper recomputes it from the engine tables).</summary>
    public static Task SetRunCountAsync(InferenceApiDbContext db, Guid recipeId, long runCount, CancellationToken ct) =>
        db.Database.IsRelational()
            ? db.MarketplaceRecipes.Where(r => r.Id == recipeId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunCount, runCount), ct)
            : Local(db, recipeId, r => r.RunCount = runCount, ct);

    private static async Task Local(InferenceApiDbContext db, Guid recipeId, Action<Data.Marketplace.MarketplaceRecipe> change, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Data.Marketplace.MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == recipeId, ct);
            if (recipe is null)
                return;
            await db.Entry(recipe).ReloadAsync(ct);
            change(recipe);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }
}
