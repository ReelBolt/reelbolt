using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>What the guard found out about a Brevo contact, after committing any local consequence.</summary>
public enum BrevoContactState
{
    /// <summary>Brevo has no such contact (404).</summary>
    Absent,

    /// <summary>Present and not blocked.</summary>
    Clear,

    /// <summary>Blocked: consent withdrawn locally (committed).</summary>
    Withdrawn,

    /// <summary>Blocked by a hard bounce only: marked undeliverable locally (committed).</summary>
    Undeliverable,
}

/// <summary>
/// The one place a Brevo block becomes local state, so an unsubscribe is never lost. Brevo's API exposes no
/// blocklist reason, so the contact is read and classified: complaint, then unsubscribe, then hard bounce only,
/// otherwise an unknown reason, which withdraws (the most protective reading; mail resumes only after a new
/// confirmed opt-in). A contact is deleted from Brevo only after this has committed its local state.
/// </summary>
public sealed class BrevoContactGuard(
    InferenceApiDbContext db,
    MarketingConsentService consents,
    IBrevoCampaignClient brevo,
    IOptions<MarketingCampaignOptions> options)
{
    private long ListId => options.Value.BrevoListId;

    /// <summary>The classification rule. Pure.</summary>
    /// <param name="statisticsCount">
    /// When true (the stale delete path), an unsubscribe or complaint in the statistics withdraws even with no block
    /// flag, unless its newest event is older than <paramref name="confirmedAt"/> (a later re-opt-in outranks old
    /// history). A time that cannot be compared withdraws.
    /// </param>
    public static BrevoContactState Decide(
        BrevoContactInfo? info, bool listSaysBlocked, long listId, out MarketingConsentSource source,
        bool statisticsCount = false, DateTime? confirmedAt = null)
    {
        source = MarketingConsentSource.BrevoBlocklist;
        bool blocked = listSaysBlocked || (info is not null && (info.EmailBlacklisted || info.ListUnsubscribed.Contains(listId)));
        if (!blocked)
        {
            if (statisticsCount && info is not null)
            {
                if (info.Complaints && !OlderThanConfirmation(info.ComplaintAt, confirmedAt))
                {
                    source = MarketingConsentSource.EmailComplaint;
                    return BrevoContactState.Withdrawn;
                }

                if (info.Unsubscribed && !OlderThanConfirmation(info.UnsubscribedAt, confirmedAt))
                {
                    source = MarketingConsentSource.BrevoUnsubscribe;
                    return BrevoContactState.Withdrawn;
                }
            }

            return info is null ? BrevoContactState.Absent : BrevoContactState.Clear;
        }

        if (info is { Complaints: true })
        {
            source = MarketingConsentSource.EmailComplaint;
            return BrevoContactState.Withdrawn;
        }

        if (info is { Unsubscribed: true })
        {
            source = MarketingConsentSource.BrevoUnsubscribe;
            return BrevoContactState.Withdrawn;
        }

        if (info is { HardBounces: true })
        {
            return BrevoContactState.Undeliverable;
        }

        return BrevoContactState.Withdrawn;
    }

    private static bool OlderThanConfirmation(DateTime? eventAt, DateTime? confirmedAt)
        => eventAt is { } e && confirmedAt is { } c && e < c;

    /// <summary>
    /// Reads the contact, classifies it and commits the local state. A failed read throws
    /// (<see cref="BrevoCampaignException"/>) and changes nothing.
    /// </summary>
    public async Task<BrevoContactState> ClassifyAsync(string email, bool listSaysBlocked, CancellationToken ct, bool statisticsCount = false)
    {
        BrevoContactInfo? info = await brevo.GetContactAsync(email, ct);
        DateTime? confirmedAt = null;
        if (statisticsCount)
        {
            string normalized = MarketingEmail.Normalize(email);
            confirmedAt = await db.MarketingConsents.AsNoTracking().Where(c => c.Email == normalized).Select(c => c.ConfirmedAt).MaxAsync(ct);
        }

        BrevoContactState state = Decide(info, listSaysBlocked, ListId, out MarketingConsentSource source, statisticsCount, confirmedAt);
        switch (state)
        {
            case BrevoContactState.Withdrawn:
                await consents.WithdrawFromProviderAsync(email, source, ct);
                break;
            case BrevoContactState.Undeliverable:
                await consents.MarkUndeliverableAsync(email, "hard_bounce", ct);
                break;
        }

        return state;
    }

    /// <summary>
    /// Deletes a stale contact: read, classify, commit the local state, only then delete. A failed read means no
    /// delete (the exception propagates and the mark stays). A 404 just clears the sync mark.
    /// </summary>
    public async Task DeleteStaleAsync(string email, CancellationToken ct)
    {
        // The flag as read before the work: a later run may re-flag the row (a re-opt-in claimed by a send) while this
        // pass is at Brevo, and that newer flag must survive. Email is unique, so there is one row.
        DateTime? flagRead = await db.MarketingConsents.AsNoTracking().Where(c => c.Email == email).Select(c => c.BrevoSyncedAt).FirstOrDefaultAsync(ct);
        BrevoContactState state = await ClassifyAsync(email, listSaysBlocked: false, ct, statisticsCount: true);
        if (state != BrevoContactState.Absent)
        {
            await brevo.DeleteContactAsync(email, ct);
        }

        await db.MarketingConsents.Where(c => c.Email == email && c.BrevoSyncedAt == flagRead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.BrevoSyncedAt, (DateTime?)null)
                // Not at Brevo any more: a later re-opt-in is a guarded first add again.
                .SetProperty(c => c.BrevoPushedAt, (DateTime?)null)
                .SetProperty(c => c.BrevoPushAttempts, 0)
                .SetProperty(c => c.BrevoPushErrorCode, (string?)null), ct);
    }
}
