using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// The transaction-scoped advisory locks of contract C8. Every cap decision on a promotion (checkout
/// reservation, bonus grant, discount record) runs under <c>pg_advisory_xact_lock(hashtext('promo:' ||
/// promotion_id))</c>; a credit-code redeem's per-user cap under the per-(promotion, user) key. Several
/// promotions are always locked in ordinal key order, so two paths never wait on each other in a cycle.
/// Other providers (the Sqlite tests) take no lock.
/// </summary>
public static class PromotionLocks
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public static string PromotionKey(Guid promotionId) => "promo:" + promotionId.ToString("D");

    public static string UserKey(Guid promotionId, Guid userId) => $"promo:{promotionId:D}:user:{userId:D}";

    /// <summary>The SqlState Postgres raises when a lock wait passes <c>lock_timeout</c>.</summary>
    public const string LockNotAvailableSqlState = "55P03";

    /// <summary>
    /// S-11: one in-process gate per promotion key, so the checkouts of one promotion queue here, holding
    /// no pooled connection, rather than each waiting on the advisory lock with a connection of its own.
    /// Entries are never removed: there is one per promotion ever checked out, a small, bounded set.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CheckoutGates = new(StringComparer.Ordinal);

    private static IEnumerable<string> OrderedKeys(IEnumerable<Guid?> promotionIds)
        => promotionIds.OfType<Guid>().Select(PromotionKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>Locks every distinct promotion in <paramref name="promotionIds"/>, in ordinal key order. Needs an open transaction.</summary>
    public static async Task LockPromotionsAsync(DbContext db, IEnumerable<Guid?> promotionIds, CancellationToken ct)
    {
        foreach (string key in OrderedKeys(promotionIds))
        {
            await LockAsync(db, key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="LockPromotionsAsync(DbContext, IEnumerable{Guid?}, CancellationToken)"/> with a bounded wait:
    /// <c>SET LOCAL lock_timeout</c> first, so a promotion lock that is not granted within
    /// <paramref name="lockWait"/> raises <see cref="LockNotAvailableSqlState"/>. Once the promotion locks are
    /// taken the timeout is reset to the server default (QA-9), so a later row lock of the transaction is not
    /// turned into a busy answer. Needs an open transaction.
    /// </summary>
    public static async Task LockPromotionsAsync(DbContext db, IEnumerable<Guid?> promotionIds, TimeSpan lockWait, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A promotion lock needs an open transaction.");
        }

        if (db.Database.ProviderName == NpgsqlProvider)
        {
            // An integer formatted here, never input; at least 1 ms, as 0 would mean "wait forever".
            long ms = Math.Max(1L, (long)Math.Ceiling(lockWait.TotalMilliseconds));
            string sql = "SET LOCAL lock_timeout = '" + ms.ToString(CultureInfo.InvariantCulture) + "ms'";
#pragma warning disable EF1002 // No user input: a formatted integer.
            await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
#pragma warning restore EF1002
        }

        await LockPromotionsAsync(db, promotionIds, ct).ConfigureAwait(false);

        if (db.Database.ProviderName == NpgsqlProvider)
        {
            // QA-9: the bound covers the promotion locks only. Later row locks of this transaction wait as the server default.
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout TO DEFAULT", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>SET LOCAL lock_timeout</c> to <paramref name="wait"/> (at least 1 ms, formatted here). Npgsql only; needs an
    /// open transaction. Used for the hourly counter lock, which is taken after the promotion locks.
    /// </summary>
    public static async Task SetLockTimeoutAsync(DbContext db, TimeSpan wait, CancellationToken ct)
    {
        if (db.Database.ProviderName == NpgsqlProvider)
        {
            long ms = Math.Max(1L, (long)Math.Ceiling(wait.TotalMilliseconds));
            string sql = "SET LOCAL lock_timeout = '" + ms.ToString(CultureInfo.InvariantCulture) + "ms'";
#pragma warning disable EF1002 // No user input: a formatted integer.
            await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
#pragma warning restore EF1002
        }
    }

    /// <summary>Resets <c>lock_timeout</c> to the server default for the rest of the transaction (Npgsql only).</summary>
    public static async Task ResetLockTimeoutAsync(DbContext db, CancellationToken ct)
    {
        if (db.Database.ProviderName == NpgsqlProvider)
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout TO DEFAULT", ct).ConfigureAwait(false);
        }
    }

    /// <summary>True when <paramref name="error"/> (or one it wraps) is a Postgres lock wait past <c>lock_timeout</c>.</summary>
    public static bool IsLockTimeout(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: LockNotAvailableSqlState })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes the in-process checkout gate of every distinct promotion in <paramref name="promotionIds"/>, in
    /// the same ordinal key order as the advisory locks, all within <paramref name="wait"/> in total. Null
    /// when one is not free in time (the gates already taken are given back); otherwise a handle that
    /// releases them all. Take it before opening the transaction.
    /// </summary>
    public static async Task<IDisposable?> EnterCheckoutGatesAsync(IEnumerable<Guid?> promotionIds, TimeSpan wait, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + wait;
        List<SemaphoreSlim> taken = [];
        try
        {
            foreach (string key in OrderedKeys(promotionIds))
            {
                SemaphoreSlim gate = CheckoutGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (!await gate.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct).ConfigureAwait(false))
                {
                    Release(taken);
                    return null;
                }

                taken.Add(gate);
            }
        }
        catch
        {
            Release(taken);
            throw;
        }

        return new GateLease(taken);
    }

    private static void Release(List<SemaphoreSlim> taken)
    {
        for (int i = taken.Count - 1; i >= 0; i--)
        {
            taken[i].Release();
        }

        taken.Clear();
    }

    private sealed class GateLease(List<SemaphoreSlim> taken) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release(taken);
            }
        }
    }

    /// <summary>Takes one transaction-scoped advisory lock (re-entrant within the transaction).</summary>
    public static async Task LockAsync(DbContext db, string key, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A promotion lock needs an open transaction.");
        }

        if (db.Database.ProviderName == NpgsqlProvider)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct).ConfigureAwait(false);
        }
    }
}
