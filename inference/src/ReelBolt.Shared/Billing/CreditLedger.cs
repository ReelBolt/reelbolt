using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Shared.Billing;

/// <summary>Outcome of one ledger operation. A refusal (<see cref="Ok"/> false) writes nothing.</summary>
/// <param name="Ok">True when the operation applied or was an idempotent replay.</param>
/// <param name="Error">A stable code when refused (<see cref="CreditLedgerErrors"/>).</param>
/// <param name="Entry">The written (or, on replay, the original) entry.</param>
/// <param name="BalanceMillis">Account balance after the operation.</param>
/// <param name="ReservedMillis">Account reserved hold after the operation.</param>
/// <param name="Replayed">True when the idempotency key had already been applied.</param>
/// <param name="ApiKeyBudget">Set with <see cref="CreditLedgerErrors.ApiKeyBudgetExceeded"/>: which window refused and why.</param>
public sealed record CreditLedgerResult(
    bool Ok,
    string? Error,
    CreditLedgerEntry? Entry,
    long BalanceMillis,
    long ReservedMillis,
    bool Replayed,
    ApiKeyBudgetRefusal? ApiKeyBudget = null)
{
    /// <summary>Spendable now: balance minus open reservations.</summary>
    public long AvailableMillis => BalanceMillis - ReservedMillis;
}

/// <summary>Attribution carried onto a ledger entry. All optional, never a foreign key.</summary>
public readonly record struct CreditLedgerContext(Guid? ProjectId = null, Guid? ExecutionId = null, Guid? StepResultId = null, string? Note = null, Guid? ApiKeyId = null);

/// <summary>Stable refusal codes.</summary>
public static class CreditLedgerErrors
{
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string InsufficientCredits = "INSUFFICIENT_CREDITS";
    public const string ReservationNotFound = "RESERVATION_NOT_FOUND";
    public const string ReservationClosed = "RESERVATION_CLOSED";
    public const string SettlementNotFound = "SETTLEMENT_NOT_FOUND";
    public const string RefundExceedsSettled = "REFUND_EXCEEDS_SETTLED";
    public const string ApiKeyInactive = "API_KEY_INACTIVE";
    public const string ApiKeyBudgetExceeded = "API_KEY_BUDGET_EXCEEDED";
}

