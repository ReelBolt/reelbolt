using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>Who asked for a reconcile: a change request that found drift, the periodic sweep, or a platform admin.</summary>
public enum ReconcileTrigger { Lazy, Sweep, Admin }

/// <summary>How a reconcile ended.</summary>
public enum ReconcileOutcome
{
    /// <summary>The provider's state was fed through the webhook processor and something was applied (a webhook was lost).</summary>
    Healed,

    /// <summary>The row already matches the provider; nothing was applied.</summary>
    InSync,

    /// <summary>The provider changed too recently for a missing webhook to count as lost; nothing was applied.</summary>
    TooRecent,

    /// <summary>No provider subscription to compare (no row and none live at the provider), or reconcile is switched off.</summary>
    NothingToReconcile,

    /// <summary>A provider read failed (network, 5xx, any 4xx including 404); nothing was applied.</summary>
    ProviderUnavailable,

    /// <summary>The provider's state differs but the webhook processor did not apply it (stale, duplicate or unresolved).</summary>
    Stale,

    /// <summary>
    /// Another request holds the organization's change claim, or every change lock connection of this replica
    /// is in use (S-4/S-23); nothing was read or applied.
    /// </summary>
    Busy
}

/// <summary>One reconcile's result.</summary>
/// <param name="Drift">The fields that differed (<see cref="SubscriptionDrift"/> names), empty when in sync or unknown.</param>
/// <param name="SubscriptionEventsApplied">0 or 1: the synthetic subscription event ended <c>Applied</c>.</param>
/// <param name="TransactionsApplied">Synthetic upgrade-payment events that ended <c>Applied</c>.</param>
/// <param name="Error">A short, secret-free reason when <see cref="ReconcileOutcome.ProviderUnavailable"/> or <see cref="ReconcileOutcome.Stale"/>.</param>
/// <param name="PaymentsUnapplied">Completed upgrade payments the provider holds that the processor did not apply (B12's drift report names them).</param>
public sealed record ReconcileResult(ReconcileOutcome Outcome, IReadOnlyList<string> Drift, int SubscriptionEventsApplied, int TransactionsApplied, string? Error, int PaymentsUnapplied = 0);

/// <summary>
/// Heals a lost provider webhook by feeding the provider's live state through the webhook's own
/// <see cref="BillingEventProcessor.ProcessAsync"/> (see <see cref="BillingReconciler"/>).
/// </summary>
public interface IBillingReconciler
{
    /// <summary>Reconciles one organization. The caller already holds the org's change claim (the controller's lazy path).</summary>
    Task<ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct);

    /// <summary>Takes the org's change claim itself, then reconciles; <see cref="ReconcileOutcome.Busy"/> when another request holds it or no lock connection is free.</summary>
    Task<ReconcileResult> ReconcileAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct);

    /// <summary>
    /// Upserts the org's watch after the provider accepted a change: kind and member are the latest request's;
    /// <c>ChangeRequestedAt = now</c> only when nothing is due yet — an older change still due keeps its time,
    /// so a repeated request never pushes the stuck threshold out (QA-3).
    /// </summary>
    Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct);
}

