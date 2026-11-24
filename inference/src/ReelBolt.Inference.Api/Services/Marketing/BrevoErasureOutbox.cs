using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Drains the Brevo erasure outbox (<c>marketing_brevo_erasures</c>) and deletes Brevo contacts that are
/// synced but no longer sendable. Inert while the campaign key is blank: rows stay in place and count as
/// pending. 204 and 404 from Brevo are both success. No log line carries an address.
/// </summary>
public sealed class BrevoErasureOutbox(
    InferenceApiDbContext db,
    MarketingConsentService consents,
    BrevoContactGuard guard,
    IBrevoCampaignClient brevo,
    ISecretProtector protector,
    IOptions<MarketingCampaignOptions> options,
    ILogger<BrevoErasureOutbox> logger,
    TimeProvider? clock = null) : IBrevoErasureDrain
{
    private const int Batch = 200;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private bool KeyBlank => string.IsNullOrWhiteSpace(options.Value.BrevoCampaignApiKey);

    /// <summary>Tries every pending row now (the send pipeline's step 1). Returns how many remain.</summary>
    public Task<int> DrainAsync(CancellationToken ct) => DrainCoreAsync(respectBackoff: false, ct);

    /// <summary>The worker's pass: rows still inside their backoff are left alone but still counted as pending.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <param name="fromSyncPass">The contact sync pass runs this: it does not wait for its own lease.</param>
    /// <param name="renew">Called before every row (each is a Brevo call); false means the lease was lost and the drain stops.</param>
    public Task<int> DrainWithBackoffAsync(CancellationToken ct, bool fromSyncPass = false, Func<CancellationToken, Task<bool>>? renew = null)
        => DrainCoreAsync(respectBackoff: true, ct, fromSyncPass || renew is not null, renew);

    /// <summary>True while a contact sync pass holds its single-flight lease (it is pushing contacts right now).</summary>
    private async Task<bool> SyncInFlightAsync(CancellationToken ct)
    {
        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(10);
        return await db.MarketingContactSyncStates.AsNoTracking().AnyAsync(s => s.LastRunOk == null && s.LastRunAt != null && s.LastRunAt > cutoff, ct);
    }

    private async Task<int> DrainCoreAsync(bool respectBackoff, CancellationToken ct, bool fromSyncPass = false, Func<CancellationToken, Task<bool>>? renew = null)
    {
        if (KeyBlank)
        {
            return await db.MarketingBrevoErasures.CountAsync(ct);
        }

        // While a campaign is Syncing its run may hold the address of a person who was just purged: deleting the contact now
        // would let the run's import put it back. The run re-stages and the drain waits until it has ended. The pipeline's own
        // step 1 (not respectBackoff) is the one drain that runs inside a run, before anything is imported.
        if (respectBackoff && (await LiveSyncingAsync(ct) || (!fromSyncPass && await SyncInFlightAsync(ct))))
        {
            return await db.MarketingBrevoErasures.CountAsync(ct);
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        List<MarketingBrevoErasure> rows = await db.MarketingBrevoErasures.OrderBy(r => r.CreatedAt).Take(Batch).ToListAsync(ct);
        foreach (MarketingBrevoErasure row in rows)
        {
            if (respectBackoff && row.LastAttemptAt is { } last && now < last + Backoff(row.Attempts))
            {
                continue;
            }

            if (renew is not null && !await renew(ct))
            {
                break;
            }

            bool ok = false;
            if (protector.TryUnprotect(row.ProtectedEmail, out string email) && email.Length > 0)
            {
                try
                {
                    await brevo.DeleteContactAsync(email, ct);
                    ok = true;
                }
                catch (BrevoCampaignException e)
                {
                    logger.LogWarning("Brevo erasure failed: erasure={ErasureId} attempts={Attempts} code={Code}", row.Id, row.Attempts + 1, e.Code);
                }
            }
            else
            {
                logger.LogWarning("Brevo erasure address could not be read: erasure={ErasureId}", row.Id);
            }

            if (ok)
            {
                db.MarketingBrevoErasures.Remove(row);
            }
            else
            {
                row.Attempts++;
                row.LastAttemptAt = now;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The contact sync worker's pass (or another replica) drained this row first: both are idempotent.
                db.Entry(row).State = EntityState.Detached;
            }
        }

        return await db.MarketingBrevoErasures.CountAsync(ct);
    }

    /// <summary>True while a Syncing row's lease is live. A stale row (a crashed run) never defers an erasure, even while its read-back keeps failing.</summary>
    private Task<bool> LiveSyncingAsync(CancellationToken ct)
        => CampaignActivity.LiveSyncingAsync(db, options.Value, _clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>2, 4, 8 ... minutes, capped at one hour.</summary>
    internal static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts, 1, 6))));

    /// <summary>Deletes from Brevo every synced contact that is not sendable and clears its sync mark. Returns how many.</summary>
    public async Task<int> DeleteUnsendableAsync(CancellationToken ct, bool fromSyncPass = false, Func<CancellationToken, Task<bool>>? renew = null)
    {
        fromSyncPass |= renew is not null;
        if (KeyBlank)
        {
            return 0;
        }

        // A running contact sync pass runs this sweep itself at its end; another one must not interleave with its pushes.
        if (!fromSyncPass && await SyncInFlightAsync(ct))
        {
            return 0;
        }

        // A Syncing row whose lease has lapsed is recovered here too, so one left behind while campaigns are disabled
        // (the campaign worker does not run then) cannot pause this sweep for good.
        (int unknown, int interrupted) = await MarketingCampaignPipeline.RecoverStaleAsync(
            db, brevo, protector, options.Value, _clock.GetUtcNow().UtcDateTime, logger, ct);
        if (unknown + interrupted > 0)
        {
            logger.LogWarning("Marketing campaign recovery (sweep): sendUnknown={Unknown} interrupted={Interrupted}", unknown, interrupted);
        }

        // A running send rebuilds the list itself; leave it alone.
        if (await LiveSyncingAsync(ct))
        {
            return 0;
        }

        List<string> synced = await db.MarketingConsents.AsNoTracking().Where(c => c.BrevoSyncedAt != null).Select(c => c.Email).ToListAsync(ct);
        if (synced.Count == 0)
        {
            return 0;
        }

        (IReadOnlyList<(Guid ConsentId, string Email)> recipients, _) = await consents.SendableRecipientsAsync(ct);
        HashSet<string> sendable = recipients.Select(r => MarketingEmail.Normalize(r.Email)).ToHashSet(StringComparer.Ordinal);
        int done = 0;
        foreach (string email in synced.Where(e => !sendable.Contains(e)).Take(Batch))
        {
            if (renew is not null && !await renew(ct))
            {
                break;
            }

            try
            {
                await guard.DeleteStaleAsync(email, ct);
            }
            catch (BrevoCampaignException e)
            {
                logger.LogWarning("Brevo contact read or delete failed; the contact stays and is retried on the next pass: code={Code}", e.Code);
                continue;
            }

            done++;
        }

        return done;
    }
}

/// <summary>Runs the erasure outbox and the unsendable-contact sweep on a timer. Runs whether or not campaigns are enabled; inert only while the key is blank.</summary>
public sealed class BrevoErasureWorker(
    IServiceScopeFactory scopes,
    IOptions<MarketingCampaignOptions> options,
    ILogger<BrevoErasureWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Deliberately not gated on Marketing:Campaigns:Enabled: turning campaigns off must never strand an erasure.
        // The worker is inert only while the key is blank (see PassAsync), and then leaves every row in place.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
            using PeriodicTimer timer = new(Tick);
            do
            {
                await PassAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    internal async Task PassAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BrevoCampaignApiKey))
        {
            return;
        }

        try
        {
            using IServiceScope scope = scopes.CreateScope();
            BrevoErasureOutbox outbox = scope.ServiceProvider.GetRequiredService<BrevoErasureOutbox>();
            await outbox.DrainWithBackoffAsync(ct);
            await outbox.DeleteUnsendableAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The Brevo erasure worker failed; retrying on the next tick: {Error}", MailLogRef.DescribeError(ex));
        }
    }
}