/// <summary>
/// The credit ledger (B5, decision D8). Every operation runs in one transaction under a per-org
/// <c>pg_advisory_xact_lock(hashtext(org_id))</c>, appends an immutable double-entry
/// <see cref="CreditLedgerEntry"/> and updates the <see cref="CreditAccount"/> projection in that
/// same transaction, so the balance never disagrees with the ledger.
/// <para>
/// Reserve holds credits before a step; Settle releases the hold and charges the actual amount (which
/// may exceed the hold: the overage is charged and the balance may go negative, the policy floor
/// belongs to the B6a gate); Release drops a hold; Refund returns settled credits. Credits live in
/// <see cref="CreditGrant"/> buckets consumed non-top-up first, earliest expiry first. Every call
/// takes an idempotency key: replaying a key returns the original entry and changes nothing.
/// </para>
/// <para>Use a dedicated, short-lived context (one per call or per scope): a refusal or an exception
/// leaves the caller's change tracker untouched only when nothing else shares it.</para>
/// </summary>
public sealed class CreditLedger(DbContext db, TimeProvider? time = null)
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>How long credits restored by a Refund live when their original grant has already expired.</summary>
    public static readonly TimeSpan RefundGrace = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Allocation(Guid GrantId, long Millis);

    /// <summary>Adds a bucket of credits (Trial, plan refill, top-up, promo). Negative debt is absorbed first.</summary>
    public Task<CreditLedgerResult> GrantAsync(
        Guid organizationId,
        CreditGrantSource source,
        long amountMillis,
        DateTime? expiresAt,
        string idempotencyKey,
        CreditLedgerEntryKind? kind = null,
        CreditLedgerContext context = default,
        CancellationToken ct = default)
    {
        CreditLedgerEntryKind entryKind = kind ?? source switch
        {
            CreditGrantSource.TopUp => CreditLedgerEntryKind.TopUp,
            CreditGrantSource.Plan => CreditLedgerEntryKind.PlanRefill,
            _ => CreditLedgerEntryKind.Grant
        };
        if (entryKind is not (CreditLedgerEntryKind.Grant or CreditLedgerEntryKind.TopUp or CreditLedgerEntryKind.PlanRefill))
        {
            throw new ArgumentException("A grant is recorded as Grant, TopUp or PlanRefill.", nameof(kind));
        }

        return RunAsync(organizationId, idempotencyKey, async account =>
        {
            if (amountMillis <= 0)
            {
                return Refuse(account, CreditLedgerErrors.InvalidAmount);
            }

            DateTime now = Now();
            CreditGrant grant = AddGrant(account, source, amountMillis, expiresAt, now);
            long balanceBefore = account.BalanceMillis;
            // sum(remaining) == max(balance, 0): credits arriving over a debt first repay it.
            grant.RemainingMillis = Math.Max(0, balanceBefore + amountMillis) - Math.Max(0, balanceBefore);
            CreditLedgerEntry entry = AddEntry(account, entryKind, amountMillis, 0, idempotencyKey, context, now);
            entry.GrantId = grant.Id;
            return Ok(account, entry);
        }, ct);
    }

    /// <summary>
    /// Holds <paramref name="amountMillis"/> before a step. Refused with
    /// <see cref="CreditLedgerErrors.InsufficientCredits"/> unless <c>available - amount &gt;=
    /// minAvailableAfterMillis</c> (default 0; the B6a gate passes a negative floor for overdraft tolerance).
    /// Expired grants are swept first so they cannot back the hold.
    /// </summary>
    public Task<CreditLedgerResult> ReserveAsync(
        Guid organizationId,
        long amountMillis,
        string idempotencyKey,
        CreditLedgerContext context = default,
        long minAvailableAfterMillis = 0,
        CancellationToken ct = default)
        => RunAsync(organizationId, idempotencyKey, async account =>
        {
            if (amountMillis <= 0)
            {
                return Refuse(account, CreditLedgerErrors.InvalidAmount);
            }

            DateTime now = Now();
            // The key check runs under the org lock, in the same transaction as the hold: N concurrent holds can never pass the cap.
            if (context.ApiKeyId is { } apiKeyId)
            {
                // A projection, never the whole row: the engine's database role has no SELECT on
                // api_keys.secret_hash (infra/postgres/engine-role.sql), and this check needs only these columns.
                ApiKey? key = await db.Set<ApiKey>().IgnoreQueryFilters().AsNoTracking()
                    .Where(k => k.Id == apiKeyId && k.OrganizationId == organizationId)
                    .Select(k => new ApiKey
                    {
                        Id = k.Id,
                        OrganizationId = k.OrganizationId,
                        ExpiresAt = k.ExpiresAt,
                        RevokedAt = k.RevokedAt,
                        DailyCapMillis = k.DailyCapMillis,
                        WeeklyCapMillis = k.WeeklyCapMillis,
                        MonthlyCapMillis = k.MonthlyCapMillis
                    })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (key is null || key.RevokedAt is not null || (key.ExpiresAt is not null && key.ExpiresAt <= now))
                {
                    return Refuse(account, CreditLedgerErrors.ApiKeyInactive);
                }

                long[] used = (await ApiKeyBudgetWindows.UsedAsync(db, [apiKeyId], now, ct).ConfigureAwait(false))[apiKeyId];
                ApiKeyBudgetRefusal? exceeded = ApiKeyBudgetWindows.FirstExceeded(key, used, amountMillis, now);
                if (exceeded is not null)
                {
                    return Refuse(account, CreditLedgerErrors.ApiKeyBudgetExceeded) with { ApiKeyBudget = exceeded };
                }
            }

            await SweepExpiredAsync(account, now, ct).ConfigureAwait(false);
            if (account.BalanceMillis - account.ReservedMillis - amountMillis < minAvailableAfterMillis)
            {
                return Refuse(account, CreditLedgerErrors.InsufficientCredits);
            }

            CreditLedgerEntry reserve = AddEntry(account, CreditLedgerEntryKind.Reserve, 0, amountMillis, idempotencyKey, context, now);
            if (context.ApiKeyId is { } spendKeyId)
            {
                db.Set<ApiKeySpend>().Add(new ApiKeySpend
                {
                    Id = Guid.NewGuid(),
                    ApiKeyId = spendKeyId,
                    OrganizationId = organizationId,
                    ReservationEntryId = reserve.Id,
                    HeldMillis = amountMillis,
                    Status = ApiKeySpendStatus.Open,
                    CreatedAt = now
                });
            }

            return Ok(account, reserve);
        }, ct);

    /// <summary>Closes a reservation: drops the hold and charges <paramref name="actualMillis"/> (0 allowed) against the grants.</summary>
    public Task<CreditLedgerResult> SettleAsync(
        Guid organizationId,
        Guid reservationEntryId,
        long actualMillis,
        string idempotencyKey,
        CreditLedgerContext context = default,
        CancellationToken ct = default)
        => RunAsync(organizationId, idempotencyKey, async account =>
        {
            if (actualMillis < 0)
            {
                return Refuse(account, CreditLedgerErrors.InvalidAmount);
            }

            (CreditLedgerEntry? reservation, string? error) = await FindOpenReservationAsync(account, reservationEntryId, ct).ConfigureAwait(false);
            if (reservation is null)
            {
                return Refuse(account, error!);
            }

            DateTime now = Now();
            List<Allocation> drawn = await ConsumeAsync(account, actualMillis, now, ct).ConfigureAwait(false);
            CreditLedgerEntry entry = AddEntry(account, CreditLedgerEntryKind.Settle, -actualMillis, -reservation.ReservedDeltaMillis, idempotencyKey, Inherit(context, reservation), now);
            entry.RefEntryId = reservation.Id;
            entry.AllocationsJson = JsonSerializer.Serialize(drawn, Json);
            await CloseSpendAsync(reservation.Id, ApiKeySpendStatus.Settled, actualMillis, now, ct).ConfigureAwait(false);
            return Ok(account, entry);
        }, ct);

    /// <summary>Drops a reservation without charging (cancelled or failed before any consumption).</summary>
    public Task<CreditLedgerResult> ReleaseAsync(
        Guid organizationId,
        Guid reservationEntryId,
        string idempotencyKey,
        CreditLedgerContext context = default,
        CancellationToken ct = default)
        => RunAsync(organizationId, idempotencyKey, async account =>
        {
            (CreditLedgerEntry? reservation, string? error) = await FindOpenReservationAsync(account, reservationEntryId, ct).ConfigureAwait(false);
            if (reservation is null)
            {
                return Refuse(account, error!);
            }

            DateTime now = Now();
            CreditLedgerEntry entry = AddEntry(account, CreditLedgerEntryKind.Release, 0, -reservation.ReservedDeltaMillis, idempotencyKey, Inherit(context, reservation), now);
            entry.RefEntryId = reservation.Id;
            await CloseSpendAsync(reservation.Id, ApiKeySpendStatus.Released, 0, now, ct).ConfigureAwait(false);
            return Ok(account, entry);
        }, ct);

    /// <summary>
    /// Returns up to the settled amount (cumulatively across refunds of the same settlement). The
    /// overdrawn part goes first, then the grants the settlement drew from, newest draw first; a grant
    /// that has since expired is replaced by a new Promo bucket living <see cref="RefundGrace"/>.
    /// </summary>
    public Task<CreditLedgerResult> RefundAsync(
        Guid organizationId,
        Guid settleEntryId,
        long amountMillis,
        string idempotencyKey,
        CreditLedgerContext context = default,
        CancellationToken ct = default)
        => RunAsync(organizationId, idempotencyKey, async account =>
        {
            if (amountMillis <= 0)
            {
                return Refuse(account, CreditLedgerErrors.InvalidAmount);
            }

            CreditLedgerEntry? settle = await db.Set<CreditLedgerEntry>()
                .FirstOrDefaultAsync(e => e.Id == settleEntryId && e.AccountId == account.Id && e.Kind == CreditLedgerEntryKind.Settle, ct)
                .ConfigureAwait(false);
            if (settle is null)
            {
                return Refuse(account, CreditLedgerErrors.SettlementNotFound);
            }

            List<CreditLedgerEntry> prior = await db.Set<CreditLedgerEntry>()
                .Where(e => e.RefEntryId == settle.Id && e.Kind == CreditLedgerEntryKind.Refund)
                .ToListAsync(ct).ConfigureAwait(false);

            long settled = -settle.BalanceDeltaMillis;
            long refundedSoFar = prior.Sum(e => e.BalanceDeltaMillis);
            if (refundedSoFar + amountMillis > settled)
            {
                return Refuse(account, CreditLedgerErrors.RefundExceedsSettled);
            }

            List<Allocation> drawn = Deserialize(settle.AllocationsJson);
            Dictionary<Guid, long> restoredPerGrant = [];
            long priorDrawnRestored = 0;
            foreach (CreditLedgerEntry p in prior)
            {
                foreach (Allocation a in Deserialize(p.AllocationsJson))
                {
                    restoredPerGrant[a.GrantId] = restoredPerGrant.GetValueOrDefault(a.GrantId) + a.Millis;
                    priorDrawnRestored += a.Millis;
                }
            }

            long undrawnTotal = settled - drawn.Sum(a => a.Millis);
            long undrawnLeft = undrawnTotal - (refundedSoFar - priorDrawnRestored);
            long left = amountMillis - Math.Min(amountMillis, undrawnLeft);

            DateTime now = Now();
            List<Allocation> restored = [];
            List<Guid> ids = drawn.Select(a => a.GrantId).ToList();
            Dictionary<Guid, CreditGrant> grants = await db.Set<CreditGrant>().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);
            long toNewBucket = 0;
            for (int i = drawn.Count - 1; i >= 0 && left > 0; i--)
            {
                Allocation a = drawn[i];
                long room = a.Millis - restoredPerGrant.GetValueOrDefault(a.GrantId);
                long take = Math.Min(room, left);
                if (take <= 0)
                {
                    continue;
                }

                left -= take;
                restored.Add(new Allocation(a.GrantId, take));
                if (grants.TryGetValue(a.GrantId, out CreditGrant? g) && g.ExpiredAt is null && (g.ExpiresAt is null || g.ExpiresAt > now))
                {
                    g.RemainingMillis += take;
                }
                else
                {
                    toNewBucket += take;
                }
            }

            // The undrawn (overdraft) part only repays the debt; whatever the balance now exceeds the
            // grants by must live in a bucket (the invariant), so it too goes to a grace bucket.
            long sumRemaining = await SumRemainingAsync(account, ct).ConfigureAwait(false) + toNewBucket;
            long surplus = Math.Max(0, account.BalanceMillis + amountMillis) - sumRemaining;
            long bucket = toNewBucket + Math.Max(0, surplus);
            if (bucket > 0)
            {
                CreditGrant fresh = AddGrant(account, CreditGrantSource.Promo, bucket, now + RefundGrace, now);
                fresh.RemainingMillis = bucket;
            }

            CreditLedgerEntry entry = AddEntry(account, CreditLedgerEntryKind.Refund, amountMillis, 0, idempotencyKey, Inherit(context, settle), now);
            entry.RefEntryId = settle.Id;
            entry.AllocationsJson = JsonSerializer.Serialize(restored, Json);
            return Ok(account, entry);
        }, ct);

    /// <summary>Manual correction: positive adds a never-expiring Promo bucket, negative consumes like a settlement.</summary>
    public Task<CreditLedgerResult> AdjustAsync(
        Guid organizationId,
        long deltaMillis,
        string idempotencyKey,
        CreditLedgerContext context = default,
        CancellationToken ct = default)
        => RunAsync(organizationId, idempotencyKey, async account =>
        {
            if (deltaMillis == 0)
            {
                return Refuse(account, CreditLedgerErrors.InvalidAmount);
            }

            DateTime now = Now();
            long balanceBefore = account.BalanceMillis;
            CreditGrant? grant = null;
            List<Allocation>? drawn = null;
            if (deltaMillis > 0)
            {
                grant = AddGrant(account, CreditGrantSource.Promo, deltaMillis, null, now);
                grant.RemainingMillis = Math.Max(0, balanceBefore + deltaMillis) - Math.Max(0, balanceBefore);
            }
            else
            {
                drawn = await ConsumeAsync(account, -deltaMillis, now, ct).ConfigureAwait(false);
            }

            CreditLedgerEntry entry = AddEntry(account, CreditLedgerEntryKind.Adjust, deltaMillis, 0, idempotencyKey, context, now);
            entry.GrantId = grant?.Id;
            entry.AllocationsJson = drawn is null ? null : JsonSerializer.Serialize(drawn, Json);
            return Ok(account, entry);
        }, ct);

    /// <summary>
    /// Zeroes every expired bucket of the org, one <c>Expire</c> entry each (keyed by the grant, so a
    /// rerun is a no-op). Returns the total millicredits expired.
    /// </summary>
    public async Task<long> ExpireAsync(Guid organizationId, CancellationToken ct = default)
    {
        long expired = 0;
        await RunAsync(organizationId, null, async account =>
        {
            expired = await SweepExpiredAsync(account, Now(), ct).ConfigureAwait(false);
            return Ok(account, null);
        }, ct).ConfigureAwait(false);
        return expired;
    }

    /// <summary>The org's current balance, hold and availability (zeros when it has no account yet).</summary>
    public async Task<CreditLedgerResult> GetBalanceAsync(Guid organizationId, CancellationToken ct = default)
    {
        CreditAccount? account = await db.Set<CreditAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.OrganizationId == organizationId, ct).ConfigureAwait(false);
        long balance = account?.BalanceMillis ?? 0;
        if (account is not null)
        {
            // Read-only mirror of SweepExpiredAsync: a grant past its expires_at is not spendable (reserve sweeps it first),
            // so a reader must not count what is left of it either. Nothing is written; the sweep stays lazy.
            DateTime now = Now();
            List<long> due = await db.Set<CreditGrant>().AsNoTracking()
                .Where(g => g.AccountId == account.Id && g.ExpiredAt == null && g.ExpiresAt != null && g.ExpiresAt <= now && g.RemainingMillis > 0)
                .Select(g => g.RemainingMillis).ToListAsync(ct).ConfigureAwait(false);
            balance -= due.Sum();
        }

        return new CreditLedgerResult(true, null, null, balance, account?.ReservedMillis ?? 0, false);
    }

    /// <summary>
    /// Audit check: the projection equals the sum of the entries' deltas and the buckets satisfy
    /// <c>sum(remaining) == max(balance, 0)</c>.
    /// </summary>
    public async Task<bool> IsConsistentAsync(Guid organizationId, CancellationToken ct = default)
    {
        CreditAccount? account = await db.Set<CreditAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.OrganizationId == organizationId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return true;
        }

        List<CreditLedgerEntry> entries = await db.Set<CreditLedgerEntry>().AsNoTracking().Where(e => e.AccountId == account.Id).ToListAsync(ct).ConfigureAwait(false);
        long remaining = (await db.Set<CreditGrant>().AsNoTracking().Where(g => g.AccountId == account.Id).Select(g => g.RemainingMillis).ToListAsync(ct).ConfigureAwait(false)).Sum();
        return entries.Sum(e => e.BalanceDeltaMillis) == account.BalanceMillis
            && entries.Sum(e => e.ReservedDeltaMillis) == account.ReservedMillis
            && remaining == Math.Max(0, account.BalanceMillis);
    }

    // ---- internals ----

    /// <summary>Closes the key's spend row of a reservation, if it has one (a reservation without a key has none).</summary>
    private async Task CloseSpendAsync(Guid reservationEntryId, ApiKeySpendStatus status, long chargedMillis, DateTime now, CancellationToken ct)
    {
        ApiKeySpend? spend = await db.Set<ApiKeySpend>().FirstOrDefaultAsync(s => s.ReservationEntryId == reservationEntryId, ct).ConfigureAwait(false);
        if (spend is null)
        {
            return;
        }

        spend.Status = status;
        spend.ChargedMillis = chargedMillis;
        spend.ClosedAt = now;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private static CreditLedgerContext Inherit(CreditLedgerContext context, CreditLedgerEntry from)
        => new(context.ProjectId ?? from.ProjectId, context.ExecutionId ?? from.ExecutionId, context.StepResultId ?? from.StepResultId, context.Note);

    private static List<Allocation> Deserialize(string? json)
        => string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<Allocation>>(json, Json) ?? [];

    private CreditLedgerResult Ok(CreditAccount account, CreditLedgerEntry? entry)
        => new(true, null, entry, account.BalanceMillis, account.ReservedMillis, false);

    private CreditLedgerResult Refuse(CreditAccount account, string error)
        => new(false, error, null, account.BalanceMillis, account.ReservedMillis, false);

    private CreditGrant AddGrant(CreditAccount account, CreditGrantSource source, long amount, DateTime? expiresAt, DateTime now)
    {
        CreditGrant grant = new()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            OrganizationId = account.OrganizationId,
            Source = source,
            AmountMillis = amount,
            RemainingMillis = amount,
            ExpiresAt = expiresAt,
            CreatedAt = now
        };
        db.Set<CreditGrant>().Add(grant);
        return grant;
    }

    private CreditLedgerEntry AddEntry(CreditAccount account, CreditLedgerEntryKind kind, long balanceDelta, long reservedDelta, string? key, CreditLedgerContext context, DateTime now)
    {
        CreditLedgerEntry entry = new()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            OrganizationId = account.OrganizationId,
            Kind = kind,
            BalanceDeltaMillis = balanceDelta,
            ReservedDeltaMillis = reservedDelta,
            BalanceAfterMillis = account.BalanceMillis + balanceDelta,
            ProjectId = context.ProjectId,
            ExecutionId = context.ExecutionId,
            StepResultId = context.StepResultId,
            Note = context.Note,
            IdempotencyKey = key ?? $"auto:{Guid.NewGuid():N}",
            CreatedAt = now
        };
        // The projection moves with its entry, so the two can never be written apart.
        account.BalanceMillis += balanceDelta;
        account.ReservedMillis += reservedDelta;
        db.Set<CreditLedgerEntry>().Add(entry);
        return entry;
    }

    private async Task<(CreditLedgerEntry? Reservation, string? Error)> FindOpenReservationAsync(CreditAccount account, Guid id, CancellationToken ct)
    {
        CreditLedgerEntry? reservation = await db.Set<CreditLedgerEntry>()
            .FirstOrDefaultAsync(e => e.Id == id && e.AccountId == account.Id && e.Kind == CreditLedgerEntryKind.Reserve, ct)
            .ConfigureAwait(false);
        if (reservation is null)
        {
            return (null, CreditLedgerErrors.ReservationNotFound);
        }

        bool closed = await db.Set<CreditLedgerEntry>()
            .AnyAsync(e => e.RefEntryId == id && (e.Kind == CreditLedgerEntryKind.Settle || e.Kind == CreditLedgerEntryKind.Release), ct)
            .ConfigureAwait(false);
        return closed ? (null, CreditLedgerErrors.ReservationClosed) : (reservation, null);
    }

    private async Task<long> SumRemainingAsync(CreditAccount account, CancellationToken ct)
    {
        // Pending (unsaved) grants and in-memory restores live in the change tracker, so read it as well.
        List<CreditGrant> grants = await db.Set<CreditGrant>().Where(g => g.AccountId == account.Id && g.RemainingMillis > 0).ToListAsync(ct).ConfigureAwait(false);
        IEnumerable<CreditGrant> pending = db.ChangeTracker.Entries<CreditGrant>()
            .Where(e => (e.State == EntityState.Added || e.State == EntityState.Modified) && e.Entity.AccountId == account.Id && e.Entity.RemainingMillis > 0)
            .Select(e => e.Entity);
        return grants.Concat(pending).DistinctBy(g => g.Id).Sum(g => g.RemainingMillis);
    }

    /// <summary>Draws <paramref name="amount"/> from the buckets in consumption order; any shortfall is overdraft.</summary>
    private async Task<List<Allocation>> ConsumeAsync(CreditAccount account, long amount, DateTime now, CancellationToken ct)
    {
        List<Allocation> drawn = [];
        if (amount <= 0)
        {
            return drawn;
        }

        List<CreditGrant> buckets = await db.Set<CreditGrant>().Where(g => g.AccountId == account.Id && g.RemainingMillis > 0).ToListAsync(ct).ConfigureAwait(false);
        long left = amount;
        foreach (CreditGrant g in buckets
                     .OrderBy(g => g.Source == CreditGrantSource.TopUp ? 1 : 0)
                     .ThenBy(g => g.ExpiresAt ?? DateTime.MaxValue)
                     .ThenBy(g => g.CreatedAt)
                     .ThenBy(g => g.Id))
        {
            if (left == 0)
            {
                break;
            }

            long take = Math.Min(g.RemainingMillis, left);
            g.RemainingMillis -= take;
            left -= take;
            drawn.Add(new Allocation(g.Id, take));
        }

        return drawn;
    }

    private async Task<long> SweepExpiredAsync(CreditAccount account, DateTime now, CancellationToken ct)
    {
        List<CreditGrant> due = await db.Set<CreditGrant>()
            .Where(g => g.AccountId == account.Id && g.ExpiredAt == null && g.ExpiresAt != null && g.ExpiresAt <= now)
            .ToListAsync(ct).ConfigureAwait(false);
        long total = 0;
        foreach (CreditGrant g in due.OrderBy(g => g.ExpiresAt))
        {
            long remaining = g.RemainingMillis;
            g.ExpiredAt = now;
            if (remaining <= 0)
            {
                continue;
            }

            g.RemainingMillis = 0;
            CreditLedgerEntry entry = AddEntry(account, CreditLedgerEntryKind.Expire, -remaining, 0, $"expire:{g.Id:N}", default, now);
            entry.GrantId = g.Id;
            total += remaining;
        }

        return total;
    }

    /// <summary>
    /// Runs <paramref name="body"/> against the org's account inside a transaction holding the org lock,
    /// replaying a previously applied idempotency key instead of re-running it.
    /// </summary>
    private async Task<CreditLedgerResult> RunAsync(Guid organizationId, string? idempotencyKey, Func<CreditAccount, Task<CreditLedgerResult>> body, CancellationToken ct)
    {
        if (idempotencyKey is { Length: 0 })
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? owned = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        try
        {
            if (db.Database.ProviderName == NpgsqlProvider)
            {
                string lockKey = organizationId.ToString();
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);
            }

            CreditAccount account = await GetOrCreateAccountAsync(organizationId, ct).ConfigureAwait(false);

            if (idempotencyKey is not null)
            {
                CreditLedgerEntry? existing = await db.Set<CreditLedgerEntry>().AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.OrganizationId != organizationId)
                    {
                        throw new InvalidOperationException("Idempotency key already used by another organization.");
                    }

                    if (owned is not null)
                    {
                        await owned.RollbackAsync(ct).ConfigureAwait(false);
                    }

                    return new CreditLedgerResult(true, null, existing, account.BalanceMillis, account.ReservedMillis, true);
                }
            }

            CreditLedgerResult result = await body(account).ConfigureAwait(false);
            // A refusal can still carry the sweep of expired buckets that preceded it; persist whatever happened.
            if (db.ChangeTracker.HasChanges())
            {
                account.RowVersion++;
                account.UpdatedAt = Now();
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            if (owned is not null)
            {
                await owned.CommitAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            if (owned is not null)
            {
                await owned.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<CreditAccount> GetOrCreateAccountAsync(Guid organizationId, CancellationToken ct)
    {
        CreditAccount? account = await db.Set<CreditAccount>().FirstOrDefaultAsync(a => a.OrganizationId == organizationId, ct).ConfigureAwait(false);
        if (account is not null)
        {
            return account;
        }

        account = new CreditAccount { Id = Guid.NewGuid(), OrganizationId = organizationId, UpdatedAt = Now() };
        db.Set<CreditAccount>().Add(account);
        // Persist now so the (account) row exists before entries reference it, even for a refused call.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return account;
    }
}
