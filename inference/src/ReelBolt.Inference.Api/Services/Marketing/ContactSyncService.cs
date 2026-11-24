using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>Process-wide timing the scoped sync service needs to remember between passes.</summary>
public sealed class ContactSyncSchedule
{
    public DateTime? LastNameReconcileAt { get; set; }
}

/// <summary>What one pass did. Counts only.</summary>
public sealed record ContactSyncResult(
    bool Ran, string? SkippedReason, int Pushed, int Failed, int Skipped, int Deleted, bool Reconciled, int ReAdded, int Removed, int Classified);

/// <summary>The numbers the admin status endpoint shows.</summary>
public sealed record ContactSyncStatus(
    bool Enabled, bool Configured, bool CampaignActive, DateTime? LastRunAt, bool? LastRunOk, string? LastErrorCode, DateTime? LastReconcileAt,
    int Synced, int PendingPush, int Failed);

public enum ContactSyncRequestOutcome
{
    Accepted,
    Disabled,
    RateLimited,
}

/// <summary>
/// One pass of the Brevo contact sync: pushes new and changed sendable contacts (names included), deletes the ones that
/// stopped being sendable, and once a night reads the managed list back and repairs it. Nothing is logged but counts and
/// stable codes: never an address or a name. Single-flight across replicas through a conditional update of the state row.
/// </summary>
public sealed class ContactSyncService(
    InferenceApiDbContext db,
    MarketingNameReconciler nameReconciler,
    BrevoContactGuard guard,
    BrevoErasureOutbox outbox,
    MarketingConsentService consents,
    IBrevoCampaignClient brevo,
    IOptions<MarketingCampaignOptions> campaignOptions,
    IOptions<MarketingContactSyncOptions> syncOptions,
    ContactSyncSchedule schedule,
    ILogger<ContactSyncService> logger,
    TimeProvider? clock = null)
{
    private const int StateId = 1;
    private const int ListPageSize = 500;
    private static readonly TimeSpan ClaimGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RunRequestGap = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconcileGap = TimeSpan.FromHours(20);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The exact LastRunAt this pass last wrote; renew and release only touch the state row while it still holds it.</summary>
    private DateTime _token;

    private DateTime _lastRenewAt;
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(60);

    private sealed class LeaseLostException : Exception;

    /// <summary>Renew every 5 items or every 60 seconds, whichever comes first (a slow Brevo).</summary>
    private bool RenewDue(int step) => step % 5 == 0 || Now - _lastRenewAt >= RenewEvery;

    /// <summary>The renew the outbox calls before each Brevo call: false when the lease is lost.</summary>
    private async Task<bool> TryRenewAsync(CancellationToken ct)
    {
        try
        {
            await RenewAsync(ct);
            return true;
        }
        catch (LeaseLostException)
        {
            return false;
        }
    }
    private MarketingCampaignOptions Campaign => campaignOptions.Value;
    private MarketingContactSyncOptions Sync => syncOptions.Value;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The key and the managed list are both set.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(Campaign.BrevoCampaignApiKey) && Campaign.BrevoListId > 0;

    /// <summary>Enabled and configured: the only state in which Brevo is ever called.</summary>
    public bool Active => Sync.Enabled && Configured;

    /// <summary>Makes sure the single state row exists (a lost insert race is fine).</summary>
    public async Task EnsureStateAsync(CancellationToken ct)
    {
        if (await db.MarketingContactSyncStates.AnyAsync(s => s.Id == StateId, ct))
        {
            return;
        }

        try
        {
            db.MarketingContactSyncStates.Add(new MarketingContactSyncState { Id = StateId });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // another replica inserted it
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Records a Sync now request. At most one per 30 seconds.</summary>
    public async Task<ContactSyncRequestOutcome> RequestRunAsync(CancellationToken ct)
    {
        if (!Active)
        {
            return ContactSyncRequestOutcome.Disabled;
        }

        await EnsureStateAsync(ct);
        DateTime now = Now;
        DateTime cutoff = now - RunRequestGap;
        int n = await db.MarketingContactSyncStates
            .Where(s => s.Id == StateId && (s.RunRequestedAt == null || s.RunRequestedAt < cutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RunRequestedAt, (DateTime?)now), ct);
        return n == 1 ? ContactSyncRequestOutcome.Accepted : ContactSyncRequestOutcome.RateLimited;
    }

    /// <summary>True when a Sync now request is waiting.</summary>
    public async Task<bool> RunRequestedAsync(CancellationToken ct)
        => await db.MarketingContactSyncStates.AsNoTracking().AnyAsync(s => s.Id == StateId && s.RunRequestedAt != null, ct);

    public async Task<ContactSyncStatus> StatusAsync(CancellationToken ct)
    {
        MarketingContactSyncState? state = await db.MarketingContactSyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == StateId, ct);
        HashSet<Guid> ids = await SendableIdsAsync(ct);
        var rows = (await db.MarketingConsents.AsNoTracking()
            .Select(c => new { c.Id, c.BrevoPushedAt, c.UpdatedAt, c.NameUpdatedAt, c.BrevoPushAttempts })
            .ToListAsync(ct)).Where(c => ids.Contains(c.Id)).ToList();
        bool IsDirty(DateTime? pushed, DateTime updated, DateTime? nameAt) => pushed is null || pushed < updated || (nameAt is not null && pushed < nameAt);
        int synced = rows.Count(c => !IsDirty(c.BrevoPushedAt, c.UpdatedAt, c.NameUpdatedAt));
        int pending = rows.Count(c => IsDirty(c.BrevoPushedAt, c.UpdatedAt, c.NameUpdatedAt));
        int failed = rows.Count(c => c.BrevoPushAttempts > 0);
        bool active = await CampaignActivity.ActiveAsync(db, Campaign, Now, ct);
        return new ContactSyncStatus(
            Sync.Enabled, Configured, active, state?.LastRunAt, state?.LastRunOk, state?.LastErrorCode, state?.LastReconcileAt, synced, pending, failed);
    }

    /// <summary>The ids of the sendable set: exactly the export and campaign definition, suppression included.</summary>
    private async Task<HashSet<Guid>> SendableIdsAsync(CancellationToken ct)
    {
        (IReadOnlyList<(Guid ConsentId, string Email)> recipients, _) = await consents.SendableRecipientsAsync(ct);
        return recipients.Select(r => r.ConsentId).ToHashSet();
    }

    /// <summary>The sendable rows as a write-ahead guard (the same rules as the consent export, minus the keyed suppression check).</summary>
    private IQueryable<MarketingConsent> Sendable(IQueryable<MarketingConsent> q)
        => q.Where(c => c.Status == MarketingConsentStatus.OptedIn && c.ConfirmedAt != null && c.UndeliverableAt == null)
            .Where(c => c.UserId == null || db.ApplicationUsers.Any(u => u.Id == c.UserId && u.EmailVerifiedAt != null && !u.IsServiceAccount));

    private static IQueryable<MarketingConsent> Dirty(IQueryable<MarketingConsent> q)
        => q.Where(c => c.BrevoPushedAt == null || c.BrevoSyncedAt == null || c.BrevoPushedAt < c.UpdatedAt || (c.NameUpdatedAt != null && c.BrevoPushedAt < c.NameUpdatedAt));

    /// <summary>One pass. <paramref name="onlyIfRequested"/>: run only when Sync now is waiting.</summary>
    public async Task<ContactSyncResult> PassAsync(CancellationToken ct, bool onlyIfRequested = false)
    {
        if (!Active)
        {
            return Skip("inactive");
        }

        await EnsureStateAsync(ct);
        MarketingContactSyncState state = await db.MarketingContactSyncStates.AsNoTracking().FirstAsync(s => s.Id == StateId, ct);
        DateTime? requestedAt = state.RunRequestedAt;
        if (onlyIfRequested && requestedAt is null)
        {
            return Skip("not_requested");
        }

        // A campaign run rebuilds the list itself; nothing is pushed or deleted underneath it.
        if (await CampaignActivity.ActiveAsync(db, Campaign, Now, ct))
        {
            return Skip("campaign_active");
        }

        // Single-flight: claim the state row. LastRunOk null marks a run in progress (a crashed one expires after ten minutes).
        DateTime start = Now;
        DateTime gapCutoff = start - ClaimGap;
        DateTime leaseCutoff = start - ClaimLease;
        int claimed = await db.MarketingContactSyncStates
            .Where(s => s.Id == StateId && (s.LastRunAt == null || s.LastRunAt <= gapCutoff) && (s.LastRunOk != null || s.LastRunAt == null || s.LastRunAt < leaseCutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, (DateTime?)start).SetProperty(x => x.LastRunOk, (bool?)null), ct);
        if (claimed != 1)
        {
            return Skip("other_run");
        }

        _token = start;
        _lastRenewAt = start;

        int pushed = 0, failed = 0, skipped = 0, deleted = 0, readded = 0, removed = 0, classified = 0;
        bool reconciled = false;
        string? error = null;
        try
        {
            await RenewAsync(ct);
            if (requestedAt is not null || schedule.LastNameReconcileAt is not { } last || start - last >= TimeSpan.FromSeconds(Math.Max(1, Sync.NameReconcileSeconds)))
            {
                int changedNames = await nameReconciler.ReconcileAsync(ct);
                schedule.LastNameReconcileAt = start;
                if (changedNames > 0)
                {
                    logger.LogInformation("Contact sync name reconcile: changed={Changed}", changedNames);
                }
            }

            await RenewAsync(ct);
            (pushed, failed, skipped) = await PushAsync(await SendableIdsAsync(ct), ct);

            // Withdrawals and erasures reach Brevo within about a minute. Both are idempotent and safe next to the erasure worker.
            await RenewAsync(ct);
            await outbox.DrainWithBackoffAsync(ct, fromSyncPass: true, renew: TryRenewAsync);
            await RenewAsync(ct);
            deleted = await outbox.DeleteUnsendableAsync(ct, fromSyncPass: true, renew: TryRenewAsync);

            if (NightlyDue(state.LastReconcileAt, start))
            {
                await RenewAsync(ct);
                (readded, removed, classified) = await ReconcileListAsync(ct);
                reconciled = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LeaseLostException)
        {
            // Another replica owns the state row now: leave it exactly as it is.
            logger.LogWarning("Contact sync pass lost its lease and stopped");
            return Skip("lease_lost");
        }
        catch (BrevoCampaignException e)
        {
            error = e.Code;
        }
        catch (Exception ex)
        {
            error = "sync_failed";
            logger.LogError("The Brevo contact sync pass failed; retrying on the next tick: {Error}", MailLogRef.DescribeError(ex));
        }

        DateTime end = Now;
        bool ok = error is null;
        DateTime token = _token;
        int released = await db.MarketingContactSyncStates.Where(s => s.Id == StateId && s.LastRunOk == null && s.LastRunAt == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunAt, (DateTime?)end)
                .SetProperty(x => x.LastRunOk, (bool?)ok)
                .SetProperty(x => x.LastErrorCode, error), CancellationToken.None);
        if (released != 1)
        {
            logger.LogWarning("Contact sync pass lost its lease before it ended");
            return Skip("lease_lost");
        }

        if (reconciled)
        {
            await db.MarketingContactSyncStates.Where(s => s.Id == StateId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastReconcileAt, (DateTime?)end), CancellationToken.None);
        }

        if (requestedAt is { } req)
        {
            // A request that arrived during the run stays for the next pass.
            await db.MarketingContactSyncStates.Where(s => s.Id == StateId && s.RunRequestedAt <= req)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RunRequestedAt, (DateTime?)null), CancellationToken.None);
        }

        logger.LogInformation(
            "Contact sync pass: ok={Ok} code={Code} pushed={Pushed} failed={Failed} skipped={Skipped} deleted={Deleted} reconciled={Reconciled} readded={ReAdded} removed={Removed} classified={Classified}",
            ok, error, pushed, failed, skipped, deleted, reconciled, readded, removed, classified);
        return new ContactSyncResult(true, null, pushed, failed, skipped, deleted, reconciled, readded, removed, classified);
    }

    private static ContactSyncResult Skip(string reason) => new(false, reason, 0, 0, 0, 0, false, 0, 0, 0);

    private bool NightlyDue(DateTime? lastReconcileAt, DateTime now)
        => now.Hour == Sync.NightlyReconcileHourUtc && (lastReconcileAt is null || now - lastReconcileAt.Value >= ReconcileGap);

    private async Task<(int Pushed, int Failed, int Skipped)> PushAsync(HashSet<Guid> sendableIds, CancellationToken ct)
    {
        int max = Math.Max(1, Sync.MaxPerTick);
        // Dirty rows, then intersected with the one sendable definition (suppression included) computed for this pass.
        var candidates = (await Dirty(Sendable(db.MarketingConsents.AsNoTracking()))
            .Select(c => new { c.Id, c.Email, c.BrevoPushedAt, c.BrevoSyncedAt, c.BrevoPushAttempts, c.BrevoPushAttemptedAt })
            .ToListAsync(ct))
            .Where(c => sendableIds.Contains(c.Id))
            .OrderBy(c => c.BrevoPushAttemptedAt is null ? 0 : 1).ThenBy(c => c.BrevoPushAttemptedAt).ThenBy(c => c.Id)
            .Take(max * 4)
            .ToList();

        DateTime now = Now;
        int pushed = 0, failed = 0, skipped = 0, rowsDone = 0;
        foreach (var row in candidates.Where(r => r.BrevoPushAttempts == 0 || r.BrevoPushAttemptedAt is null || now >= r.BrevoPushAttemptedAt + Sync.PushBackoff(r.BrevoPushAttempts)).Take(max))
        {
            ct.ThrowIfCancellationRequested();
            Guid id = row.Id;
            string email = row.Email;
            // Evaluated BEFORE the write-ahead flag sets BrevoSyncedAt: a contact that is not at Brevo is a guarded first add.
            bool firstAdd = row.BrevoPushedAt is null || row.BrevoSyncedAt is null;
            if (RenewDue(++rowsDone))
            {
                await RenewAsync(ct);
            }

            // Write-ahead: from here a purge stages its erasure. Zero rows: the row changed or was purged, nothing is pushed.
            DateTime flagAt = Now;
            int flagged = await Sendable(db.MarketingConsents).Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrevoSyncedAt, c => c.BrevoSyncedAt ?? flagAt), ct);
            if (flagged != 1)
            {
                skipped++;
                continue;
            }

            try
            {
                if (firstAdd)
                {
                    BrevoContactState state = await guard.ClassifyAsync(email, listSaysBlocked: false, ct);
                    if (state is BrevoContactState.Withdrawn or BrevoContactState.Undeliverable)
                    {
                        skipped++;
                        continue;
                    }
                }

                // The time BEFORE the names are read: a change committed meanwhile has a newer stamp and is pushed again.
                DateTime pushStart = Now;
                var names = await db.MarketingConsents.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.FirstName, c.LastName }).FirstOrDefaultAsync(ct);
                if (names is null)
                {
                    skipped++;
                    continue;
                }

                await brevo.UpsertContactAsync(new BrevoContactInput(email, names.FirstName, names.LastName), firstAdd ? Campaign.BrevoListId : null, ct);
                await db.MarketingConsents.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.BrevoPushedAt, (DateTime?)pushStart)
                    .SetProperty(c => c.BrevoPushAttempts, 0)
                    .SetProperty(c => c.BrevoPushErrorCode, (string?)null), ct);

                // A purge or withdrawal that drained while the upsert was in flight must not be undone: an update recreates the
                // contact outside the list, where no sweep would find it. Re-read, and delete again at once when it is gone.
                if (!await Sendable(db.MarketingConsents.AsNoTracking()).AnyAsync(c => c.Id == id, ct))
                {
                    await brevo.DeleteContactAsync(email, ct);
                    await db.MarketingConsents.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.BrevoSyncedAt, (DateTime?)null)
                        .SetProperty(c => c.BrevoPushedAt, (DateTime?)null)
                        .SetProperty(c => c.BrevoPushAttempts, 0)
                        .SetProperty(c => c.BrevoPushErrorCode, (string?)null), ct);
                    skipped++;
                    continue;
                }

                pushed++;
            }
            catch (BrevoCampaignException e)
            {
                failed++;
                DateTime attemptedAt = Now;
                string code = e.Code.Length > 100 ? e.Code[..100] : e.Code;
                await db.MarketingConsents.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.BrevoPushAttempts, c => c.BrevoPushAttempts + 1)
                    .SetProperty(c => c.BrevoPushAttemptedAt, (DateTime?)attemptedAt)
                    .SetProperty(c => c.BrevoPushErrorCode, code), CancellationToken.None);
                logger.LogWarning("Brevo contact push failed: consent={ConsentId} attempts={Attempts} code={Code}", id, row.BrevoPushAttempts + 1, e.Code);
            }
        }

        return (pushed, failed, skipped);
    }

    /// <summary>
    /// Keeps the single-flight claim alive and moves the token. A pass whose claim was taken over (zero rows) stops at once.
    /// </summary>
    private async Task RenewAsync(CancellationToken ct)
    {
        DateTime old = _token;
        DateTime next = Now;
        if (next <= old)
        {
            next = old.AddTicks(1);
        }

        int n = await db.MarketingContactSyncStates.Where(s => s.Id == StateId && s.LastRunOk == null && s.LastRunAt == old)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastRunAt, (DateTime?)next), ct);
        if (n != 1)
        {
            throw new LeaseLostException();
        }

        _token = next;
        _lastRenewAt = Now;
    }

    /// <summary>
    /// Reads the managed list and repairs it: blocked members are classified (a withdrawal is committed, then the contact is
    /// deleted), members that are not sendable are deleted, sendable rows missing from the list are queued for a guarded
    /// first add. A failed list read throws before anything is changed.
    /// </summary>
    private async Task<(int ReAdded, int Removed, int Classified)> ReconcileListAsync(CancellationToken ct)
    {
        long listId = Campaign.BrevoListId;
        List<BrevoListContact> onList = [];
        for (int offset = 0; ; offset += ListPageSize)
        {
            await RenewAsync(ct);
            IReadOnlyList<BrevoListContact> page = await brevo.GetListContactsAsync(listId, offset, ListPageSize, ct);
            onList.AddRange(page);
            if (page.Count < ListPageSize)
            {
                break;
            }
        }

        (IReadOnlyList<(Guid ConsentId, string Email)> recipients, _) = await consents.SendableRecipientsAsync(ct);
        HashSet<string> sendable = recipients.Select(r => MarketingEmail.Normalize(r.Email)).ToHashSet(StringComparer.Ordinal);
        int cap = Math.Max(1, Sync.MaxPerTick);
        int removed = 0, classified = 0;

        int steps = 0;
        foreach (BrevoListContact member in onList.Where(m => m.EmailBlacklisted || m.ListUnsubscribed.Contains(listId)).Take(cap))
        {
            ct.ThrowIfCancellationRequested();
            if (RenewDue(steps++))
            {
                await RenewAsync(ct);
            }

            try
            {
                BrevoContactState state = await guard.ClassifyAsync(member.Email, listSaysBlocked: true, ct);
                classified++;
                if (state is BrevoContactState.Withdrawn or BrevoContactState.Undeliverable)
                {
                    await guard.DeleteStaleAsync(member.Email, ct);
                    removed++;
                }
            }
            catch (BrevoCampaignException e)
            {
                logger.LogWarning("Contact sync reconcile: blocked member could not be processed; retried next night: code={Code}", e.Code);
            }
        }

        foreach (BrevoListContact member in onList.Where(m => !sendable.Contains(m.Email) && !m.EmailBlacklisted && !m.ListUnsubscribed.Contains(listId)).Take(cap))
        {
            ct.ThrowIfCancellationRequested();
            if (RenewDue(steps++))
            {
                await RenewAsync(ct);
            }

            try
            {
                await guard.DeleteStaleAsync(member.Email, ct);
                removed++;
            }
            catch (BrevoCampaignException e)
            {
                logger.LogWarning("Contact sync reconcile: stale member could not be deleted; retried next night: code={Code}", e.Code);
            }
        }

        HashSet<string> members = onList.Select(m => m.Email).ToHashSet(StringComparer.Ordinal);
        List<string> pushedEmails = await db.MarketingConsents.AsNoTracking()
            .Where(c => c.BrevoPushedAt != null)
            .Select(c => c.Email)
            .ToListAsync(ct);
        string[] toReAdd = [.. pushedEmails.Where(e => sendable.Contains(e) && !members.Contains(e))];
        int readded = 0;
        foreach (string[] chunk in toReAdd.Chunk(500))
        {
            await RenewAsync(ct);
            readded += await db.MarketingConsents.Where(c => chunk.Contains(c.Email))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrevoPushedAt, (DateTime?)null), ct);
        }

        return (readded, removed, classified);
    }
}