/// <summary>
/// A webhook substitute, never a second write path. It reads the provider's live subscription (and its recent
/// completed upgrade payments) as the events their webhooks would have carried, and feeds them — under the
/// system tenant scope — to the same <see cref="BillingEventProcessor.ProcessAsync"/>, so idempotency,
/// out-of-order protection and upgrade-credit pairing are the webhook's own. Its only own write is the
/// org's <see cref="BillingReconcileWatch"/>.
/// <list type="number">
/// <item>The org's current provider subscription row; with none, the customer's live subscription id from the
/// provider listing; with neither, <see cref="ReconcileOutcome.NothingToReconcile"/> (error <see cref="NoCustomerError"/> when the
/// org has no billing customer to ask about, so the backlog can tell it from an abandoned checkout).</item>
/// <item>The snapshot and its <see cref="SubscriptionDrift"/> (a held downgrade the provider already bills counts as in sync;
/// a hold the provider no longer bills — a lost "keep" — is <c>scheduled_change</c> drift, released by the processor).
/// A snapshot without the provider's <c>updated_at</c> is <see cref="ReconcileOutcome.ProviderUnavailable"/> (fails closed) unless the provider
/// declares <see cref="IBillingProvider.ReconcilesWithoutUpdatedAt"/> (Stripe: its reconcile ids are content hashes and the event is stamped with the fetch time).</item>
/// <item>Drift younger than <see cref="BillingReconcileOptions.StuckAfterSeconds"/> (anchored on the provider's
/// <c>updated_at</c>, or on the watch's change request when the provider has none) is left to its webhook unless forced.</item>
/// <item>The completed upgrade payments of that subscription since the period start − 1 day. Every provider read
/// happens before any write; any read failure (network, error status, malformed body) is
/// <see cref="ReconcileOutcome.ProviderUnavailable"/> and applies nothing.</item>
/// <item>The subscription event (only when drifting), then every payment not already on an upgrade row.</item>
/// <item>The watch: last check, outcome, error, failure streak; the change the run started from is cleared when
/// healed, in sync or nothing to do (one recorded during the run stays due).</item>
/// </list>
/// </summary>
public sealed class BillingReconciler(
    InferenceApiDbContext db,
    IEnumerable<IBillingProvider> providers,
    BillingEventProcessor processor,
    IBillingChangeLock changeLock,
    IOptions<BillingOptions> billingOptions,
    IOptions<BillingReconcileOptions> options,
    ILogger<BillingReconciler> logger,
    ITenantContext? tenant = null,
    TimeProvider? time = null,
    IOptions<BillingProviderOptions>? providerOptions = null) : IBillingReconciler
{
    /// <summary>The kind of a watch the reconciler opened itself (no change request recorded).</summary>
    public const string OverdueKind = "overdue";

    /// <summary>
    /// The prefix of every synthetic event id the reconciler feeds (<c>reconcile:sub:…</c>, <c>reconcile:txn:…</c>);
    /// reserved, so a real webhook carrying one is refused.
    /// </summary>
    public const string SyntheticEventIdPrefix = "reconcile:";

    /// <summary>The prefix of the event ids minted when a buyer's return confirms a checkout (<c>confirm:…</c>); not a real webhook.</summary>
    public const string ConfirmEventIdPrefix = "confirm:";

    /// <summary>The kind recorded when a checkout session is created.</summary>
    public const string CheckoutKind = "checkout";

    private const int MaxKindLength = 32;
    private const int MaxErrorLength = 500;

    /// <summary>The watch's error when the provider's subscription carries no <c>updated_at</c>.</summary>
    public const string NoUpdatedAtError = "provider returned no updated_at";

    /// <summary>The watch's error when the organization has no billing customer to ask the provider about.</summary>
    public const string NoCustomerError = "no_customer";

    /// <summary>The watch's error when a provider body could not be parsed.</summary>
    public const string MalformedError = "provider returned a malformed response";

    private readonly IReadOnlyList<IBillingProvider> _providers = providers.ToList();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private bool Disabled => !billingOptions.Value.Enabled || !options.Value.Enabled;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    private static ReconcileResult Result(ReconcileOutcome outcome, IReadOnlyList<string>? drift = null, int subs = 0, int txns = 0, string? error = null, int unapplied = 0)
        => new(outcome, drift ?? [], subs, txns, error, unapplied);

    public async Task<ReconcileResult> ReconcileAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct)
    {
        if (Disabled) return Result(ReconcileOutcome.NothingToReconcile);

        IAsyncDisposable? claim;
        try
        {
            claim = await changeLock.TryAcquireAsync(organizationId, ct).ConfigureAwait(false);
        }
        catch (BillingChangeLockCapacityException)
        {
            // S-4/S-23: every lock connection of this replica is in use; the next sweep (or a retry) picks it up.
            BillingWebhookDiagnostics.RecordRun(trigger, ReconcileOutcome.Busy);
            logger.LogInformation("Reconcile ({Trigger}) of organization {Org} skipped: every change lock connection of this replica is in use.", trigger, organizationId);
            return Result(ReconcileOutcome.Busy);
        }

        await using (claim)
        {
            if (claim is null)
            {
                BillingWebhookDiagnostics.RecordRun(trigger, ReconcileOutcome.Busy);
                logger.LogDebug("Reconcile ({Trigger}) of organization {Org} skipped: another request holds its change claim.", trigger, organizationId);
                return Result(ReconcileOutcome.Busy);
            }

            return await ReconcileHeldAsync(organizationId, trigger, force, ct).ConfigureAwait(false);
        }
    }

    public async Task<ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct)
    {
        if (Disabled) return Result(ReconcileOutcome.NothingToReconcile);

        DateTime now = UtcNow;
        BillingReconcileWatch? watch = await db.BillingReconcileWatches.AsNoTracking()
            .FirstOrDefaultAsync(w => w.OrganizationId == organizationId, ct).ConfigureAwait(false);

        ReconcileResult result;
        try
        {
            result = await RunAsync(organizationId, trigger, force, now, watch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Not a provider read (those are ProviderUnavailable): applying failed and rolled back. Rethrown,
            // so the caller answers as it would for a webhook failure; the watch stays as it was.
            logger.LogError(BillingWebhookDiagnostics.ReconcileFailed, ex,
                "Reconcile ({Trigger}) of organization {Org} failed while applying the provider's state.", trigger, organizationId);
            throw;
        }

        await UpdateWatchAsync(organizationId, watch, result, now, ct).ConfigureAwait(false);
        Report(organizationId, trigger, result);
        return result;
    }

    private async Task<ReconcileResult> RunAsync(Guid organizationId, ReconcileTrigger trigger, bool force, DateTime now, BillingReconcileWatch? watch, CancellationToken ct)
    {
        using IDisposable scope = tenant.System();

        // 1. The org's current provider subscription row (the change endpoints' own selection).
        Subscription? sub = await db.Subscriptions.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.Status != SubscriptionStatus.Canceled)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (sub is not { ProviderSubscriptionId: not null })
        {
            sub = null;
        }

        IBillingProvider? provider = sub is null
            ? BillingProviderSelector.Active(_providers, providerOptions?.Value)
            : _providers.FirstOrDefault(p => p.IsConfigured && string.Equals(p.Name, sub.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return Result(ReconcileOutcome.NothingToReconcile);
        }

        SubscriptionDrift drift = SubscriptionDrift.None;
        BillingSnapshot snapshot;
        IReadOnlyList<BillingSnapshot> payments;
        try
        {
            string? subscriptionId = sub?.ProviderSubscriptionId;
            if (subscriptionId is null)
            {
                string? customerId = await db.BillingCustomers.AsNoTracking()
                    .Where(c => c.OrganizationId == organizationId && c.Provider == provider.Name)
                    .Select(c => c.ProviderCustomerId)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (customerId is null) return Result(ReconcileOutcome.NothingToReconcile, error: NoCustomerError);

                subscriptionId = await ProviderRead(() => provider.FindLiveSubscriptionIdAsync(customerId, organizationId, ct)).ConfigureAwait(false);
                if (subscriptionId is null) return Result(ReconcileOutcome.NothingToReconcile);
            }

            // 2. The live subscription as its webhook would carry it, and what differs.
            try
            {
                snapshot = await ProviderRead(() => provider.SnapshotSubscriptionAsync(subscriptionId, ct)).ConfigureAwait(false);
            }
            catch (BillingProviderException ex) when (ex.Status == 404 && sub is not null)
            {
                // Paddle has no subscription for the id the ledger holds: the reverse drift (B12), named
                // rather than folded into an anonymous provider failure. Nothing is applied.
                return ProviderUnavailable(organizationId, provider, SubscriptionDrift.LedgerOnly, ShortError(ex), ex);
            }

            if (snapshot.ProviderUpdatedAt is null && !provider.ReconcilesWithoutUpdatedAt)
            {
                // Without it the synthetic event would be stamped "now" (outranking any real webhook) and the
                // change's age is unknown: fail closed rather than guess.
                return ProviderUnavailable(organizationId, provider, drift, NoUpdatedAtError, null);
            }

            if (sub is null)
            {
                drift = SubscriptionDrift.Missing;
            }
            else
            {
                List<Plan> plans = await db.Plans.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                drift = SubscriptionDrift.Compute(sub, snapshot.Event, plans);
            }

            // 3. A change younger than the threshold may still have its webhook on the way.
            if (!drift.InSync)
            {
                // A provider with no updated_at (Stripe): the age comes from the watch's own change request; with
                // none recorded the drift is found by the sweep/lazy path because something was already due, so
                // it is not "too recent". The synthetic event is stamped with the fetch time either way.
                DateTime? anchorOrNull = snapshot.ProviderUpdatedAt ?? watch?.ChangeRequestedAt;
                DateTime anchor = anchorOrNull ?? now;
                TimeSpan stuckAfter = options.Value.StuckAfter;
                bool tooRecent = anchorOrNull is not null && now - anchor < stuckAfter;
                if (tooRecent && !force)
                {
                    return Result(ReconcileOutcome.TooRecent, drift.Fields);
                }

                if (!tooRecent)
                {
                    logger.LogWarning(BillingWebhookDiagnostics.ChangeStuck,
                        "Organization {Org}'s subscription {Sub} differs from the provider ({Drift}) since {Since:O}, longer than {StuckAfter}: its webhook is overdue ({Trigger}).",
                        organizationId, subscriptionId, string.Join(",", drift.Fields), anchor, stuckAfter, trigger);
                }
            }

            // 4. Recent upgrade payments, read before anything is written.
            DateTime paymentsSince = (snapshot.Event.PeriodStart ?? now).AddDays(-1);
            payments = await ProviderRead(() => provider.CompletedUpgradeTransactionsAsync(subscriptionId, paymentsSince, ct)).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return ProviderUnavailable(organizationId, provider, drift, ShortError(ex), ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return ProviderUnavailable(organizationId, provider, drift, "provider timed out", ex);
        }
        catch (MalformedProviderResponseException ex)
        {
            return ProviderUnavailable(organizationId, provider, drift, MalformedError, ex.InnerException);
        }

        // 5. Through the webhook's own processor, as the system (a webhook has no tenant either).
        List<BillingSnapshot> unseen = await UnseenPaymentsAsync(provider.Name, payments, ct).ConfigureAwait(false);
        BillingEventOutcome? subscriptionOutcome = null;
        int subscriptionsApplied = 0;
        int paymentsApplied = 0;
        if (!drift.InSync)
        {
            subscriptionOutcome = await processor.ProcessAsync(snapshot.Event, snapshot.RawJson, ct).ConfigureAwait(false);
            if (subscriptionOutcome == BillingEventOutcome.Applied) subscriptionsApplied = 1;
        }

        foreach (BillingSnapshot payment in unseen)
        {
            if (await processor.ProcessAsync(payment.Event, payment.RawJson, ct).ConfigureAwait(false) == BillingEventOutcome.Applied)
            {
                paymentsApplied++;
            }
        }

        // A payment Paddle holds that the processor did not apply: the ledger does not have it (B12 names it).
        int paymentsUnapplied = unseen.Count - paymentsApplied;

        if (subscriptionOutcome is { } applied && applied != BillingEventOutcome.Applied)
        {
            return Result(ReconcileOutcome.Stale, drift.Fields, subscriptionsApplied, paymentsApplied,
                $"subscription event {applied.ToString().ToLowerInvariant()}: the provider's state was not applied", paymentsUnapplied);
        }

        return Result(subscriptionsApplied + paymentsApplied > 0 ? ReconcileOutcome.Healed : ReconcileOutcome.InSync,
            drift.Fields, subscriptionsApplied, paymentsApplied, error: null, unapplied: paymentsUnapplied);
    }

    /// <summary>
    /// The upgrade payments not yet on any upgrade row. One already there (its real webhook landed, or an
    /// earlier reconcile fed it) is a no-op in the processor; skipping it keeps an in-sync run "in sync".
    /// </summary>
    private async Task<List<BillingSnapshot>> UnseenPaymentsAsync(string provider, IReadOnlyList<BillingSnapshot> payments, CancellationToken ct)
    {
        List<BillingSnapshot> candidates = payments
            .Where(p => p.Event.Kind == BillingEventKind.UpgradePaid && p.Event.TransactionId is not null)
            .ToList();
        if (candidates.Count == 0) return candidates;

        List<string> ids = candidates.Select(p => p.Event.TransactionId!).Distinct(StringComparer.Ordinal).ToList();
        List<string?> known = await db.BillingPendingUpgradeGrants.AsNoTracking()
            .Where(r => r.Provider == provider && r.TransactionId != null && ids.Contains(r.TransactionId))
            .Select(r => r.TransactionId)
            .ToListAsync(ct).ConfigureAwait(false);
        HashSet<string> seen = new(known.OfType<string>(), StringComparer.Ordinal);
        return candidates.Where(p => !seen.Contains(p.Event.TransactionId!)).ToList();
    }

    /// <summary>A provider read whose body could not be understood, as opposed to a database or programming error.</summary>
    private sealed class MalformedProviderResponseException(Exception inner) : Exception("The billing provider returned a malformed response.", inner);

    /// <summary>
    /// Awaits one provider read; a body the provider could not be parsed from (<see cref="System.Text.Json.JsonException"/>,
    /// a missing member, a member of the wrong JSON kind) is a <see cref="MalformedProviderResponseException"/>, which the
    /// read phase reports as <see cref="ReconcileOutcome.ProviderUnavailable"/>.
    /// </summary>
    private static async Task<T> ProviderRead<T>(Func<Task<T>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new MalformedProviderResponseException(ex);
        }
    }

    private ReconcileResult ProviderUnavailable(Guid organizationId, IBillingProvider provider, SubscriptionDrift drift, string error, Exception? ex)
    {
        logger.LogWarning(BillingWebhookDiagnostics.ReconcileFailed, ex,
            "Reconcile of organization {Org} could not read billing provider {Provider}: {Error}. Nothing applied.", organizationId, provider.Name, error);
        return Result(ReconcileOutcome.ProviderUnavailable, drift.Fields, error: error);
    }

    /// <summary>A short, secret-free description of a provider failure: status and error code, never a body or URL.</summary>
    private static string ShortError(HttpRequestException ex)
    {
        string text = ex switch
        {
            BillingProviderException refused => $"provider refused: HTTP {refused.Status} {refused.Code}",
            { StatusCode: { } status } => $"provider error: HTTP {(int)status}",
            // A 2xx whose body is not what was asked for (e.g. a list without a data array): not a network failure.
            { HttpRequestError: HttpRequestError.InvalidResponse } => MalformedError,
            _ => $"provider unreachable: {ex.HttpRequestError}"
        };
        return Truncate(text, 200)!;
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    /// <summary>
    /// Step 6: the watch records the check; a change is no longer due once healed, in sync or nothing is there.
    /// Only the change this run started from is cleared: one recorded meanwhile (the row changed since
    /// <paramref name="seen"/> — its <c>ChangeRequestedAt</c> or, since an older due time is kept, its <c>UpdatedAt</c> —
    /// or a row that did not exist then) stays due. A row inserted by a concurrent request between the read and
    /// the save is reloaded and updated.
    /// </summary>
    private async Task UpdateWatchAsync(Guid organizationId, BillingReconcileWatch? seen, ReconcileResult result, DateTime now, CancellationToken ct)
    {
        DateTime? startedFrom = seen?.ChangeRequestedAt;
        for (int attempt = 0; ; attempt++)
        {
            BillingReconcileWatch? row = await db.BillingReconcileWatches
                .FirstOrDefaultAsync(w => w.OrganizationId == organizationId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new BillingReconcileWatch { OrganizationId = organizationId, ChangeKind = OverdueKind };
                db.BillingReconcileWatches.Add(row);
            }

            bool failed = result.Outcome is ReconcileOutcome.ProviderUnavailable or ReconcileOutcome.Stale;
            bool recordedDuringRun = row.ChangeRequestedAt is not null
                && (row.ChangeRequestedAt != startedFrom || row.UpdatedAt != seen?.UpdatedAt);
            bool youngCheckout = result.Outcome == ReconcileOutcome.NothingToReconcile
                && row.ChangeKind == CheckoutKind
                && row.ChangeRequestedAt is { } started
                && now - started < options.Value.CheckoutWatch;
            if (result.Outcome is ReconcileOutcome.Healed or ReconcileOutcome.InSync or ReconcileOutcome.NothingToReconcile)
            {
                if (!youngCheckout && !recordedDuringRun) row.ChangeRequestedAt = null;
            }
            else
            {
                // Still due (or newly found due, i.e. overdue): the sweep comes back to it on its next pass.
                if (row.ChangeRequestedAt is null)
                {
                    row.ChangeRequestedAt = now;
                    row.ChangeKind = OverdueKind;
                }
            }

            row.LastCheckedAt = now;
            row.LastOutcome = result.Outcome.ToString();
            row.LastError = Truncate(result.Error, MaxErrorLength);
            row.ConsecutiveFailures = failed ? row.ConsecutiveFailures + 1 : 0;
            row.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt == 0 && db.Entry(row).State == EntityState.Added)
            {
                // A concurrent request inserted the org's watch first: update that one instead.
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    private void Report(Guid organizationId, ReconcileTrigger trigger, ReconcileResult result)
    {
        BillingWebhookDiagnostics.RecordRun(trigger, result.Outcome);
        string drift = string.Join(",", result.Drift);
        switch (result.Outcome)
        {
            case ReconcileOutcome.Healed:
                logger.LogInformation(BillingWebhookDiagnostics.ReconcileHealed,
                    "Reconcile ({Trigger}) healed organization {Org}: drift [{Drift}], {Subscriptions} subscription event(s) and {Transactions} upgrade payment(s) applied from the provider.",
                    trigger, organizationId, drift, result.SubscriptionEventsApplied, result.TransactionsApplied);
                break;
            case ReconcileOutcome.Stale:
                logger.LogWarning(BillingWebhookDiagnostics.ReconcileFailed,
                    "Reconcile ({Trigger}) of organization {Org} found drift [{Drift}] it could not apply: {Error}. Review manually.",
                    trigger, organizationId, drift, result.Error);
                break;
            case ReconcileOutcome.ProviderUnavailable:
                // Already logged with the exception where it was caught.
                break;
            default:
                logger.LogDebug("Reconcile ({Trigger}) of organization {Org}: {Outcome}, drift [{Drift}].", trigger, organizationId, result.Outcome, drift);
                break;
        }
    }

    public async Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct)
    {
        if (Disabled) return;

        for (int attempt = 0; ; attempt++)
        {
            DateTime now = UtcNow;
            BillingReconcileWatch? row = await db.BillingReconcileWatches
                .FirstOrDefaultAsync(w => w.OrganizationId == organizationId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new BillingReconcileWatch { OrganizationId = organizationId };
                db.BillingReconcileWatches.Add(row);
            }

            row.ChangeKind = Truncate(changeKind, MaxKindLength) ?? OverdueKind;
            row.ChangeRequestedAt ??= now;
            row.ChangeRequestedBy = userId;
            row.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                // A reconcile in the same scope (the controller's lazy heal) must compare the watch as stored, not
                // this instance's unrounded timestamps (Postgres keeps microseconds).
                db.Entry(row).State = EntityState.Detached;
                return;
            }
            catch (Exception ex)
            {
                // S-15: never leave the failed watch tracked in the scope's shared DbContext, or the caller's
                // next save (e.g. the promo-discount removal record) re-attempts it and fails with it.
                db.Entry(row).State = EntityState.Detached;
                // A concurrent request inserted the same org's watch first: update that one instead.
                if (ex is DbUpdateException && attempt == 0) continue;
                throw;
            }
        }
    }
}
