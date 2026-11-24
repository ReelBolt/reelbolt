using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// Bound from <c>Billing:AnnualGrants</c> (<c>Billing__AnnualGrants__*</c>). Runs only while <c>Billing:Enabled</c> is also true.
/// </summary>
public sealed class AnnualGrantOptions
{
    public const string SectionName = "Billing:AnnualGrants";

    /// <summary>Runs the monthly grant sweep for annual subscriptions.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes from the start of one pass to the start of the next. Values below 1 count as 1.</summary>
    public int SweepIntervalMinutes { get; set; } = 60;

    /// <summary>Seconds after startup before the first pass.</summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Most annual subscriptions one pass looks at. The pass resumes after the last one it handled (a cursor kept
    /// between passes), so the rest are reached by the following passes and no subscription is ever starved.
    /// </summary>
    public int MaxPerSweep { get; set; } = 500;

    /// <summary>Seconds after which one pass stops taking new subscriptions (the cursor resumes there). Values below 1 count as 1.</summary>
    public int MaxPassSeconds { get; set; } = 300;
}

/// <summary>
/// An annual term is paid once but its credits arrive monthly. The renewal webhook grants month 0 at the term
/// start; this sweep grants months 1..11, each at the monthly anniversary of the term start, at the CURRENT plan
/// and seats, expiring at the next anniversary (the last month at the term end), keyed
/// <c>plan:{provider}:{subscription}:{periodStart}:m{n}</c>. It grants only the month whose window
/// is open now: a month whose window already ended (a replica that was down) is lost, as its credits would be born
/// expired, and one still to come is never granted early.
/// <para>
/// Skipped: a subscription not Active or Trialing, one whose cancellation is already effective, one whose term
/// payment was fully refunded, and a term the renewal webhook has not opened yet (the row's period is the
/// source of truth; a lapsed period grants nothing). Many replicas: every replica runs it with no
/// coordination of its own; each subscription is handled under the same advisory lock the webhooks take and the
/// ledger keys make a second pass a replay.
/// </para>
/// </summary>
public sealed class AnnualCreditGrantSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> billingOptions,
    IOptions<AnnualGrantOptions> options,
    ILogger<AnnualCreditGrantSweeper> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // Where the next pass resumes (the last subscription id handled when a pass stopped on its cap); null = from the start.
    private Guid? _cursor;

    private bool Disabled => !billingOptions.Value.Enabled || !options.Value.Enabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Disabled)
        {
            logger.LogDebug("The annual credit grant sweep is disabled (Billing:Enabled / Billing:AnnualGrants:Enabled)");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.StartupDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepOnceAsync(_clock.GetUtcNow().UtcDateTime, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The annual credit grant sweep failed; retrying after the interval");
                }

                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.SweepIntervalMinutes)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass at <paramref name="nowUtc"/>. Returns the monthly grants it made. Public for tests.</summary>
    public async Task<int> SweepOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (Disabled)
        {
            return 0;
        }

        List<Guid> candidates;
        int dueCount;
        using (IServiceScope scope = scopeFactory.CreateScope())
        using (IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            var rows = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Provider != null && s.ProviderSubscriptionId != null
                    && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing)
                    && s.PeriodStart != null && s.PeriodEnd != null && s.PeriodStart <= nowUtc && s.PeriodEnd > nowUtc)
                .OrderBy(s => s.Id)
                .Select(s => new { s.Id, s.PeriodStart, s.PeriodEnd })
                .ToListAsync(ct).ConfigureAwait(false);
            List<Guid> due = rows
                .Where(r => SubscriptionInterval.Of(r.PeriodStart, r.PeriodEnd) == BillingInterval.Annual)
                .Select(r => r.Id)
                .ToList();
            dueCount = due.Count;
            // Resume after the cursor and wrap round, so a pass that hit its cap never restarts at the same first ones.
            int from = _cursor is { } last ? due.FindIndex(id => id.CompareTo(last) > 0) : 0;
            if (from < 0)
            {
                from = 0;
            }

            candidates = due.Skip(from).Concat(due.Take(from)).Take(Math.Max(1, options.Value.MaxPerSweep)).ToList();
        }

        long started = _clock.GetTimestamp();
        TimeSpan budget = TimeSpan.FromSeconds(Math.Max(1, options.Value.MaxPassSeconds));
        int granted = 0;
        bool timedOut = false;
        foreach (Guid id in candidates)
        {
            if (_clock.GetElapsedTime(started) > budget)
            {
                timedOut = true;
                break;
            }

            _cursor = id;
            try
            {
                granted += await GrantDueMonthsAsync(id, nowUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "The annual credit grant sweep could not grant subscription {Subscription}; continuing with the next one", id);
            }
        }

        if (!timedOut && candidates.Count >= dueCount)
        {
            // The due set was walked to its end: the next pass starts over.
            _cursor = null;
        }

        if (granted > 0)
        {
            logger.LogInformation("The annual credit grant sweep granted {Count} monthly refill(s) across {Subs} annual subscription(s)", granted, candidates.Count);
        }

        return granted;
    }

    private async Task<int> GrantDueMonthsAsync(Guid subscriptionRowId, DateTime now, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        TimeProvider time = scope.ServiceProvider.GetService<TimeProvider>() ?? _clock;

        Subscription? first = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionRowId, ct).ConfigureAwait(false);
        if (first?.Provider is not { } provider || first.ProviderSubscriptionId is not { } providerSubscriptionId)
        {
            return 0;
        }

        // The webhooks of one subscription take this lock; holding it here orders a grant against a refund's clawback.
        await using var tx = db.Database.IsNpgsql() ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        if (tx is not null)
        {
            long key = BillingEventProcessor.SubscriptionLockKey(provider, providerSubscriptionId);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct).ConfigureAwait(false);
        }

        Subscription? sub = await db.Subscriptions.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionRowId, ct).ConfigureAwait(false);
        if (sub is not { PeriodStart: { } start, PeriodEnd: { } end }
            || sub.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing)
            || SubscriptionInterval.Of(start, end) != BillingInterval.Annual
            || start > now || end <= now)
        {
            return 0;
        }

        if (sub.CancelAtPeriodEnd && (sub.CancelEffectiveAt ?? end) <= now)
        {
            return 0;
        }

        AnnualTermPayments term = await AnnualTermPayments.LoadAsync(db, provider, providerSubscriptionId, start, now, sub.TermOpeningTransactionId, ct).ConfigureAwait(false);
        if (term.Refunded || ((term.Missing || term.Ambiguous) && sub.Status == SubscriptionStatus.Active))
        {
            // Fail closed: a refunded or charged-back term grants nothing, neither does an Active term with no recorded
            // payment, nor one whose payments are ambiguous (several, no known opening payment). Flagged for review.
            logger.LogWarning(
                "Annual subscription {Subscription} ({Provider}) term {Start:O}: {Reason}; monthly grants skipped. Review manually.",
                providerSubscriptionId, provider, start, term.Refunded ? "a term payment was refunded in full or charged back" : term.Ambiguous ? "several payments of the term and no known opening payment" : "no payment is recorded for the term");
            return 0;
        }

        Plan? plan = await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct).ConfigureAwait(false);
        long amount = plan is null ? 0 : BillingEventProcessor.EntitlementMillis(plan, sub.Seats);
        // An upgrade whose payment is not paired and granted yet, or was refunded, has not been paid for: grant
        // at the entitlement before it, as the renewal path does.
        // Several such upgrades (or seat payments): the lowest pre-upgrade entitlement among them.
        List<long?> unpaidBases = await db.BillingPendingUpgradeGrants.AsNoTracking()
            .Where(r => r.Provider == provider && r.ProviderSubscriptionId == providerSubscriptionId && r.PeriodStart == start
                && r.NewEntitlementMillis != null && (r.GrantedAt == null || r.ClawedBackAt != null))
            .Select(r => r.OldEntitlementMillis).ToListAsync(ct).ConfigureAwait(false);
        if (unpaidBases.Count > 0)
        {
            amount = Math.Min(amount, unpaidBases.Min() ?? 0);
        }

        if (plan is null || amount <= 0)
        {
            return 0;
        }

        int granted = 0;
        // Only the month whose window is open now: an earlier month's credits would expire before they were granted.
        int through = AnnualCreditSchedule.MonthIndex(start, end, now);
        for (int n = Math.Max(1, through); n <= through; n++)
        {
            string key = AnnualCreditSchedule.MonthKey(provider, providerSubscriptionId, start, n);
            if (await db.CreditLedgerEntries.AsNoTracking().AnyAsync(e => e.IdempotencyKey == key, ct).ConfigureAwait(false))
            {
                continue;
            }

            DateTime expiresAt = n >= AnnualCreditSchedule.MonthsPerTerm - 1 ? end : AnnualCreditSchedule.Anniversary(start, end, n + 1);
            CreditLedgerResult grant = await new CreditLedger(db, time).GrantAsync(
                sub.OrganizationId,
                CreditGrantSource.Plan,
                amount,
                expiresAt,
                key,
                context: new CreditLedgerContext(Note: $"{plan.Key} refill, month {n + 1} of 12"),
                ct: ct).ConfigureAwait(false);
            if (!grant.Ok)
            {
                throw new InvalidOperationException($"Annual refill refused: {grant.Error}");
            }

            if (!grant.Replayed)
            {
                granted++;
            }
        }

        if (tx is not null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return granted;
    }
}
