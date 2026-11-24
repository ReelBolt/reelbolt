using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>Promotions: admin create/edit, code redemption, checkout resolution and admin credit grants.</summary>
public interface IPromotionService
{
    /// <summary>Redeems a CreditCode for the org: one Promo ledger grant, at most the per-org and global limits.</summary>
    Task<PromotionOutcome<RedeemResult>> RedeemAsync(Guid orgId, Guid userId, string? code, CancellationToken ct);

    /// <summary>
    /// The discount (and, for a top-up, the bonus) a checkout carries. A supplied code that does not
    /// apply is <see cref="PromotionErrors.Invalid"/>, never silently dropped; with no code the matching
    /// automatic promotion applies, if any.
    /// </summary>
    Task<PromotionOutcome<CheckoutPromotions>> ResolveCheckoutPromotionAsync(Guid orgId, Guid userId, CheckoutTarget target, string? code, CancellationToken ct);

    /// <summary>
    /// Reserves the resolved promotions for a checkout about to be created (contract C8): the caller's
    /// own open reservations of these promotions are superseded, every cap is re-checked and the
    /// <see cref="PromotionCheckout"/> row is inserted in one transaction under each promotion's advisory
    /// lock. Rate-limited per user: only a checkout that reserves is counted, so a busy answer or a promotion that
    /// no longer applies is not, and a user over the hourly limit is refused before any promotion gate or lock. The count joins this
    /// checkout's transaction, which relies on the counter store sharing the scoped <see cref="InferenceApiDbContext"/>.
    /// Nothing is reserved (and nothing counted) when no promotion is attached.
    /// </summary>
    Task<PromotionOutcome<CheckoutReservation>> ReserveCheckoutAsync(Guid orgId, Guid userId, CheckoutTarget target, CheckoutPromotions promos, CancellationToken ct);

    /// <summary>
    /// Stamps the provider's transaction id on a reservation once the provider created the checkout, and
    /// answers whether the reservation was still live (F-5). False means a newer checkout superseded it
    /// meanwhile: the transaction is recorded on the row and cancelled at once (best effort; the sweeper
    /// retries), and the caller must not hand out its checkout.
    /// </summary>
    Task<bool> AttachTransactionAsync(Guid reservationId, string transactionId, CancellationToken ct);

    /// <summary>Drops a reservation whose provider checkout was never created.</summary>
    Task ReleaseAsync(Guid reservationId, CancellationToken ct);

    /// <summary>
    /// Cancels the provider transactions of superseded reservations (contract C10). Best effort: never
    /// throws (short of the caller's cancellation); a failed cancel is retried by <see cref="PromotionCheckoutSweeper"/>.
    /// </summary>
    Task CancelSupersededAsync(IReadOnlyCollection<Guid> checkoutIds, CancellationToken ct);

    Task<PromotionOutcome<Promotion>> CreateAsync(CreatePromotionDto dto, Guid adminUserId, CancellationToken ct);

    Task<PromotionOutcome<Promotion>> UpdateAsync(Guid id, PromotionPatch patch, CancellationToken ct);

    Task<PromotionOutcome<AdminGrantOutcome>> GrantCreditsAsync(Guid orgId, GrantCreditsDto dto, Guid adminUserId, CancellationToken ct);

    /// <summary>Active, automatic (no code), in-window promotions of the checkout kinds, oldest first.</summary>
    Task<IReadOnlyList<Promotion>> ActiveAutomaticAsync(CancellationToken ct);
}

