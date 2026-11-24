namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// <c>Marketing:ContactSync:*</c>. The Brevo contact sync worker is on by default but stays inert until the campaign key
/// (<c>Marketing:Campaigns:BrevoCampaignApiKey</c>) and the managed list id are set. It does not need
/// <c>Marketing:Campaigns:Enabled</c>.
/// </summary>
public sealed class MarketingContactSyncOptions
{
    public const string SectionName = "Marketing:ContactSync";

    /// <summary>Env <c>MARKETING_CONTACT_SYNC_ENABLED</c>; default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between worker passes.</summary>
    public int TickSeconds { get; set; } = 60;

    /// <summary>Most contacts pushed (and most stale contacts deleted by a nightly reconcile) in one pass.</summary>
    public int MaxPerTick { get; set; } = 200;

    /// <summary>Seconds between runs of the name reconciler.</summary>
    public int NameReconcileSeconds { get; set; } = 300;

    /// <summary>The UTC hour at which the nightly list reconcile may run (at most once per 20 hours).</summary>
    public int NightlyReconcileHourUtc { get; set; } = 3;

    /// <summary>A failed push is retried after 2, 4, 8 ... minutes, capped at this many.</summary>
    public int PushBackoffMaxMinutes { get; set; } = 60;

    /// <summary>The wait before a failed push may be retried.</summary>
    internal TimeSpan PushBackoff(int attempts)
        => TimeSpan.FromMinutes(Math.Min(Math.Max(1, PushBackoffMaxMinutes), Math.Pow(2, Math.Clamp(attempts, 1, 6))));
}
