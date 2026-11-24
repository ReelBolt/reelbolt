namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>Configuration of the recipe marketplace (<c>Marketplace</c> section). Everything is off by default.</summary>
public sealed class MarketplaceOptions
{
    public const string SectionName = "Marketplace";

    /// <summary>Master switch. Off: every marketplace route answers a bare 404 and no worker does anything.</summary>
    public bool Enabled { get; set; }

    public int MaxPublishesPerUserPerDay { get; set; } = 5;
    public int MaxLivePerOrganization { get; set; } = 50;

    /// <summary>Publishes a user may have refused by the text check per UTC day before further attempts are limited.</summary>
    public int MaxBlockedPublishAttemptsPerUserPerDay { get; set; } = 20;

    /// <summary>Ratings needed before an average is shown.</summary>
    public int MinRatingsForAverage { get; set; } = 5;

    /// <summary>Open reports from distinct users that hide a recipe pending review.</summary>
    public int ReportsAutoHideThreshold { get; set; } = 3;

    public int MaxReportsPerUserPerDay { get; set; } = 10;

    /// <summary>Only reports from accounts at least this old (and with a verified email) count toward <see cref="ReportsAutoHideThreshold"/>.</summary>
    public int MinReporterAccountAgeDays { get; set; } = 7;

    /// <summary>Minutes between preview sweeper runs; 0 turns the sweeper off.</summary>
    public int PreviewSweepIntervalMinutes { get; set; } = 5;

    /// <summary>A version still <c>preparing</c> after this long is re-dispatched once, then marked failed.</summary>
    public int PreviewStuckMinutes { get; set; } = 30;

    /// <summary>Months the moderation log and handled reports are kept before the retention sweep deletes them.</summary>
    public int RetentionMonths { get; set; } = 12;

    /// <summary>Minutes between retention sweeps; 0 turns it off.</summary>
    public int RetentionSweepIntervalMinutes { get; set; } = 1440;

    /// <summary>The consent text version a publisher accepts; bump it when <see cref="MarketplaceConsentText"/> changes.</summary>
    public string ConsentVersion { get; set; } = MarketplaceConsentText.CurrentVersion;
}