/// <summary>
/// The promotion rules (saas-promotions plan, Task 3). Codes exist only here: plan and pack discounts
/// are provider discounts this server attaches by id, and free credits are <see cref="CreditGrantSource.Promo"/>
/// ledger grants. Every code failure looks the same to the caller and counts towards the
/// <see cref="PromotionCodeAttemptLimiter"/>.
/// </summary>
public sealed partial class PromotionService(
    InferenceApiDbContext db,
    IEnumerable<IBillingProvider> providers,
    PromotionCodeAttemptLimiter limiter,
    ILogger<PromotionService> logger,
    TimeProvider? time = null,
    Microsoft.Extensions.Options.IOptions<PromotionOptions>? options = null,
    ReelBolt.Inference.Api.Services.Marketing.IMailRateCounterStore? counters = null,
    Microsoft.Extensions.Options.IOptions<BillingProviderOptions>? providerSelection = null) : IPromotionService
{
    public const int DefaultCreditExpiryDays = 90;
    public const int MaxCodeCredits = 100_000;
    public const int MaxCreditExpiryDays = 730;
    public const int MaxGrantCredits = 1_000_000;
    public const int MaxReasonLength = 500;

    private readonly IReadOnlyList<IBillingProvider> _providers = providers.ToList();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly PromotionOptions _options = options?.Value ?? new PromotionOptions();

    /// <summary>
    /// How long an unpaid checkout keeps its promotions reserved (<c>Billing:Promotions:CheckoutReservationMinutes</c>):
    /// an open <see cref="PromotionCheckout"/> counts against the global cap until its payment webhook
    /// lands, it is superseded, or this passes. Expired reservations never count.
    /// </summary>
    private TimeSpan ReservationLifetime => TimeSpan.FromMinutes(Math.Max(1, _options.CheckoutReservationMinutes));

    /// <summary>The C2 counter bucket of the promo checkout rate limit.</summary>
    public static string CheckoutBucket(Guid userId) => $"promo:checkout:user:{userId:D}";

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private IBillingProvider? Provider => BillingProviderSelector.Active(_providers, providerSelection?.Value);

    [GeneratedRegex("^[A-Z0-9]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    /// <summary>What a person typed, as stored: whitespace removed, uppercase. Null when nothing is left.</summary>
    public static string? NormalizeCode(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        string code = string.Concat(raw.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        return code.Length == 0 ? null : code;
    }

    private static bool InWindow(Promotion p, DateTime now)
        => (p.StartsAt is null || p.StartsAt <= now) && (p.EndsAt is null || p.EndsAt > now);

    private async Task<Promotion?> FindByCodeAsync(string code, CancellationToken ct)
        => CodePattern().IsMatch(code)
            ? await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct)
            : null;

    // ------------------------------------------------------------------ redeem

    /// <summary>Whether the account has verified its email, read from the database (never from the token).</summary>
    private Task<bool> IsVerifiedAsync(Guid userId, CancellationToken ct)
        => db.ApplicationUsers.AsNoTracking().AnyAsync(u => u.Id == userId && u.EmailVerifiedAt != null && !u.IsServiceAccount, ct);

    public async Task<PromotionOutcome<RedeemResult>> RedeemAsync(Guid orgId, Guid userId, string? rawCode, CancellationToken ct)
    {
        // Only verified accounts redeem (C8): throwaway signups must not farm credit codes.
        if (!await IsVerifiedAsync(userId, ct))
        {
            return PromotionErrors.EmailNotVerified;
        }

        // Counted before the lookup; only a successful redeem hands the attempt back. Unknown, paused,
        // exhausted and already-redeemed codes all stay counted.
        if (limiter.TryBegin(userId, orgId) is not { } attempt)
        {
            return PromotionErrors.RateLimited;
        }

        DateTime now = Now;
        string? code = NormalizeCode(rawCode);
        Promotion? promo = code is null ? null : await FindByCodeAsync(code, ct);
        if (promo is not { Status: PromotionStatus.Active, Kind: PromotionKind.CreditCode, CreditsMillis: > 0 } || !InWindow(promo, now))
        {
            return PromotionErrors.Invalid;
        }

        long credits = promo.CreditsMillis!.Value;
        DateTime expiresAt = now.AddDays(promo.CreditExpiryDays ?? DefaultCreditExpiryDays);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // S-04: the per-user cap is checked under the per-(promotion, user) lock, inside the redeem
            // transaction, so concurrent redeems from N orgs of one user serialise and the second sees the first.
            await PromotionLocks.LockAsync(db, PromotionLocks.UserKey(promo.Id, userId), ct);
            int prior = await db.PromotionRedemptions.CountAsync(
                r => r.PromotionId == promo.Id && r.OrganizationId == orgId && r.Kind == PromotionRedemptionKind.CreditCodeRedeemed
                    && r.Outcome == PromotionRedemptionOutcome.Granted, ct);
            int byUser = await db.PromotionRedemptions.CountAsync(
                r => r.PromotionId == promo.Id && r.UserId == userId && r.Kind == PromotionRedemptionKind.CreditCodeRedeemed
                    && r.Outcome == PromotionRedemptionOutcome.Granted, ct);

            // The same limit per person: creating Team orgs must not multiply a once-per-org code.
            if (prior >= promo.MaxRedemptionsPerOrg || byUser >= promo.MaxRedemptionsPerOrg)
            {
                await tx.RollbackAsync(ct);
                return PromotionErrors.AlreadyRedeemed;
            }

            // The global cap, enforced by one conditional UPDATE: of two racing redeems at the cap, one
            // sees zero rows affected.
            int claimed = await db.Promotions
                .Where(p => p.Id == promo.Id && (p.MaxRedemptions == null || p.RedemptionCount < p.MaxRedemptions))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, p => p.RedemptionCount + 1).SetProperty(p => p.UpdatedAt, now), ct);
            if (claimed == 0)
            {
                // Exhausted reads exactly like an unknown code, so the answer reveals nothing.
                await tx.RollbackAsync(ct);
                return PromotionErrors.Invalid;
            }

            // The per-org cap under concurrency: two redeems that both read the same prior count derive
            // the same idempotency key, so the ledger applies one and replays the other.
            CreditLedgerResult grant = await new CreditLedger(db, _time).GrantAsync(
                orgId,
                CreditGrantSource.Promo,
                credits,
                expiresAt,
                $"promo:{promo.Id}:{orgId}:{prior + 1}",
                context: new CreditLedgerContext(Note: $"promo code {promo.Code}"),
                ct: ct);
            if (grant.Replayed)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return PromotionErrors.AlreadyRedeemed;
            }

            if (!grant.Ok || grant.Entry is null)
            {
                throw new InvalidOperationException($"Promo grant refused: {grant.Error}");
            }

            db.PromotionRedemptions.Add(new PromotionRedemption
            {
                Id = Guid.NewGuid(),
                PromotionId = promo.Id,
                OrganizationId = orgId,
                UserId = userId,
                Kind = PromotionRedemptionKind.CreditCodeRedeemed,
                LedgerEntryId = grant.Entry.Id,
                CreditsMillis = credits,
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }

        limiter.Succeeded(attempt);
        return PromotionOutcome<RedeemResult>.Success(new RedeemResult(credits / 1000.0, expiresAt, promo.PublicHeadline));
    }

    // ------------------------------------------------------------------ checkout

    public async Task<PromotionOutcome<CheckoutPromotions>> ResolveCheckoutPromotionAsync(Guid orgId, Guid userId, CheckoutTarget target, string? rawCode, CancellationToken ct)
    {
        DateTime now = Now;
        Promotion? discount = null;
        Promotion? bonus = null;
        Guid? codePromotionId = null;
        string? code = NormalizeCode(rawCode);

        // C8: only verified accounts reserve or redeem. A typed code says so; an automatic promotion is
        // simply not applied.
        bool verified = await IsVerifiedAsync(userId, ct);
        if (code is not null)
        {
            if (!verified)
            {
                return PromotionErrors.EmailNotVerified;
            }

            if (limiter.TryBegin(userId, orgId) is not { } attempt)
            {
                return PromotionErrors.RateLimited;
            }

            Promotion? promo = await FindByCodeAsync(code, ct);
            if (promo is null || !await AppliesAsync(promo, orgId, userId, target, now, ct))
            {
                return PromotionErrors.Invalid;
            }

            limiter.Succeeded(attempt);
            codePromotionId = promo.Id;

            if (promo.Kind == PromotionKind.PackBonus)
            {
                bonus = promo;
            }
            else
            {
                discount = promo;
            }
        }

        if (verified)
        {
            discount ??= await AutomaticAsync(target.IsPack ? PromotionKind.PackDiscount : PromotionKind.PlanDiscount, orgId, userId, target, now, ct);
            if (target.IsPack)
            {
                bonus ??= await AutomaticAsync(PromotionKind.PackBonus, orgId, userId, target, now, ct);
            }
        }

        return PromotionOutcome<CheckoutPromotions>.Success(new CheckoutPromotions(discount, bonus, codePromotionId));
    }

    /// <summary>
    /// Active, in its window, the kind the target takes, targeting its price id, backed by a provider
    /// discount (a bonus needs none), and under every cap (contract C8):
    /// <list type="bullet">
    /// <item>global: completed uses plus the open reservations of OTHER buyers (unpaid, not superseded,
    /// younger than <see cref="ReservationLifetime"/>). The caller's own open reservations (same user or
    /// same org) are superseded by this checkout, so they never count against it (N-01);</item>
    /// <item>per org and per user: completed uses only, both against <see cref="Promotion.MaxRedemptionsPerOrg"/>,
    /// so a second org of the same person gets no second use (F-5). A NotGranted row is not a use (C10).</item>
    /// </list>
    /// An automatic promotion over a cap is simply skipped, a code is invalid.
    /// </summary>
    private async Task<bool> AppliesAsync(Promotion p, Guid orgId, Guid userId, CheckoutTarget target, DateTime now, CancellationToken ct)
    {
        bool kindMatches = target.IsPack
            ? p.Kind is PromotionKind.PackDiscount or PromotionKind.PackBonus
            : p.Kind == PromotionKind.PlanDiscount;
        if (p.Status != PromotionStatus.Active || !kindMatches || !InWindow(p, now)
            || !PromotionRules.ReadStrings(p.TargetPriceIdsJson).Contains(target.PriceId, StringComparer.Ordinal))
        {
            return false;
        }

        if (p.Kind == PromotionKind.PackBonus ? p.BonusPercent is not > 0 : string.IsNullOrEmpty(p.ProviderDiscountId))
        {
            return false;
        }

        bool isBonus = p.Kind == PromotionKind.PackBonus;
        if (p.MaxRedemptions is { } max)
        {
            DateTime openSince = now - ReservationLifetime;
            int othersOpen = await db.PromotionCheckouts.AsNoTracking()
                .Where(c => c.CompletedAt == null && c.SupersededAt == null && c.CreatedAt > openSince)
                .Where(c => isBonus ? c.BonusPromotionId == p.Id : c.DiscountPromotionId == p.Id)
                .Where(c => c.UserId != userId && c.OrganizationId != orgId)
                .CountAsync(ct);
            if (p.RedemptionCount + othersOpen >= max)
            {
                return false;
            }
        }

        PromotionRedemptionKind counted = isBonus ? PromotionRedemptionKind.BonusGranted : PromotionRedemptionKind.DiscountApplied;
        int byOrg = await db.PromotionRedemptions.CountAsync(
            r => r.PromotionId == p.Id && r.OrganizationId == orgId && r.Kind == counted && r.Outcome == PromotionRedemptionOutcome.Granted, ct);
        int byUser = await db.PromotionRedemptions.CountAsync(
            r => r.PromotionId == p.Id && r.UserId == userId && r.Kind == counted && r.Outcome == PromotionRedemptionOutcome.Granted, ct);
        return byOrg < p.MaxRedemptionsPerOrg && byUser < p.MaxRedemptionsPerOrg;
    }

    public async Task<PromotionOutcome<CheckoutReservation>> ReserveCheckoutAsync(Guid orgId, Guid userId, CheckoutTarget target, CheckoutPromotions promos, CancellationToken ct)
    {
        if (promos.IsEmpty)
        {
            return PromotionOutcome<CheckoutReservation>.Success(CheckoutReservation.None);
        }

        DateTime now = Now;
        DateTimeOffset decidedAt = _time.GetUtcNow();
        ReelBolt.Inference.Api.Services.Marketing.MailRateLimit checkoutLimit =
            new("promo_checkout", CheckoutBucket(userId), TimeSpan.FromHours(1), Math.Max(1, _options.CheckoutsPerUserPerHour));
        TimeSpan lockWait = TimeSpan.FromMilliseconds(Math.Max(1, _options.CheckoutLockWaitMilliseconds));
        if (counters is not null)
        {
            // C5 probe: a sliding hour. allow answers false, so this counts nothing; an over-limit user is refused
            // here, before any gate or promotion lock. The count is recorded under the locks, below, and only for a
            // checkout that reserves. The probe takes the hourly counter lock, so it runs in its own short
            // transaction bounded by CheckoutLockWaitMilliseconds: a wait past it is a busy answer, not a hang.
            await using var probeTx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await PromotionLocks.SetLockTimeoutAsync(db, lockWait, ct);
                ReelBolt.Inference.Api.Services.Marketing.MailRateDecision probe =
                    await counters.DecideAsync([checkoutLimit], decidedAt, _ => false, ct);
                await probeTx.RollbackAsync(ct);
                if (!probe.Estimates.All(e => e.Admits))
                {
                    return PromotionErrors.CheckoutRateLimited;
                }
            }
            catch (Exception e) when (PromotionLocks.IsLockTimeout(e))
            {
                await probeTx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                logger.LogInformation("Promotion checkout lock wait exceeded {LockWaitMs} ms; answering checkout_busy", (int)lockWait.TotalMilliseconds);
                return PromotionErrors.CheckoutBusy;
            }
        }

        Guid? discountId = promos.Discount?.Id;
        Guid? bonusId = promos.Bonus?.Id;

        // S-11: queue for the promotions in-process first, holding no pooled connection, and bound the
        // whole wait (gate plus database lock) by CheckoutLockWaitMilliseconds. A wait past it is a
        // retryable CheckoutBusy, never a pool timeout that surfaces as a 500.
        long waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using IDisposable? gates = await PromotionLocks.EnterCheckoutGatesAsync([discountId, bonusId], lockWait, ct);
        if (gates is null)
        {
            return PromotionErrors.CheckoutBusy;
        }

        TimeSpan remaining = lockWait - System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await PromotionLocks.LockPromotionsAsync(db, [discountId, bonusId], remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), ct);

            // At most one open reservation per (promotion, user) and per (promotion, org): this checkout
            // supersedes the caller's own (N-01). Superseded rows never reserve anything again, and their
            // provider transactions are cancelled once this checkout exists (C10).
            List<Guid> superseded = await db.PromotionCheckouts
                .Where(c => c.CompletedAt == null && c.SupersededAt == null && (c.UserId == userId || c.OrganizationId == orgId))
                .Where(c => (discountId != null && c.DiscountPromotionId == discountId) || (bonusId != null && c.BonusPromotionId == bonusId))
                .Select(c => c.Id)
                .ToListAsync(ct);
            if (superseded.Count > 0)
            {
                await db.PromotionCheckouts
                    .Where(c => superseded.Contains(c.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.SupersededAt, now), ct);
            }

            // Every cap again, under the locks, on fresh rows: two checkouts side by side cannot both
            // take the last use.
            Promotion? discount = null;
            Promotion? bonus = null;
            foreach (Promotion candidate in new[] { promos.Discount, promos.Bonus }.OfType<Promotion>())
            {
                Promotion? fresh = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == candidate.Id, ct);
                if (fresh is not null && await AppliesAsync(fresh, orgId, userId, target, now, ct))
                {
                    if (fresh.Kind == PromotionKind.PackBonus)
                    {
                        bonus = fresh;
                    }
                    else
                    {
                        discount = fresh;
                    }
                }
                else if (candidate.Id == promos.CodePromotionId)
                {
                    await tx.RollbackAsync(ct);
                    return PromotionErrors.Invalid;
                }
            }

            if (discount is null && bonus is null)
            {
                await tx.RollbackAsync(ct);
                return PromotionOutcome<CheckoutReservation>.Success(CheckoutReservation.None);
            }

            if (counters is not null)
            {
                // The count of a checkout that reserves, recorded under the locks above and in this transaction, so a
                // checkout that loses the hour to a concurrent one rolls back. Relies on the store sharing the scoped
                // InferenceApiDbContext: it joins this transaction.
                // Bounded by what is left of the same CheckoutLockWaitMilliseconds deadline; a wait past it is a 55P03,
                // which the catch below answers as busy after the rollback. The bound is reset afterwards (QA-9).
                TimeSpan leftForCounter = lockWait - System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted);
                await PromotionLocks.SetLockTimeoutAsync(db, leftForCounter > TimeSpan.Zero ? leftForCounter : TimeSpan.FromMilliseconds(1), ct);
                ReelBolt.Inference.Api.Services.Marketing.MailRateDecision recorded =
                    await counters.DecideAsync([checkoutLimit], decidedAt, estimates => estimates.All(e => e.Admits), ct);
                await PromotionLocks.ResetLockTimeoutAsync(db, ct);
                if (!recorded.Allowed)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogInformation("Promotion checkout refused under the lock: the hourly checkout limit was reached by a concurrent checkout");
                    return PromotionErrors.CheckoutRateLimited;
                }
            }

            PromotionCheckout row = new()
            {
                Id = Guid.NewGuid(),
                ProviderTransactionId = null,
                OrganizationId = orgId,
                UserId = userId,
                DiscountPromotionId = discount?.Id,
                BonusPromotionId = bonus?.Id,
                PriceId = target.PriceId,
                TopUpCredits = target.PackCredits,
                CreatedAt = now,
                // Inserted only after every cap of the attached promotions held, under their locks (C10).
                WithinCapsAtReservation = true
            };
            db.PromotionCheckouts.Add(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.Entry(row).State = EntityState.Detached;
            return PromotionOutcome<CheckoutReservation>.Success(
                new CheckoutReservation(row.Id, new CheckoutPromotions(discount, bonus, promos.CodePromotionId), superseded));
        }
        catch (Exception e) when (PromotionLocks.IsLockTimeout(e))
        {
            // Another replica held a promotion (or a row this checkout needed) past the wait: nothing was
            // reserved or superseded.
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            logger.LogInformation("Promotion checkout lock wait exceeded {LockWaitMs} ms; answering checkout_busy", (int)lockWait.TotalMilliseconds);
            return PromotionErrors.CheckoutBusy;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<bool> AttachTransactionAsync(Guid reservationId, string transactionId, CancellationToken ct)
    {
        // F-5: attach and check in one conditional UPDATE (the row lock orders it against a supersede). A
        // supersede that committed first makes it match nothing; one that commits later finds the
        // transaction id here and cancels it itself. No window is left in which neither side cancels.
        int live = await db.PromotionCheckouts
            .Where(c => c.Id == reservationId && c.SupersededAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProviderTransactionId, transactionId), ct);
        if (live > 0)
        {
            return true;
        }

        // Superseded while the provider created the checkout. Record the transaction anyway, so the
        // sweeper owes its cancel if the immediate one below fails.
        int recorded = await db.PromotionCheckouts
            .Where(c => c.Id == reservationId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProviderTransactionId, transactionId), ct);
        logger.LogWarning(
            "Promotion checkout {Checkout} was superseded while its provider transaction {Transaction} was created; cancelling it",
            reservationId, transactionId);
        if (recorded > 0)
        {
            await CancelSupersededAsync([reservationId], ct);
        }
        else if ((_providers.FirstOrDefault(p => p.IsConfigured && p.OwnsTransaction(transactionId)) ?? Provider) is { } provider)
        {
            // The row is gone (its org was deleted): nothing would ever sweep this transaction.
            try
            {
                await provider.CancelTransactionAsync(transactionId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Could not cancel provider transaction {Transaction} of a vanished promotion checkout ({Error})", transactionId, ex.GetType().Name);
            }
        }

        return false;
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct)
        => await db.PromotionCheckouts
            .Where(c => c.Id == reservationId && c.ProviderTransactionId == null)
            .ExecuteDeleteAsync(ct);

    public async Task CancelSupersededAsync(IReadOnlyCollection<Guid> checkoutIds, CancellationToken ct)
    {
        if (checkoutIds.Count == 0 || Provider is not { } provider)
        {
            return;
        }

        try
        {
            await PromotionCheckoutCancellation.CancelAsync(db, _providers, provider, checkoutIds, _time, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never fails the checkout that superseded them: the sweeper retries what is still owed.
            logger.LogWarning("Cancelling the provider transactions of superseded promotion checkouts failed ({Error}); the sweeper retries", ex.GetType().Name);
        }
    }

    private async Task<Promotion?> AutomaticAsync(PromotionKind kind, Guid orgId, Guid userId, CheckoutTarget target, DateTime now, CancellationToken ct)
    {
        List<Promotion> candidates = await db.Promotions.AsNoTracking()
            .Where(p => p.Kind == kind && p.Code == null && p.Status == PromotionStatus.Active)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        foreach (Promotion candidate in candidates)
        {
            if (await AppliesAsync(candidate, orgId, userId, target, now, ct))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<Promotion>> ActiveAutomaticAsync(CancellationToken ct)
    {
        DateTime now = Now;
        List<Promotion> active = await db.Promotions.AsNoTracking()
            .Where(p => p.Status == PromotionStatus.Active && p.Code == null && p.Kind != PromotionKind.CreditCode)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        return active.Where(p => InWindow(p, now)).ToList();
    }

    // ------------------------------------------------------------------ admin create / edit

    public async Task<PromotionOutcome<Promotion>> CreateAsync(CreatePromotionDto dto, Guid adminUserId, CancellationToken ct)
    {
        DateTime now = Now;
        List<string> errors = [];
        Promotion p = new() { Id = Guid.NewGuid(), Status = PromotionStatus.Active, CreatedAt = now, UpdatedAt = now, CreatedByUserId = adminUserId };

        if (!PromotionRules.TryParseEnum(dto.Kind, out PromotionKind kind))
        {
            errors.Add("kind must be creditCode, planDiscount, packDiscount or packBonus.");
        }

        p.Kind = kind;
        p.Name = dto.Name?.Trim() ?? string.Empty;
        if (p.Name.Length is 0 or > PromotionRules.MaxNameLength)
        {
            errors.Add($"name must be 1 to {PromotionRules.MaxNameLength} characters.");
        }

        p.PublicHeadline = string.IsNullOrWhiteSpace(dto.PublicHeadline) ? null : dto.PublicHeadline.Trim();
        if (p.PublicHeadline?.Length > PromotionRules.MaxNameLength)
        {
            errors.Add($"publicHeadline must be at most {PromotionRules.MaxNameLength} characters.");
        }

        p.Code = NormalizeCode(dto.Code);
        if (p.Code is not null && !CodePattern().IsMatch(p.Code))
        {
            errors.Add("code must be 4 to 32 letters or digits.");
        }

        p.StartsAt = dto.StartsAt is { } s ? PromotionRules.ToUtc(s) : null;
        p.EndsAt = dto.EndsAt is { } e ? PromotionRules.ToUtc(e) : null;
        if (p.StartsAt is { } start && p.EndsAt is { } end && start >= end)
        {
            errors.Add("startsAt must be before endsAt.");
        }

        if (p.EndsAt is { } ends && ends <= now)
        {
            errors.Add("endsAt must be in the future.");
        }

        p.MaxRedemptions = dto.MaxRedemptions;
        if (dto.MaxRedemptions is < 1)
        {
            errors.Add("maxRedemptions must be at least 1.");
        }

        p.MaxRedemptionsPerOrg = dto.MaxRedemptionsPerOrg ?? 1;
        if (p.MaxRedemptionsPerOrg < 1)
        {
            errors.Add("maxRedemptionsPerOrg must be at least 1.");
        }

        switch (kind)
        {
            case PromotionKind.CreditCode:
                ValidateCreditCode(dto, p, errors);
                break;
            case PromotionKind.PlanDiscount:
            case PromotionKind.PackDiscount:
                ValidateDiscount(dto, p, errors);
                break;
            case PromotionKind.PackBonus:
                if (dto.BonusPercent is not (>= 1 and <= 100))
                {
                    errors.Add("bonusPercent must be 1 to 100.");
                }

                p.BonusPercent = dto.BonusPercent;
                break;
        }

        if (errors.Count > 0)
        {
            return PromotionError.Validation(errors);
        }

        if (p.Code is not null && await db.Promotions.AnyAsync(x => x.Code == p.Code, ct))
        {
            return PromotionErrors.CodeTaken;
        }

        IBillingProvider? provider = null;
        if (kind != PromotionKind.CreditCode)
        {
            provider = Provider;
            if (provider is null)
            {
                return PromotionErrors.NotConfigured;
            }

            PromotionError? targetError = await ResolveTargetsAsync(dto, p, provider, ct);
            if (targetError is not null)
            {
                return targetError;
            }

            if (p.Code is null && await OverlapsAsync(p, now, ct))
            {
                return PromotionErrors.Overlap;
            }
        }

        if (provider is not null && kind is PromotionKind.PlanDiscount or PromotionKind.PackDiscount)
        {
            try
            {
                p.ProviderDiscountId = await provider.CreateDiscountAsync(SpecOf(p), ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Billing provider {Provider} refused to create the discount for promotion {Promotion}.", provider.Name, p.Id);
                return PromotionErrors.ProviderError;
            }
        }

        db.Promotions.Add(p);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique code. The provider discount (if any) is orphaned: switch it off.
            db.ChangeTracker.Clear();
            if (provider is not null && p.ProviderDiscountId is { } orphan)
            {
                await TryDeactivateAsync(provider, orphan);
            }

            if (p.Code is not null && await db.Promotions.AnyAsync(x => x.Code == p.Code, ct))
            {
                return PromotionErrors.CodeTaken;
            }

            throw;
        }

        return PromotionOutcome<Promotion>.Success(p);
    }

    private static void ValidateCreditCode(CreatePromotionDto dto, Promotion p, List<string> errors)
    {
        if (p.Code is null)
        {
            errors.Add("code is required for a credit code.");
        }

        if (dto.Credits is not (>= 1 and <= MaxCodeCredits))
        {
            errors.Add($"credits must be 1 to {MaxCodeCredits}.");
        }

        int days = dto.CreditExpiryDays ?? DefaultCreditExpiryDays;
        if (days is < 1 or > MaxCreditExpiryDays)
        {
            errors.Add($"creditExpiryDays must be 1 to {MaxCreditExpiryDays}.");
        }

        p.CreditsMillis = (dto.Credits ?? 0) * 1000L;
        p.CreditExpiryDays = days;
    }

    private static void ValidateDiscount(CreatePromotionDto dto, Promotion p, List<string> errors)
    {
        if (!PromotionRules.TryParseEnum(dto.DiscountType, out PromotionDiscountType type))
        {
            errors.Add("discountType must be percentage or flat.");
        }
        else
        {
            p.DiscountType = type;
            string amount = dto.DiscountAmount?.Trim() ?? string.Empty;
            if (type == PromotionDiscountType.Percentage)
            {
                bool ok = decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal pct)
                    && pct >= 0.01m && pct <= 100m && decimal.Round(pct, 2) == pct;
                if (!ok)
                {
                    errors.Add("discountAmount must be a percentage from 0.01 to 100.");
                }
            }
            else
            {
                if (!(amount.Length > 0 && amount.All(char.IsAsciiDigit) && long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out long minor) && minor > 0))
                {
                    errors.Add("discountAmount must be whole minor units (cents) for a flat discount.");
                }

                p.CurrencyCode = dto.CurrencyCode?.Trim().ToUpperInvariant();
                if (p.CurrencyCode != "USD")
                {
                    errors.Add("currencyCode must be USD for a flat discount.");
                }
            }

            p.DiscountAmount = amount;
        }

        if (p.Kind == PromotionKind.PackDiscount)
        {
            p.DurationKind = PromotionDuration.Once;
            return;
        }

        if (!PromotionRules.TryParseEnum(dto.Duration, out PromotionDuration duration))
        {
            errors.Add("duration must be once, months or forever.");
            return;
        }

        p.DurationKind = duration;
        if (duration == PromotionDuration.Months)
        {
            if (dto.DurationMonths is not (>= 1 and <= 999))
            {
                errors.Add("durationMonths must be 1 to 999.");
            }

            p.DurationMonths = dto.DurationMonths;
        }
        else if (dto.DurationMonths is not null)
        {
            errors.Add("durationMonths is only allowed with months.");
        }

        if (duration == PromotionDuration.Months && dto.IncludeAnnual == true && p.Kind == PromotionKind.PlanDiscount)
        {
            errors.Add("includeAnnual cannot be combined with a months duration: a months offer is monthly only.");
        }
    }

    /// <summary>Resolves the targets to explicit provider price ids. An empty list means every sellable plan or pack.</summary>
    private async Task<PromotionError?> ResolveTargetsAsync(CreatePromotionDto dto, Promotion p, IBillingProvider provider, CancellationToken ct)
    {
        List<string> priceIds = [];
        if (p.Kind == PromotionKind.PlanDiscount)
        {
            // Paddle counts a months duration in billing periods, so it means months only on a monthly
            // price: a months discount is restricted to the plans' monthly prices.
            bool monthlyOnly = p.DurationKind == PromotionDuration.Months;
            // A plan discount targets the monthly price ids only; the annual ones only when the promotion asks for them
            // explicitly (IncludeAnnual) and its duration is not counted in months.
            bool withAnnual = dto.IncludeAnnual == true && !monthlyOnly;
            IReadOnlyList<string> PricesOf(Plan plan) => withAnnual
                ? BillingEventProcessor.PriceIdsOf(plan, provider.Name)
                : BillingEventProcessor.MonthlyPriceIdOf(plan, provider.Name) is { } monthly ? [monthly] : [];

            List<Plan> sellable = await db.Plans.AsNoTracking()
                .Where(x => x.IsActive && (x.Kind == PlanKind.Subscription || x.Kind == PlanKind.PerSeat))
                .OrderBy(x => x.SortOrder)
                .ToListAsync(ct);
            List<string> keys = (dto.PlanKeys ?? []).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (keys.Count == 0)
            {
                priceIds.AddRange(sellable.SelectMany(PricesOf));
                if (priceIds.Count == 0)
                {
                    return PromotionErrors.TargetNotPurchasable("No plan");
                }
            }

            foreach (string key in keys)
            {
                Plan? plan = sellable.FirstOrDefault(x => x.Key == key);
                IReadOnlyList<string> ids = plan is null ? [] : PricesOf(plan);
                if (ids.Count == 0)
                {
                    return PromotionErrors.TargetNotPurchasable($"Plan '{key}'");
                }

                priceIds.AddRange(ids);
            }

            p.TargetPlanKeysJson = JsonSerializer.Serialize(keys);
        }
        else
        {
            List<int> packs = (dto.PackCredits ?? []).Distinct().ToList();
            foreach (int pack in packs.Count == 0 ? provider.TopUpPacks : packs)
            {
                if (provider.TopUpPriceId(pack) is not { } id)
                {
                    return PromotionErrors.TargetNotPurchasable($"The {pack}-credit pack");
                }

                priceIds.Add(id);
            }

            if (priceIds.Count == 0)
            {
                return PromotionErrors.TargetNotPurchasable("No pack");
            }

            p.TargetPackCreditsJson = JsonSerializer.Serialize(packs);
        }

        p.TargetPriceIdsJson = JsonSerializer.Serialize(priceIds.Distinct(StringComparer.Ordinal).ToList());
        return null;
    }

    private static ProviderDiscountSpec SpecOf(Promotion p)
    {
        (bool recur, int? intervals) = p.Kind == PromotionKind.PackDiscount
            ? (false, null)
            : p.DurationKind switch
            {
                PromotionDuration.Months => (true, p.DurationMonths),
                PromotionDuration.Forever => (true, (int?)null),
                _ => (false, null)
            };
        return new ProviderDiscountSpec(
            p.Id,
            p.PublicHeadline ?? p.Name,
            p.DiscountType!.Value,
            p.DiscountAmount!,
            p.DiscountType == PromotionDiscountType.Flat ? p.CurrencyCode : null,
            recur,
            intervals,
            p.MaxRedemptions,
            PromotionRules.ReadStrings(p.TargetPriceIdsJson),
            p.EndsAt);
    }

    /// <summary>
    /// A second Active automatic promotion of the same kind whose window overlaps and whose targets
    /// intersect would make "the" automatic discount ambiguous. Promotions already ended never overlap.
    /// </summary>
    private async Task<bool> OverlapsAsync(Promotion candidate, DateTime now, CancellationToken ct)
    {
        if (candidate.Code is not null || candidate.Kind == PromotionKind.CreditCode)
        {
            return false;
        }

        List<Promotion> others = await db.Promotions.AsNoTracking()
            .Where(x => x.Id != candidate.Id && x.Kind == candidate.Kind && x.Code == null && x.Status == PromotionStatus.Active)
            .ToListAsync(ct);
        HashSet<string> mine = PromotionRules.ReadStrings(candidate.TargetPriceIdsJson).ToHashSet(StringComparer.Ordinal);
        return others.Any(o =>
            !(o.EndsAt is { } ended && ended <= now)
            && (o.StartsAt ?? DateTime.MinValue) < (candidate.EndsAt ?? DateTime.MaxValue)
            && (candidate.StartsAt ?? DateTime.MinValue) < (o.EndsAt ?? DateTime.MaxValue)
            && PromotionRules.ReadStrings(o.TargetPriceIdsJson).Any(mine.Contains));
    }

    private async Task TryDeactivateAsync(IBillingProvider provider, string discountId)
    {
        try
        {
            await provider.UpdateDiscountAsync(discountId, false, null, false, CancellationToken.None);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not deactivate orphaned provider discount {Discount}.", discountId);
        }
    }

    public async Task<PromotionOutcome<Promotion>> UpdateAsync(Guid id, PromotionPatch patch, CancellationToken ct)
    {
        Promotion? p = await db.Promotions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return PromotionErrors.NotFound;
        }

        DateTime now = Now;
        DateTime? endsAt = patch.ClearEndsAt ? null : patch.EndsAt ?? p.EndsAt;
        if (patch.EndsAt is { } newEnd && p.StartsAt is { } start && newEnd <= start)
        {
            return PromotionError.Validation(["endsAt must be after startsAt."]);
        }

        // Reactivation, or an Active promotion's window growing (a later endsAt, or none), re-runs the
        // create-time overlap rule against the promotion as it would be saved.
        bool reactivating = patch.Status == PromotionStatus.Active && p.Status != PromotionStatus.Active;
        bool windowGrows = p.EndsAt is { } oldEnd && (endsAt is null || endsAt > oldEnd);
        if ((patch.Status ?? p.Status) == PromotionStatus.Active && (reactivating || windowGrows))
        {
            Promotion projected = new()
            {
                Id = p.Id,
                Kind = p.Kind,
                Code = p.Code,
                StartsAt = p.StartsAt,
                EndsAt = endsAt,
                TargetPriceIdsJson = p.TargetPriceIdsJson
            };
            if (await OverlapsAsync(projected, now, ct))
            {
                return PromotionErrors.Overlap;
            }
        }

        // The provider first: the local row changes only once the provider has accepted the change.
        bool providerChange = patch.Status is not null || patch.EndsAt is not null || patch.ClearEndsAt;
        if (p.ProviderDiscountId is { } discountId && providerChange)
        {
            if (Provider is not { } provider)
            {
                return PromotionErrors.NotConfigured;
            }

            try
            {
                await provider.UpdateDiscountAsync(
                    discountId,
                    patch.Status is { } status ? status == PromotionStatus.Active : null,
                    patch.ClearEndsAt ? null : patch.EndsAt,
                    patch.ClearEndsAt,
                    ct);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Billing provider {Provider} refused to update discount {Discount}.", provider.Name, discountId);
                return PromotionErrors.ProviderError;
            }
        }

        if (patch.Name is not null)
        {
            p.Name = patch.Name;
        }

        if (patch.HasHeadline)
        {
            p.PublicHeadline = patch.PublicHeadline;
        }

        if (patch.Status is { } newStatus)
        {
            p.Status = newStatus;
        }

        p.EndsAt = endsAt;
        p.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return PromotionOutcome<Promotion>.Success(p);
    }

    // ------------------------------------------------------------------ admin credit grants

    public async Task<PromotionOutcome<AdminGrantOutcome>> GrantCreditsAsync(Guid orgId, GrantCreditsDto dto, Guid adminUserId, CancellationToken ct)
    {
        if (orgId == WellKnownOrganizations.PlatformId
            || !await db.Organizations.AnyAsync(o => o.Id == orgId && o.Kind != OrganizationKind.Platform, ct))
        {
            return PromotionErrors.NotFound;
        }

        DateTime now = Now;
        List<string> errors = [];
        if (dto.RequestId == Guid.Empty)
        {
            errors.Add("requestId is required.");
        }

        if (dto.Credits is not (>= 1 and <= MaxGrantCredits))
        {
            errors.Add($"credits must be 1 to {MaxGrantCredits}.");
        }

        DateTime? expiresAt = dto.ExpiresAt is { } e ? PromotionRules.ToUtc(e) : null;
        if (expiresAt is null || expiresAt < now.AddDays(1) || expiresAt > now.AddYears(3))
        {
            errors.Add("expiresAt must be between 1 day and 3 years from now.");
        }

        string reason = dto.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > MaxReasonLength)
        {
            errors.Add($"reason must be 1 to {MaxReasonLength} characters.");
        }

        if (errors.Count > 0)
        {
            return PromotionError.Validation(errors);
        }

        GrantRequest request = new(orgId, dto.Credits!.Value * 1000L, expiresAt!.Value, reason);
        if (await ExistingGrantAsync(request, dto.RequestId, ct) is { } replay)
        {
            return replay;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            CreditLedgerResult grant = await new CreditLedger(db, _time).GrantAsync(
                orgId,
                CreditGrantSource.Promo,
                dto.Credits!.Value * 1000L,
                expiresAt,
                $"admin-grant:{dto.RequestId}",
                context: new CreditLedgerContext(Note: $"admin grant: {reason}"),
                ct: ct);
            if (grant.Replayed)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return await ExistingGrantAsync(request, dto.RequestId, ct)
                    ?? throw new InvalidOperationException($"Admin grant {dto.RequestId} is in the ledger without its row.");
            }

            if (!grant.Ok || grant.Entry is null)
            {
                throw new InvalidOperationException($"Admin grant refused: {grant.Error}");
            }

            AdminCreditGrant row = new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                CreditsMillis = dto.Credits.Value * 1000L,
                ExpiresAt = expiresAt!.Value,
                Reason = reason,
                GrantedByUserId = adminUserId,
                LedgerEntryId = grant.Entry.Id,
                RequestId = dto.RequestId,
                CreatedAt = now
            };
            db.AdminCreditGrants.Add(row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PromotionOutcome<AdminGrantOutcome>.Success(new AdminGrantOutcome(row, false));
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same requestId won the unique index: answer with its row.
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return await ExistingGrantAsync(request, dto.RequestId, ct) ?? throw new InvalidOperationException("Admin grant failed to save.");
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>What a grant request asks for, compared field by field against a replayed requestId.</summary>
    private sealed record GrantRequest(Guid OrgId, long CreditsMillis, DateTime ExpiresAt, string Reason);

    /// <summary>
    /// The stored grant for a requestId: replayed when the request asks for exactly the same grant
    /// (org, credits, expiry to the millisecond, trimmed reason), otherwise a 409, never the original
    /// grant silently standing in for a different one.
    /// </summary>
    private async Task<PromotionOutcome<AdminGrantOutcome>?> ExistingGrantAsync(GrantRequest request, Guid requestId, CancellationToken ct)
    {
        AdminCreditGrant? existing = await db.AdminCreditGrants.AsNoTracking().FirstOrDefaultAsync(g => g.RequestId == requestId, ct);
        if (existing is null)
        {
            return null;
        }

        bool same = existing.OrganizationId == request.OrgId
            && existing.CreditsMillis == request.CreditsMillis
            && (PromotionRules.Utc(existing.ExpiresAt) - request.ExpiresAt).Duration() < TimeSpan.FromMilliseconds(1)
            && string.Equals(existing.Reason, request.Reason, StringComparison.Ordinal);
        return same
            ? PromotionOutcome<AdminGrantOutcome>.Success(new AdminGrantOutcome(existing, true))
            : PromotionErrors.RequestIdConflict;
    }
}
