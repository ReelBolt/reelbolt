using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Keeps the names on <c>marketing_consents</c> in line with the accounts (pure DB, no Brevo). For a row with an
/// account the account is the authority (Admin &gt; Account &gt; Form): while the source is null, Form or Account the name
/// follows the account display name, and an unusable account name CLEARS any anonymous Form name. Admin names are never
/// touched. Also clears the names of Withdrawn rows (<see cref="ClearWithdrawnAsync"/>). A conditional update on the old values makes a concurrent admin or form edit win.
/// No name is ever logged or written to an event.
/// </summary>
public sealed class MarketingNameReconciler(InferenceApiDbContext db, TimeProvider? clock = null)
{
    private const int Batch = 500;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Runs one pass; returns the number of rows changed.</summary>
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        int changed = 0;
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        Guid after = Guid.Empty;
        while (true)
        {
            var batch = await (
                from c in db.MarketingConsents.AsNoTracking()
                join u in db.ApplicationUsers.AsNoTracking() on c.UserId equals u.Id
                where c.Id.CompareTo(after) > 0
                    && !u.IsServiceAccount
                    && (c.NameSource == null || c.NameSource == MarketingNameSource.Form || c.NameSource == MarketingNameSource.Account)
                    && (c.Status == MarketingConsentStatus.Pending || c.Status == MarketingConsentStatus.OptedIn)
                orderby c.Id
                select new
                {
                    c.Id,
                    c.Email,
                    c.FirstName,
                    c.LastName,
                    c.NameSource,
                    u.DisplayName
                }).Take(Batch).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                (string? first, string? last) = MarketingName.FromDisplayName(row.DisplayName, row.Email);
                if (first == row.FirstName && last == row.LastName)
                {
                    continue;
                }

                Guid id = row.Id;
                string? oldFirst = row.FirstName;
                string? oldLast = row.LastName;
                MarketingNameSource? oldSource = row.NameSource;
                changed += await db.MarketingConsents
                    .Where(c => c.Id == id && c.FirstName == oldFirst && c.LastName == oldLast && c.NameSource == oldSource)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.FirstName, first)
                        .SetProperty(c => c.LastName, last)
                        .SetProperty(c => c.NameSource, (MarketingNameSource?)MarketingNameSource.Account)
                        .SetProperty(c => c.NameUpdatedAt, (DateTime?)now), ct);
            }

            after = batch[^1].Id;
        }

        changed += await ClearWithdrawnAsync(ct);
        return changed;
    }

    /// <summary>
    /// Data minimisation: a withdrawn contact keeps no name (the source stays). Pure DB and always on: the sweeper
    /// calls it whatever the Brevo, campaign or sync switches say. Returns the number of rows cleared.
    /// </summary>
    public async Task<int> ClearWithdrawnAsync(CancellationToken ct)
    {
        int changed = 0;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        while (true)
        {
            List<Guid> ids = await db.MarketingConsents.AsNoTracking()
                .Where(c => c.Status == MarketingConsentStatus.Withdrawn && (c.FirstName != null || c.LastName != null))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(Batch)
                .ToListAsync(ct);
            if (ids.Count == 0)
            {
                break;
            }

            int cleared = await db.MarketingConsents
                .Where(c => ids.Contains(c.Id) && c.Status == MarketingConsentStatus.Withdrawn)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.FirstName, (string?)null)
                    .SetProperty(c => c.LastName, (string?)null)
                    .SetProperty(c => c.NameUpdatedAt, (DateTime?)now), ct);
            changed += cleared;
            if (cleared == 0)
            {
                break;
            }
        }

        return changed;
    }
}
