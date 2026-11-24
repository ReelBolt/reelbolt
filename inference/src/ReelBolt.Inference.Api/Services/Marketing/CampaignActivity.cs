using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>The one definition of "a campaign run is in progress", shared by the erasure outbox and the contact sync.</summary>
internal static class CampaignActivity
{
    /// <summary>True while a Syncing row's lease is live. A stale row (a crashed run) is not live.</summary>
    public static Task<bool> LiveSyncingAsync(InferenceApiDbContext db, MarketingCampaignOptions options, DateTime now, CancellationToken ct)
    {
        DateTime cutoff = now.AddMinutes(-Math.Max(0, options.SyncStaleMinutes));
        return db.MarketingCampaigns.AnyAsync(c => c.Status == MarketingCampaignStatus.Syncing && c.UpdatedAt > cutoff, ct);
    }

    /// <summary>True while a campaign is Queued or has a live Syncing lease: the run rebuilds the list itself.</summary>
    public static async Task<bool> ActiveAsync(InferenceApiDbContext db, MarketingCampaignOptions options, DateTime now, CancellationToken ct)
        => await db.MarketingCampaigns.AnyAsync(c => c.Status == MarketingCampaignStatus.Queued, ct)
            || await LiveSyncingAsync(db, options, now, ct);
}
