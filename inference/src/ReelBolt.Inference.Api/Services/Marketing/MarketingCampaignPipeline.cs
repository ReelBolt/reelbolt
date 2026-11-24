using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>Drains pending Brevo erasures before a send. Returns how many are still pending (0 means clear).</summary>
public interface IBrevoErasureDrain
{
    Task<int> DrainAsync(CancellationToken ct);
}

/// <summary>
/// The send pipeline for one campaign: our consent list is the source of truth, Brevo's managed list is rebuilt
/// from it, verified by reading it back, and only then is the campaign sent. Every failure before the send call
/// fails closed. The send call is made at most once and never retried.
/// </summary>
public sealed class MarketingCampaignPipeline(
    InferenceApiDbContext db,
    MarketingConsentService consents,
    MarketingCampaignService campaigns,
    IBrevoCampaignClient brevo,
    IBrevoErasureDrain erasures,
    BrevoContactGuard guard,
    IOptions<MarketingCampaignOptions> options,
    ILogger<MarketingCampaignPipeline> logger,
    TimeProvider? clock = null,
    ReelBolt.Shared.Inference.ISecretProtector? protector = null,
    IOptionsMonitor<MarketingCampaignOptions>? monitor = null)
{
    private const int PageSize = 500;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private MarketingCampaignOptions Opt => options.Value;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // The lease: the Syncing row's UpdatedAt is the owner token of the run that holds it. Every renewal is a short
    // conditional update (Status Syncing AND UpdatedAt == the value this run last wrote); a run that outlives its
    // lease, or whose row another run claimed, matches no row and aborts without sending. No DB connection is held
    // between renewals. Stored with microsecond precision so the equality survives a Postgres round trip.
    private DateTime _lease;
    private string[]? _imported; // the set once a run may have put it at Brevo
    private Guid? _leaseFor;

    private static DateTime Micro(DateTime t) => new(t.Ticks - (t.Ticks % 10), DateTimeKind.Utc);

    private DateTime NextLease()
    {
        DateTime candidate = Micro(Now);
        return candidate <= _lease ? _lease.AddTicks(10) : candidate;
    }

    private async Task InitLeaseAsync(Guid id, CancellationToken ct)
    {
        if (_leaseFor == id) return;
        DateTime stored = await db.MarketingCampaigns.AsNoTracking().Where(c => c.Id == id).Select(c => c.UpdatedAt).FirstAsync(ct);
        _lease = DateTime.SpecifyKind(stored, DateTimeKind.Utc);
        _leaseFor = id;
    }

    /// <summary>Extends the lease. No row matched means another run owns the campaign now: abort (never send).</summary>
    private async Task RenewLeaseAsync(Guid id, CancellationToken ct)
    {
        DateTime expected = _lease;
        DateTime next = NextLease();
        int rows = await db.MarketingCampaigns
            .Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, next), ct);
        if (rows != 1) throw new PipelineFailure("lease_lost");
        _lease = next;
    }

    private sealed class PipelineFailure(string code) : Exception(code)
    {
        public string Code => code;
    }

    /// <summary>Waits for a Brevo process to complete; failed, cancelled or too slow throws (fail closed).</summary>
    public static async Task WaitForProcessAsync(
        IBrevoCampaignClient brevo, long processId, MarketingCampaignOptions o, CancellationToken ct, Func<Task>? onPoll = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, o.ProcessWaitSeconds));
        while (true)
        {
            string status = await brevo.GetProcessStatusAsync(processId, ct);
            switch (status)
            {
                case "completed":
                    return;
                case "failed":
                case "cancelled":
                    throw new BrevoCampaignException("brevo_process_" + status);
            }

            if (DateTime.UtcNow >= deadline) throw new BrevoCampaignException("brevo_process_timeout");
            if (onPoll is not null) await onPoll();
            if (o.ProcessPollMs > 0) await Task.Delay(o.ProcessPollMs, ct);
        }
    }

    // ----- worker entry points -----

    /// <summary>A stale Syncing row goes Failed (<c>interrupted</c>), or SendUnknown when the send call had begun.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        (int unknown, int failed) = await RecoverStaleAsync(db, brevo, protector, Opt, Now, logger, ct);
        if (unknown + failed > 0)
        {
            logger.LogWarning("Marketing campaign recovery: sendUnknown={Unknown} interrupted={Interrupted}", unknown, failed);
        }
    }

    /// <summary>
    /// The recovery statements, shared with the erasure worker so a Syncing row left behind is recovered even while campaigns
    /// are disabled (and so cannot pause the unsendable-contact sweep for good). A crashed run no longer holds the set it
    /// imported, so before a stale row is settled the managed list is read back and every address on it with no consent row
    /// gets an erasure. A failed read-back (or no key to read with) leaves the row Syncing for the next tick.
    /// </summary>
    internal static async Task<(int Unknown, int Failed)> RecoverStaleAsync(
        InferenceApiDbContext db, IBrevoCampaignClient brevo, ReelBolt.Shared.Inference.ISecretProtector? protector,
        MarketingCampaignOptions o, DateTime now, ILogger logger, CancellationToken ct)
    {
        DateTime cutoff = now.AddMinutes(-Math.Max(0, o.SyncStaleMinutes));
        var stale = await db.MarketingCampaigns.AsNoTracking()
            .Where(c => c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt <= cutoff)
            .Select(c => new { c.Id, c.UpdatedAt, c.DispatchStartedAt })
            .ToListAsync(ct);
        int unknown = 0, failed = 0;
        foreach (var row in stale)
        {
            if (!await StageErasuresFromManagedListAsync(db, brevo, protector, o, now, logger, ct))
            {
                DateTime rowUpdated = DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc);
                if (now - rowUpdated < TimeSpan.FromHours(Math.Max(0, o.SyncRecoveryMaxAgeHours)))
                {
                    logger.LogWarning("Marketing campaign recovery deferred: campaign={CampaignId} (the managed list could not be read back)", row.Id);
                    continue;
                }

                logger.LogWarning("Marketing campaign recovery settled without a read-back: campaign={CampaignId} (the managed list read-back never completed; nothing was staged)", row.Id);
            }

            DateTime updated = DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc);
            if (row.DispatchStartedAt != null)
            {
                unknown += await db.MarketingCampaigns
                    .Where(c => c.Id == row.Id && c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt == updated && c.DispatchStartedAt != null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.SendUnknown)
                        .SetProperty(c => c.FailureCode, "send_unknown").SetProperty(c => c.UpdatedAt, now), ct);
            }
            else
            {
                failed += await db.MarketingCampaigns
                    .Where(c => c.Id == row.Id && c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt == updated && c.DispatchStartedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.Failed)
                        .SetProperty(c => c.FailureCode, "interrupted").SetProperty(c => c.UpdatedAt, now), ct);
            }
        }

        return (unknown, failed);
    }

    /// <summary>Reads the managed list and stages an erasure for each address with no consent row. False: could not read or stage (settle nothing).</summary>
    private static async Task<bool> StageErasuresFromManagedListAsync(
        InferenceApiDbContext db, IBrevoCampaignClient brevo, ReelBolt.Shared.Inference.ISecretProtector? protector,
        MarketingCampaignOptions o, DateTime now, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.BrevoCampaignApiKey)) return false;
        if (o.BrevoListId <= 0) return true; // no list is configured, so none of this run's imports can be read or erased through it
        if (protector is null) return false;
        List<string> onList = [];
        try
        {
            for (int offset = 0; ; offset += PageSize)
            {
                IReadOnlyList<BrevoListContact> page = await brevo.GetListContactsAsync(o.BrevoListId, offset, PageSize, ct);
                onList.AddRange(page.Select(c => MarketingEmail.Normalize(c.Email)));
                if (page.Count < PageSize) break;
            }
        }
        catch (BrevoCampaignException e)
        {
            logger.LogWarning("Marketing recovery could not read the managed list back: code={Code}", e.Code);
            return false;
        }

        await StageMissingAsync(db, protector, [.. onList.Distinct(StringComparer.Ordinal)], now, ct, Math.Max(0, o.RecoveryMaxStagedErasures), logger);
        return true;
    }

    /// <summary>Stages an erasure for every address in <paramref name="addresses"/> with no consent row and none pending yet.</summary>
    internal static async Task StageMissingAsync(
        InferenceApiDbContext db, ReelBolt.Shared.Inference.ISecretProtector protector, string[] addresses, DateTime now, CancellationToken ct,
        int? maxStaged = null, ILogger? logger = null)
    {
        HashSet<string> present = [];
        foreach (string[] part in addresses.Chunk(500))
        {
            present.UnionWith(await db.MarketingConsents.AsNoTracking().Where(c => part.Contains(c.Email)).Select(c => c.Email).ToListAsync(ct));
        }

        string[] purged = [.. addresses.Where(e => !present.Contains(e))];
        if (purged.Length == 0) return;

        // a shared list or hand-added contacts must never be deleted: too many unknown addresses means stage none
        if (maxStaged is { } cap && purged.Length > cap)
        {
            logger?.LogWarning("Marketing recovery staged no erasures: {Count} addresses on the managed list have no consent row, above the bound of {Max}", purged.Length, cap);
            return;
        }

        // an address already waiting for its erasure is not staged twice
        HashSet<string> pending = [];
        foreach (string protectedEmail in await db.MarketingBrevoErasures.AsNoTracking().Select(r => r.ProtectedEmail).ToListAsync(ct))
        {
            if (protector.TryUnprotect(protectedEmail, out string plain)) pending.Add(MarketingEmail.Normalize(plain));
        }

        foreach (string email in purged.Where(e => !pending.Contains(e)))
        {
            db.MarketingBrevoErasures.Add(new MarketingBrevoErasure { Id = Guid.NewGuid(), ProtectedEmail = protector.Protect(email), CreatedAt = now });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Claims the oldest Queued campaign by a conditional update (replica-safe) and runs it. False: nothing claimed.</summary>
    public async Task<bool> ClaimAndRunAsync(CancellationToken ct)
    {
        List<Guid> queued = await db.MarketingCampaigns.AsNoTracking().Where(c => c.Status == MarketingCampaignStatus.Queued)
            .OrderBy(c => c.CreatedAt).Select(c => c.Id).Take(5).ToListAsync(ct);
        foreach (Guid id in queued)
        {
            DateTime now = Micro(Now);
            int claimed = await db.MarketingCampaigns.Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.Syncing).SetProperty(c => c.UpdatedAt, now), ct);
            if (claimed == 1)
            {
                _lease = now;
                _leaseFor = id;
                await RunAsync(id, ct);
                return true;
            }
        }

        return false;
    }

    /// <summary>Runs the pipeline for a campaign already claimed (Syncing).</summary>
    public async Task RunAsync(Guid campaignId, CancellationToken ct)
    {
        try
        {
            await InitLeaseAsync(campaignId, ct);
            await RunStepsAsync(campaignId, ct);
        }
        catch (PipelineFailure f)
        {
            await FailAsync(campaignId, f.Code);
        }
        catch (BrevoCampaignException e)
        {
            await FailAsync(campaignId, e.Code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown: the row stays Syncing and recovery takes it on a worker pass once its lease has lapsed
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("Marketing campaign run failed: campaign={CampaignId} {Error}", campaignId, MailLogRef.DescribeError(ex));
            await FailAsync(campaignId, "pipeline_error");
        }
        finally
        {
            _leaseFor = null;
            await RestageAfterRunAsync(campaignId);
        }
    }

    /// <summary>
    /// Every exit of a run that got as far as the import (success, failure, cancellation) re-stages an erasure for each address
    /// of the set that has no consent row any more, so a person purged while the run held their address cannot stay at Brevo.
    /// </summary>
    private async Task RestageAfterRunAsync(Guid campaignId)
    {
        string[]? imported = _imported;
        _imported = null;
        if (imported is null || protector is null) return;
        try
        {
            await StageMissingAsync(db, protector, imported, Now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError("Marketing campaign could not re-stage erasures after the run: campaign={CampaignId} {Error}", campaignId, MailLogRef.DescribeError(ex));
        }
    }

    /// <summary>Refreshes the results of Sending campaigns.</summary>
    public Task RefreshResultsAsync(CancellationToken ct) => campaigns.RefreshAllSendingAsync(ct);

    /// <summary>
    /// Fails the row, only before the send was attempted: once <c>dispatch_started_at</c> is set the campaign may be out, so
    /// no path here writes Failed (a row left in Syncing becomes SendUnknown by recovery and a human decides).
    /// </summary>
    private Task FailAsync(Guid id, string code) => FailCoreAsync(id, code, beforeDispatchOnly: true);

    /// <summary>Failed after Brevo itself refused <c>sendNow</c> with a definite 4xx answer: nothing was sent.</summary>
    private Task FailRefusedAsync(Guid id, string code) => FailCoreAsync(id, code, beforeDispatchOnly: false);

    private async Task FailCoreAsync(Guid id, string code, bool beforeDispatchOnly)
    {
        // Only the owner may fail the row: a run that lost its lease must not overwrite the run that holds it now.
        DateTime expected = _lease;
        DateTime now = NextLease();
        int rows = await db.MarketingCampaigns
            .Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt == expected
                && (!beforeDispatchOnly || c.DispatchStartedAt == null))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, MarketingCampaignStatus.Failed)
                .SetProperty(c => c.FailureCode, code).SetProperty(c => c.UpdatedAt, now), CancellationToken.None);
        logger.LogWarning("Marketing campaign failed: campaign={CampaignId} code={Code} owned={Owned}", id, code, rows == 1);
    }

    private Task TouchAsync(Guid id, CancellationToken ct) => RenewLeaseAsync(id, ct);

    // ----- the steps -----

    private async Task RunStepsAsync(Guid id, CancellationToken ct)
    {
        MarketingCampaign campaign = await db.MarketingCampaigns.AsNoTracking().FirstAsync(c => c.Id == id, ct);
        if (!Opt.ListSendEnabled) throw new PipelineFailure("list_send_disabled");
        if (string.IsNullOrWhiteSpace(Opt.BrevoCampaignApiKey)) throw new PipelineFailure("brevo_key_missing");
        if (Opt.BrevoListId <= 0) throw new PipelineFailure("brevo_list_missing");
        long listId = Opt.BrevoListId;
        CampaignContent content = MarketingCampaignService.ContentOf(campaign);
        IReadOnlyList<string> contentErrors = campaigns.Validate(content);
        if (contentErrors.Count > 0) throw new PipelineFailure(contentErrors[0]);
        string html = campaigns.BuildHtml(content);

        // 1. pending erasures
        if (await erasures.DrainAsync(ct) > 0) throw new PipelineFailure("erasure_pending");
        await TouchAsync(id, ct);

        // 2. what Brevo knows about the list: own unsubscribes withdraw, every blocked contact is read and classified (never lost)
        await ApplyBrevoSignalsAsync(id, listId, ct);
        await TouchAsync(id, ct);

        // 3. the sendable set
        HashSet<string> sendable = await ReadSendableAsync(ct);
        if (sendable.Count == 0) throw new PipelineFailure("no_recipients");

        // The admin confirmed a count; the set may shrink but never grow past what was confirmed.
        if (campaign.ConfirmedRecipientCount is { } confirmed && sendable.Count > confirmed) throw new PipelineFailure("recipient_count_changed");

        // 4. delete from Brevo every synced contact that is no longer sendable
        List<string> stale = await db.MarketingConsents.AsNoTracking().Where(c => c.BrevoSyncedAt != null).Select(c => c.Email).ToListAsync(ct);
        foreach (string email in stale.Where(e => !sendable.Contains(e)))
        {
            await RenewLeaseAsync(id, ct);
            // read, classify, commit any withdrawal, only then delete; a failed read fails the run closed
            await guard.DeleteStaleAsync(email, ct);
        }

        await TouchAsync(id, ct);

        // 5. write-ahead: flag every row of the set as synced BEFORE anything is imported, in one conditional update.
        // From here a purge stages its erasure, and a crash or timeout leaves the flag set so the sweep deletes the contact.
        // If the affected count differs from the set, a row was purged or changed meanwhile: fail with nothing imported.
        string[] ordered = [.. sendable.Order(StringComparer.Ordinal)];
        DateTime flaggedAt = Now;
        int flagged = await db.MarketingConsents
            .Where(c => ordered.Contains(c.Email) && c.Status == MarketingConsentStatus.OptedIn && c.ConfirmedAt != null && c.UndeliverableAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrevoSyncedAt, flaggedAt), ct);
        if (flagged != ordered.Length) throw new PipelineFailure("consent_changed");

        // The names are read AFTER the flag update, so a name change committed later has a newer UpdatedAt than flaggedAt and the
        // contact sync worker re-pushes it.
        Dictionary<string, (string? First, string? Last)> names = [];
        foreach (string[] part in ordered.Chunk(1000))
        {
            foreach (var n in await db.MarketingConsents.AsNoTracking().Where(c => part.Contains(c.Email)).Select(c => new { c.Email, c.FirstName, c.LastName }).ToListAsync(ct))
            {
                names[n.Email] = (n.FirstName, n.LastName);
            }
        }

        // remove all, import the sendable set in chunks, wait for each process
        _imported = ordered;
        await WaitForProcessAsync(brevo, await brevo.RemoveAllFromListAsync(listId, ct), Opt, ct, () => RenewLeaseAsync(id, ct));
        foreach (string[] chunk in ordered.Chunk(Math.Max(1, Opt.ImportChunkSize)))
        {
            BrevoContactInput[] inputs = [.. chunk.Select(e => names.TryGetValue(e, out var n) ? new BrevoContactInput(e, n.First, n.Last) : new BrevoContactInput(e))];
            await WaitForProcessAsync(brevo, await brevo.ImportAsync(listId, inputs, ct), Opt, ct, () => RenewLeaseAsync(id, ct));

            // So the contact sync worker does not push these rows again: pushed as of the flag time, a later change is newer.
            string[] done = chunk;
            await db.MarketingConsents
                .Where(c => done.Contains(c.Email) && c.BrevoSyncedAt == flaggedAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.BrevoPushedAt, (DateTime?)flaggedAt)
                    .SetProperty(c => c.BrevoPushAttempts, 0)
                    .SetProperty(c => c.BrevoPushErrorCode, (string?)null), ct);
            await TouchAsync(id, ct);
        }

        // A row purged during the run may have read before the flag: its erasure was never staged. Stage one with the
        // address this run holds, so the contact cannot stay at Brevo even if the drain ran before the import.
        await StageErasuresForPurgedAsync(ordered, ct);

        // 6. read the list back: exactly the sendable set, nobody blocked, and the count agrees
        List<BrevoListContact> onList = await ReadListAsync(id, listId, ct);
        BrevoListContact[] blocked = [.. onList.Where(c => IsBlocked(c, listId))];
        if (blocked.Length > 0)
        {
            foreach (BrevoListContact c in blocked)
            {
                await RenewLeaseAsync(id, ct);
                await guard.ClassifyAsync(c.Email, listSaysBlocked: true, ct);
            }

            throw new PipelineFailure("list_blocked_contact");
        }

        HashSet<string> members = onList.Select(c => c.Email).ToHashSet(StringComparer.Ordinal);
        if (!members.SetEquals(sendable)) throw new PipelineFailure("list_mismatch");
        if (await brevo.GetListSubscriberCountAsync(listId, ct) != sendable.Count) throw new PipelineFailure("list_count_mismatch");

        // 7. the sendable set must not have changed meanwhile (a withdrawal since step 3 stops the send)
        HashSet<string> again = await ReadSendableAsync(ct);
        if (!again.SetEquals(sendable)) throw new PipelineFailure("list_changed");
        await TouchAsync(id, ct);

        // 8. a purge since step 1 stages an erasure: check before a campaign is created at all, then create it, re-read
        // everything as the last thing before dispatch, record the intent under the lease, send once.
        if (await db.MarketingBrevoErasures.AnyAsync(ct)) throw new PipelineFailure("erasure_pending");
        long brevoCampaignId = await brevo.CreateCampaignAsync(
            new BrevoCampaignDraft($"reelbolt-{campaign.Id:N}", campaign.Subject.Trim(), html, Opt.FromAddress, Opt.FromName, [listId]), ct);
        try
        {
            // Read through the monitor so a reloadable source is honoured here (a value from an environment variable
            // is bound once and needs a restart, which also ends this run).
            if (!(monitor?.CurrentValue ?? Opt).ListSendEnabled) throw new PipelineFailure("list_send_disabled");

            // Any difference since step 7 (a withdrawal, a purge, a staged erasure) fails closed with no sendNow.
            if (!(await ReadSendableAsync(ct)).SetEquals(sendable)) throw new PipelineFailure("list_changed");
            if (await db.MarketingBrevoErasures.AnyAsync(ct)) throw new PipelineFailure("erasure_pending");

            // Ownership is verified and the intent recorded in one conditional update, directly before dispatch.
            DateTime expected = _lease;
            DateTime dispatchAt = NextLease();
            int recorded = await db.MarketingCampaigns
                .Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt == expected)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.BrevoCampaignId, brevoCampaignId)
                    .SetProperty(c => c.DispatchStartedAt, dispatchAt).SetProperty(c => c.UpdatedAt, dispatchAt), ct);
            if (recorded != 1) throw new PipelineFailure("lease_lost");
            _lease = dispatchAt;
        }
        catch (Exception)
        {
            // Nothing was sent: drop the draft campaign created for this run.
            await BestEffortDeleteAsync(brevoCampaignId);
            throw;
        }

        try
        {
            await brevo.SendNowAsync(brevoCampaignId, CancellationToken.None);
        }
        catch (BrevoCampaignException e) when (e.OutcomeUnknown)
        {
            await SetOutcomeAsync(id, MarketingCampaignStatus.SendUnknown, "send_unknown", null);
            return;
        }
        catch (BrevoCampaignException e)
        {
            await BestEffortDeleteAsync(brevoCampaignId);
            await FailRefusedAsync(id, e.Code);
            return;
        }

        if (await SetOutcomeAsync(id, MarketingCampaignStatus.Sending, null, sendable.Count))
        {
            logger.LogInformation("Marketing campaign sending: campaign={CampaignId} recipients={Recipients}", id, sendable.Count);
        }
    }

    private async Task StageErasuresForPurgedAsync(string[] ordered, CancellationToken ct)
    {
        if (protector is null)
        {
            bool anyPurged = false;
            foreach (string[] part in ordered.Chunk(500))
            {
                int present = await db.MarketingConsents.AsNoTracking().CountAsync(c => part.Contains(c.Email), ct);
                if (present != part.Length) anyPurged = true;
            }

            if (anyPurged) throw new PipelineFailure("erasure_unavailable");
            return;
        }

        await StageMissingAsync(db, protector, ordered, Now, ct);
    }

    private const int OutcomeWriteAttempts = 3;

    /// <summary>
    /// Records the outcome after <c>sendNow</c>. A database failure is retried a bounded number of times; if it persists the
    /// row is left Syncing with its dispatch time set, which recovery turns into SendUnknown. It never writes Failed.
    /// </summary>
    private async Task<bool> SetOutcomeAsync(Guid id, MarketingCampaignStatus status, string? code, int? finalCount)
    {
        DateTime expected = _lease;
        DateTime now = NextLease();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                int rows = await db.MarketingCampaigns.Where(c => c.Id == id && c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt == expected)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status).SetProperty(c => c.FailureCode, code)
                        .SetProperty(c => c.FinalRecipientCount, finalCount).SetProperty(c => c.UpdatedAt, now), CancellationToken.None);
                if (rows != 1)
                {
                    logger.LogWarning("Marketing campaign outcome matched no row (the lease moved on): campaign={CampaignId} outcome={Outcome}", id, status);
                }

                return rows == 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Marketing campaign outcome not recorded: campaign={CampaignId} attempt={Attempt} {Error}",
                    id, attempt, MailLogRef.DescribeError(ex));
                if (attempt >= OutcomeWriteAttempts) return false;
            }
        }
    }

    private async Task BestEffortDeleteAsync(long brevoCampaignId)
    {
        try
        {
            await brevo.DeleteCampaignAsync(brevoCampaignId, CancellationToken.None);
        }
        catch (BrevoCampaignException)
        {
            // best effort
        }
    }

    private async Task<HashSet<string>> ReadSendableAsync(CancellationToken ct)
    {
        (IReadOnlyList<(Guid ConsentId, string Email)> recipients, _) = await consents.SendableRecipientsAsync(ct);
        return recipients.Select(r => MarketingEmail.Normalize(r.Email)).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<BrevoListContact>> ReadListAsync(Guid id, long listId, CancellationToken ct)
    {
        List<BrevoListContact> all = [];
        for (int offset = 0; ; offset += PageSize)
        {
            await RenewLeaseAsync(id, ct);
            IReadOnlyList<BrevoListContact> page = await brevo.GetListContactsAsync(listId, offset, PageSize, ct);
            all.AddRange(page);
            if (page.Count < PageSize) return all;
        }
    }

    private static bool IsBlocked(BrevoListContact c, long listId) => c.EmailBlacklisted || c.ListUnsubscribed.Contains(listId);

    private async Task ApplyBrevoSignalsAsync(Guid id, long listId, CancellationToken ct)
    {
        foreach (BrevoListContact c in (await ReadListAsync(id, listId, ct)).Where(c => IsBlocked(c, listId)))
        {
            await RenewLeaseAsync(id, ct);
            await guard.ClassifyAsync(c.Email, listSaysBlocked: true, ct);
        }
    }
}
