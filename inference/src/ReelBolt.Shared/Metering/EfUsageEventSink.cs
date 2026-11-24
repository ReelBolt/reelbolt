using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Inserts events through whichever DbContext maps <c>usage_events</c>. The engine's mapping is
/// <c>ExcludeFromMigrations()</c>, which only affects migrations, so it can insert like the API.
/// A retried batch is safe: rows whose idempotency key already exists are skipped, and a unique-index race
/// falls back to row-by-row so one duplicate never loses its neighbours.
/// </summary>
public sealed class EfUsageEventSink<TContext>(IServiceScopeFactory scopes) : IUsageEventSink
    where TContext : DbContext
{
    public async Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        DbSet<UsageEvent> set = db.Set<UsageEvent>();

        string[] keys = events.Select(e => e.IdempotencyKey).ToArray();
        HashSet<string> existing = (await set.AsNoTracking()
            .Where(e => keys.Contains(e.IdempotencyKey))
            .Select(e => e.IdempotencyKey)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        List<UsageEvent> fresh = events.Where(e => !existing.Contains(e.IdempotencyKey)).ToList();
        if (fresh.Count == 0) return;

        set.AddRange(fresh);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another writer inserted one of these keys between our check and the insert.
            db.ChangeTracker.Clear();
            foreach (UsageEvent row in fresh)
            {
                await using AsyncServiceScope single = scopes.CreateAsyncScope();
                TContext one = single.ServiceProvider.GetRequiredService<TContext>();
                one.Set<UsageEvent>().Add(row);
                try
                {
                    await one.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Duplicate idempotency key: already recorded, which is the point of the key.
                }
            }
        }
    }
}
